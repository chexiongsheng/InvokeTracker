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
                var backupPath = _config.AssemblyPath + ".backup";
                File.Copy(_config.AssemblyPath, backupPath, true);
                Console.WriteLine($"Backup created: {backupPath}");

                // Also backup PDB file if it exists
                var pdbPath = Path.ChangeExtension(_config.AssemblyPath, ".pdb");
                if (File.Exists(pdbPath))
                {
                    var pdbBackupPath = pdbPath + ".backup";
                    File.Copy(pdbPath, pdbBackupPath, true);
                    Console.WriteLine($"PDB backup created: {pdbBackupPath}");
                }
            }

            // Load assembly with read/write mode
            var readerParameters = new ReaderParameters
            {
                ReadSymbols = true,
                ReadWrite = string.IsNullOrEmpty(_config.OutputPath)
            };

            using var assembly = AssemblyDefinition.ReadAssembly(_config.AssemblyPath, readerParameters);

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
                // ToList() creates a snapshot to avoid "Collection was modified" exception
                // when we add helper types during processing
                var typesToProcess = module.Types.ToList();
                foreach (var type in typesToProcess)
                {
                    ProcessType(type);
                }
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
            }
            else
            {
                // No OutputPath specified, write directly to original assembly (old behavior)
                assembly.Write(_config.AssemblyPath, writerParameters);
                Console.WriteLine($"  - Modified assembly: {_config.AssemblyPath}");
            }
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

                // For generic types, use a helper type to store counters
                TypeDefinition targetType;
                if (type.HasGenericParameters)
                {
                    targetType = GetOrCreateHelperType(type);
                }
                else
                {
                    targetType = type;
                }

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
                Console.WriteLine($"  ✓ {type.Name}.{method.Name} -> {targetType.Name}.{fieldName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Failed to instrument {type.Name}.{method.Name}: {ex.Message}");
            }
        }

        private TypeDefinition GetOrCreateHelperType(TypeDefinition genericType)
        {
            // Sanitize the generic type name: replace backtick with underscore
            // e.g., "StaticTranslate`1" -> "StaticTranslate_1"
            //       "Action`2" -> "Action_2"
            // This preserves generic parameter count to avoid naming conflicts
            var sanitizedName = genericType.Name.Replace('`', '_');
            
            var helperTypeName = sanitizedName + HelperTypeSuffix;
            var helperTypeFullName = genericType.Namespace + "." + helperTypeName;

            // Check if we already created this helper type
            if (_helperTypes.TryGetValue(helperTypeFullName, out var existingHelper))
            {
                return existingHelper;
            }

            // Check if helper type already exists in the module
            var existingType = genericType.Module.Types.FirstOrDefault(t => t.FullName == helperTypeFullName);
            if (existingType != null)
            {
                _helperTypes[helperTypeFullName] = existingType;
                return existingType;
            }

            // Create new helper type (non-generic, public but marked as compiler-generated)
            var helperType = new TypeDefinition(
                genericType.Namespace,
                helperTypeName,
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit,
                genericType.Module.TypeSystem.Object
            );

            // Add the helper type to the module
            genericType.Module.Types.Add(helperType);
            _helperTypes[helperTypeFullName] = helperType;

            Console.WriteLine($"  → Created helper type: {helperTypeName} for generic type {genericType.Name}");

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
    }
}
