using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace InvokeTracker
{
    /// <summary>
    /// Core IL weaver that instruments methods with invoke counters
    /// </summary>
    public class AssemblyWeaver
    {
        private readonly WeaverConfig _config;
        private int _instrumentedMethodCount = 0;
        private int _addedFieldCount = 0;
        private Dictionary<string, TypeDefinition> _helperTypes = new Dictionary<string, TypeDefinition>();
        private const string HelperTypeSuffix = "_InvokeCounters";
        private CallerSideInstrumentationContext _callerSideContext = new CallerSideInstrumentationContext();

        public AssemblyWeaver(WeaverConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Weave the target assembly with invoke tracking instrumentation
        /// </summary>
        public void Weave()
        {
            if (!File.Exists(_config.AssemblyPath))
            {
                throw new FileNotFoundException($"Assembly not found: {_config.AssemblyPath}");
            }

            Console.WriteLine($"Loading assembly: {_config.AssemblyPath}");

            // Create backup if requested
            if (_config.CreateBackup)
            {
                CreateBackup(_config.AssemblyPath, _config.BackupDirectory);
            }

            // Load assembly with read/write mode
            var resolver = new DefaultAssemblyResolver();
            
            // Add search directories for assembly resolution
            var assemblyDir = Path.GetDirectoryName(_config.AssemblyPath);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                resolver.AddSearchDirectory(assemblyDir);
            }
            
            Console.WriteLine($"search directory count: {_config.SearchDirectories.Count}");
            // Add additional search directories from config
            foreach (var searchDir in _config.SearchDirectories)
            {
                if (!string.IsNullOrEmpty(searchDir) && Directory.Exists(searchDir))
                {
                    resolver.AddSearchDirectory(searchDir);
                    Console.WriteLine($"Added search directory: {searchDir}");
                }
                else
                {
                    Console.WriteLine($"Invalid search directory: {searchDir}");
                }
            }
            
            var readerParameters = new ReaderParameters
            {
                ReadSymbols = true,
                ReadWrite = string.IsNullOrEmpty(_config.OutputPath),
                AssemblyResolver = resolver
            };

            // Detect original PDB file name before loading
            string? originalPdbPath = DetectOriginalPdbPath(_config.AssemblyPath);
            if (originalPdbPath != null)
            {
                Console.WriteLine($"Detected original PDB: {originalPdbPath}");
            }

            AssemblyDefinition assembly;
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(_config.AssemblyPath, readerParameters);
            }
            catch (Exception ex) when (ex.Message.Contains("Symbols were found but are not matching"))
            {
                Console.WriteLine($"Warning: PDB symbols don't match assembly, loading without symbols...");
                readerParameters.ReadSymbols = false;
                assembly = AssemblyDefinition.ReadAssembly(_config.AssemblyPath, readerParameters);
            }

            using (assembly)
            {
                Console.WriteLine($"Processing assembly: {assembly.Name.Name}");

            // Check if assembly is already instrumented
            bool alreadyInstrumented = false;
            foreach (var module in assembly.Modules)
            {
                foreach (var type in module.Types)
                {
                    if (type.Fields.Any(f => f.Name.StartsWith(_config.FieldPrefix)))
                    {
                        alreadyInstrumented = true;
                        break;
                    }
                }
                if (alreadyInstrumented) break;
            }

            if (alreadyInstrumented)
            {
                Console.WriteLine($"\n⚠️ Assembly is already instrumented!");
                Console.WriteLine($"  - Found fields with prefix: {_config.FieldPrefix}");
                Console.WriteLine($"  - Skipping instrumentation to avoid duplicate counters.");
                Console.WriteLine($"\n💡 If you want to re-instrument:");
                Console.WriteLine($"  1. Restore from backup (.backup file)");
                Console.WriteLine($"  2. Or use 'Restore All from Backup' in Unity Editor");
                Console.WriteLine($"  3. Then run instrumentation again");
                return; // Exit without error
            }

            // Process all types (collect first to avoid collection modification during enumeration)
            foreach (var module in assembly.Modules)
            {
                // First pass: Identify methods that need caller-side instrumentation
                Console.WriteLine("\n=== First Pass: Identifying methods for caller-side instrumentation ===");
                var typesToScan = module.Types.ToList();
                foreach (var type in typesToScan)
                {
                    IdentifyCallerSideInstrumentationMethods(type);
                }

                // First pass: Scan all call sites
                Console.WriteLine("\n=== First Pass: Scanning call sites ===");
                foreach (var type in typesToScan)
                {
                    ScanMethodCallSites(type);
                }

                Console.WriteLine($"\n=== Summary ===");
                Console.WriteLine($"  - Methods needing caller-side instrumentation: {_callerSideContext.MethodsNeedingInstrumentation.Count}");
                Console.WriteLine($"  - Total call sites found: {_callerSideContext.CallSites.Values.Sum(list => list.Count)}");

                // Second pass: Process types and instrument methods
                Console.WriteLine("\n=== Second Pass: Instrumenting methods ===");
                // ToList() creates a snapshot to avoid "Collection was modified" exception
                // when we add helper types during processing
                var typesToProcess = module.Types.ToList();
                foreach (var type in typesToProcess)
                {
                    ProcessType(type);
                }

                // Second pass: Instrument call sites
                Console.WriteLine("\n=== Second Pass: Instrumenting call sites ===");
                InstrumentCallSites();
            }

            // Save the modified assembly
            var writerParameters = new WriterParameters
            {
                WriteSymbols = readerParameters.ReadSymbols
            };

            Console.WriteLine($"\nInstrumentation complete!");
            Console.WriteLine($"  - Methods instrumented: {_instrumentedMethodCount}");
            Console.WriteLine($"  - Counter fields added: {_addedFieldCount}");

            // If OutputPath is specified, write directly to it (caller will handle copy)
            if (!string.IsNullOrEmpty(_config.OutputPath))
            {
                assembly.Write(_config.OutputPath, writerParameters);
                Console.WriteLine($"  - Output file: {_config.OutputPath}");
                
                // If we detected a non-standard PDB name, rename the generated PDB
                if (originalPdbPath != null && writerParameters.WriteSymbols)
                {
                    RenameGeneratedPdb(_config.OutputPath, originalPdbPath);
                }
            }
            else
            {
                // No OutputPath specified, write directly to original assembly (old behavior)
                assembly.Write(_config.AssemblyPath, writerParameters);
                Console.WriteLine($"  - Modified assembly: {_config.AssemblyPath}");
                
                // If we detected a non-standard PDB name, rename the generated PDB
                if (originalPdbPath != null && writerParameters.WriteSymbols)
                {
                    RenameGeneratedPdb(_config.AssemblyPath, originalPdbPath);
                }
            }
            } // end using (assembly)
        }

        private void ProcessType(TypeDefinition type)
        {
            // Skip compiler-generated types unless configured
            if (!_config.InstrumentCompilerGenerated && IsCompilerGenerated(type))
            {
                return;
            }

            // Check namespace filters
            if (!ShouldProcessType(type))
            {
                return;
            }

            // Process nested types recursively
            foreach (var nestedType in type.NestedTypes)
            {
                ProcessType(nestedType);
            }

            // Process methods in this type
            var methodsToInstrument = type.Methods
                .Where(m => ShouldInstrumentMethod(m))
                .ToList();

            foreach (var method in methodsToInstrument)
            {
                InstrumentMethod(type, method);
            }
        }

        private bool ShouldProcessType(TypeDefinition type)
        {
            var fullName = type.FullName;

            // Check exclude list first
            foreach (var exclude in _config.ExcludeNamespaces)
            {
                if (fullName.StartsWith(exclude))
                {
                    return false;
                }
            }

            // If include list is empty, include all (except excluded)
            if (_config.IncludeNamespaces.Count == 0)
            {
                return true;
            }

            // Check include list
            foreach (var include in _config.IncludeNamespaces)
            {
                if (fullName.StartsWith(include))
                {
                    return true;
                }
            }

            return false;
        }

        private bool ShouldInstrumentMethod(MethodDefinition method)
        {
            // Skip abstract methods
            if (method.IsAbstract)
                return false;

            // Skip methods without body
            if (!method.HasBody)
                return false;

            // Skip compiler-generated unless configured
            if (!_config.InstrumentCompilerGenerated && IsCompilerGenerated(method))
                return false;

            // Skip property getters/setters (optional - you can change this)
            // if (method.IsGetter || method.IsSetter)
            //     return false;

            return true;
        }

        private void InstrumentMethod(TypeDefinition type, MethodDefinition method)
        {
            try
            {
                // Generate field name for this method
                var fieldName = GenerateFieldName(method);

                // Use a helper type to store counters for all types
                TypeDefinition targetType = GetOrCreateHelperType(type);

                // Check if field already exists
                var counterField = targetType.Fields.FirstOrDefault(f => f.Name == fieldName);
                
                if (counterField == null)
                {
                    // Add static uint field to the target type
                    counterField = new FieldDefinition(
                        fieldName,
                        FieldAttributes.Public | FieldAttributes.Static,
                        method.Module.TypeSystem.UInt32
                    );
                    targetType.Fields.Add(counterField);
                    _addedFieldCount++;
                }

                // Inject IL code at method entry to increment the counter
                InjectCounterIncrement(method, counterField);

                _instrumentedMethodCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Failed to instrument {type.Name}.{method.Name}: {ex.Message}");
            }
        }

        private TypeDefinition GetOrCreateHelperType(TypeDefinition type)
        {
            // Sanitize the type name: replace backtick with underscore for generic types
            // e.g., "StaticTranslate`1" -> "StaticTranslate_1"
            //       "Action`2" -> "Action_2"
            // For non-generic types, the name remains unchanged
            // This preserves generic parameter count to avoid naming conflicts
            var sanitizedName = type.Name.Replace('`', '_');
            
            var helperTypeName = sanitizedName + HelperTypeSuffix;
            var helperTypeFullName = type.Namespace + "." + helperTypeName;

            // Check if we already created this helper type
            if (_helperTypes.TryGetValue(helperTypeFullName, out var existingHelper))
            {
                return existingHelper;
            }

            // Check if helper type already exists in the module
            var existingType = type.Module.Types.FirstOrDefault(t => t.FullName == helperTypeFullName);
            if (existingType != null)
            {
                _helperTypes[helperTypeFullName] = existingType;
                return existingType;
            }

            // Create new helper type (non-generic, public but marked as compiler-generated)
            var helperType = new TypeDefinition(
                type.Namespace,
                helperTypeName,
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit,
                type.Module.TypeSystem.Object
            );

            // Add the helper type to the module
            type.Module.Types.Add(helperType);
            _helperTypes[helperTypeFullName] = helperType;

            Console.WriteLine($"  → Created helper type: {helperTypeName} for type {type.Name}");

            return helperType;
        }

        private string GenerateFieldName(MethodDefinition method)
        {
            // Sanitize method name (remove special characters)
            var sanitizedName = method.Name
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace(".", "_")
                .Replace("|", "_");

            return $"{_config.FieldPrefix}{sanitizedName}";
        }

        private static FieldReference GetFieldRef(FieldDefinition definition)
        {
            if (definition.DeclaringType.HasGenericParameters)
            {
                var declaringType = new GenericInstanceType(definition.DeclaringType);
                foreach (var parameter in definition.DeclaringType.GenericParameters)
                {
                    declaringType.GenericArguments.Add(parameter);
                }
                return new FieldReference(definition.Name, definition.FieldType, declaringType);
            }

            return definition;
        }

        private void InjectCounterIncrement(MethodDefinition method, FieldDefinition counterField)
        {
            var il = method.Body.GetILProcessor();
            var firstInstruction = method.Body.Instructions[0];

            var fieldRef = GetFieldRef(counterField);

            var loadField = il.Create(OpCodes.Ldsfld, fieldRef);
            var loadOne = il.Create(OpCodes.Ldc_I4_1);
            var add = il.Create(OpCodes.Add);
            var storeField = il.Create(OpCodes.Stsfld, fieldRef);

            // Insert at the beginning of the method
            il.InsertBefore(firstInstruction, loadField);
            il.InsertAfter(loadField, loadOne);
            il.InsertAfter(loadOne, add);
            il.InsertAfter(add, storeField);

            // Update offsets
            method.Body.OptimizeMacros();
        }

        private bool IsCompilerGenerated(TypeDefinition type)
        {
            return type.Name.Contains("<") || 
                   type.Name.Contains(">") ||
                   type.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute");
        }

        private bool IsCompilerGenerated(MethodDefinition method)
        {
            return method.Name.Contains("<") || 
                   method.Name.Contains(">") ||
                   method.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute");
        }

        /// <summary>
        /// Create backup of assembly file with path record
        /// </summary>
        private void CreateBackup(string assemblyPath, string? backupDirectory)
        {
            // Determine backup directory
            string backupDir;
            if (!string.IsNullOrEmpty(backupDirectory))
            {
                backupDir = backupDirectory;
                // Create backup directory if it doesn't exist
                if (!Directory.Exists(backupDir))
                {
                    Directory.CreateDirectory(backupDir);
                    Console.WriteLine($"Created backup directory: {backupDir}");
                }
            }
            else
            {
                // Use same directory as assembly
                backupDir = Path.GetDirectoryName(assemblyPath) ?? "";
            }

            // Backup DLL file
            var fileName = Path.GetFileName(assemblyPath);
            var backupPath = Path.Combine(backupDir, fileName + ".backup");
            File.Copy(assemblyPath, backupPath, true);
            Console.WriteLine($"Backup created: {backupPath}");

            // Create path record file
            var pathRecordFile = backupPath + ".txt";
            File.WriteAllText(pathRecordFile, assemblyPath);
            Console.WriteLine($"Path record created: {pathRecordFile}");

            // Also backup PDB file if it exists
            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (File.Exists(pdbPath))
            {
                var pdbFileName = Path.GetFileName(pdbPath);
                var pdbBackupPath = Path.Combine(backupDir, pdbFileName + ".backup");
                File.Copy(pdbPath, pdbBackupPath, true);
                Console.WriteLine($"PDB backup created: {pdbBackupPath}");

                // Create path record for PDB
                var pdbPathRecordFile = pdbBackupPath + ".txt";
                File.WriteAllText(pdbPathRecordFile, pdbPath);
                Console.WriteLine($"PDB path record created: {pdbPathRecordFile}");
            }
        }

        /// <summary>
        /// Detect the original PDB file path for the assembly
        /// Checks both standard (xxx.pdb) and non-standard (xxx.dll.pdb) naming conventions
        /// </summary>
        private string? DetectOriginalPdbPath(string assemblyPath)
        {
            // Check standard naming: xxx.dll -> xxx.pdb
            var standardPdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (File.Exists(standardPdbPath))
            {
                return standardPdbPath;
            }

            // Check non-standard naming: xxx.dll -> xxx.dll.pdb
            var nonStandardPdbPath = assemblyPath + ".pdb";
            if (File.Exists(nonStandardPdbPath))
            {
                return nonStandardPdbPath;
            }

            return null;
        }

        /// <summary>
        /// Rename the generated PDB file to match the original naming convention
        /// Cecil always generates xxx.dll.pdb, but we may need xxx.pdb
        /// </summary>
        private void RenameGeneratedPdb(string assemblyPath, string originalPdbPath)
        {
            // Cecil generates: xxx.dll.pdb
            var cecilGeneratedPdb = assemblyPath + ".pdb";
            
            // Get the original PDB file name
            var originalPdbFileName = Path.GetFileName(originalPdbPath);
            var standardPdbFileName = Path.GetFileName(Path.ChangeExtension(assemblyPath, ".pdb"));

            // If original uses standard naming (xxx.pdb) but Cecil generated xxx.dll.pdb
            if (originalPdbFileName == standardPdbFileName && File.Exists(cecilGeneratedPdb))
            {
                var targetPdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
                
                // Delete old PDB if exists
                if (File.Exists(targetPdbPath))
                {
                    File.Delete(targetPdbPath);
                }
                
                // Rename Cecil's generated PDB to match original naming
                File.Move(cecilGeneratedPdb, targetPdbPath);
                Console.WriteLine($"  - Renamed PDB: {Path.GetFileName(cecilGeneratedPdb)} -> {Path.GetFileName(targetPdbPath)}");
            }
        }

        /// <summary>
        /// Identify methods that need caller-side instrumentation
        /// (abstract methods, interface methods, extern methods, etc.)
        /// </summary>
        private void IdentifyCallerSideInstrumentationMethods(TypeDefinition type)
        {
            // Skip compiler-generated types unless configured
            if (!_config.InstrumentCompilerGenerated && IsCompilerGenerated(type))
            {
                return;
            }

            // Check namespace filters
            if (!ShouldProcessType(type))
            {
                return;
            }

            // Process nested types recursively
            foreach (var nestedType in type.NestedTypes)
            {
                IdentifyCallerSideInstrumentationMethods(nestedType);
            }

            // Find methods that need caller-side instrumentation
            foreach (var method in type.Methods)
            {
                // Skip compiler-generated unless configured
                if (!_config.InstrumentCompilerGenerated && IsCompilerGenerated(method))
                    continue;

                // Check if method needs caller-side instrumentation
                bool needsCallerSide = false;
                string reason = "";

                if (method.IsAbstract)
                {
                    needsCallerSide = true;
                    reason = "abstract method";
                }
                else if (!method.HasBody)
                {
                    needsCallerSide = true;
                    if (type.IsInterface)
                        reason = "interface method";
                    else if (method.IsPInvokeImpl)
                        reason = "extern/P/Invoke method";
                    else
                        reason = "method without body";
                }

                if (needsCallerSide)
                {
                    // Create helper type and counter field for this method
                    var helperType = GetOrCreateHelperType(type);
                    var fieldName = GenerateFieldName(method);
                    
                    var counterField = helperType.Fields.FirstOrDefault(f => f.Name == fieldName);
                    if (counterField == null)
                    {
                        counterField = new FieldDefinition(
                            fieldName,
                            FieldAttributes.Public | FieldAttributes.Static,
                            method.Module.TypeSystem.UInt32
                        );
                        helperType.Fields.Add(counterField);
                        _addedFieldCount++;
                    }

                    // Add to context
                    _callerSideContext.AddMethod(method, helperType, counterField);
                    
                    Console.WriteLine($"  ✓ Identified: {type.FullName}::{method.Name} ({reason})");
                }
            }
        }

        /// <summary>
        /// Scan all method bodies to find call sites that need instrumentation
        /// </summary>
        private void ScanMethodCallSites(TypeDefinition type)
        {
            // Skip compiler-generated types unless configured
            if (!_config.InstrumentCompilerGenerated && IsCompilerGenerated(type))
            {
                return;
            }

            // Process nested types recursively
            foreach (var nestedType in type.NestedTypes)
            {
                ScanMethodCallSites(nestedType);
            }

            // Scan all methods with bodies
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                // Skip compiler-generated unless configured
                if (!_config.InstrumentCompilerGenerated && IsCompilerGenerated(method))
                    continue;

                // Scan IL instructions for Call and Callvirt
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                    {
                        var callee = instruction.Operand as MethodReference;
                        if (callee != null && _callerSideContext.NeedsCallerSideInstrumentation(callee))
                        {
                            _callerSideContext.AddCallSite(method, instruction, callee);
                            Console.WriteLine($"  → Found call site: {method.FullName} calls {callee.FullName}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Instrument all identified call sites
        /// </summary>
        private void InstrumentCallSites()
        {
            int instrumentedCallSites = 0;

            foreach (var kvp in _callerSideContext.CallSites)
            {
                var methodSignature = kvp.Key;
                var callSites = kvp.Value;

                var methodInfo = _callerSideContext.GetMethodInfo(callSites[0].Callee);
                if (methodInfo == null)
                {
                    Console.WriteLine($"  ✗ Warning: Method info not found for {methodSignature}");
                    continue;
                }

                foreach (var callSite in callSites)
                {
                    try
                    {
                        InstrumentCallSite(callSite, methodInfo);
                        instrumentedCallSites++;
                        Console.WriteLine($"  ✓ Instrumented call site in {callSite.Caller.FullName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  ✗ Failed to instrument call site in {callSite.Caller.FullName}: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"\n  - Total call sites instrumented: {instrumentedCallSites}");
        }

        /// <summary>
        /// Instrument a single call site by inserting counter increment before the call
        /// </summary>
        private void InstrumentCallSite(CallerSideInstrumentationContext.CallSite callSite, CallerSideInstrumentationContext.MethodInfo methodInfo)
        {
            var caller = callSite.Caller;
            var callInstruction = callSite.CallInstruction;
            var counterField = methodInfo.CounterField;

            var il = caller.Body.GetILProcessor();

            // Import the counter field reference if it's from another assembly
            FieldReference fieldRef;
            if (counterField.DeclaringType.Module != caller.Module)
            {
                // Cross-assembly reference - need to import
                var importedType = caller.Module.ImportReference(counterField.DeclaringType);
                fieldRef = new FieldReference(counterField.Name, counterField.FieldType, importedType);
            }
            else
            {
                fieldRef = GetFieldRef(counterField);
            }

            // Create instructions to increment counter
            var loadField = il.Create(OpCodes.Ldsfld, fieldRef);
            var loadOne = il.Create(OpCodes.Ldc_I4_1);
            var add = il.Create(OpCodes.Add);
            var storeField = il.Create(OpCodes.Stsfld, fieldRef);

            // Insert before the call instruction
            il.InsertBefore(callInstruction, loadField);
            il.InsertAfter(loadField, loadOne);
            il.InsertAfter(loadOne, add);
            il.InsertAfter(add, storeField);

            // Update offsets and exception handlers
            caller.Body.OptimizeMacros();
        }
    }
}
