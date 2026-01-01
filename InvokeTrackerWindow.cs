using UnityEngine;
using UnityEditor;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace InvokeTracker.Unity.Editor
{
    /// <summary>
    /// Unity Editor window for managing invoke tracking instrumentation
    /// </summary>
    public class InvokeTrackerWindow : EditorWindow
    {
        private InvokeTrackerConfig config;
        private Vector2 scrollPosition;

        [MenuItem("Tools/Invoke Tracker")]
        public static void ShowWindow()
        {
            var window = GetWindow<InvokeTrackerWindow>("Invoke Tracker");
            window.minSize = new Vector2(400, 500);
            window.AutoLoadConfig();
        }

        /// <summary>
        /// Automatically find and load the first available config in the project
        /// </summary>
        private void AutoLoadConfig()
        {
            if (config != null)
                return;

            // Search for all InvokeTrackerConfig assets in the project
            var guids = AssetDatabase.FindAssets("t:InvokeTrackerConfig");
            
            if (guids.Length > 0)
            {
                // Load the first config found
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                config = AssetDatabase.LoadAssetAtPath<InvokeTrackerConfig>(path);
                
                if (config != null)
                {
                    UnityEngine.Debug.Log($"Auto-loaded config: {config.name} from {path}");
                }
            }
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            EditorGUILayout.LabelField("Unity Invoke Tracker", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            DrawConfigurationMode();

            EditorGUILayout.EndScrollView();
        }

        private void DrawConfigurationMode()
        {
            EditorGUILayout.LabelField("Configuration Asset", EditorStyles.boldLabel);
            
            if (config != null)
            {
                EditorGUILayout.HelpBox($"Using configuration: {config.name}", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("No configuration loaded. Select or create one below.", MessageType.Warning);
            }
            
            var newConfig = (InvokeTrackerConfig)EditorGUILayout.ObjectField(
                "Config Asset", 
                config, 
                typeof(InvokeTrackerConfig), 
                false);

            if (newConfig != config)
            {
                config = newConfig;
            }

            EditorGUILayout.Space();

            if (config == null)
            {
                EditorGUILayout.HelpBox("Please select or create a configuration asset.", MessageType.Warning);
                
                if (GUILayout.Button("Create New Config", GUILayout.Height(30)))
                {
                    CreateNewConfig();
                }

                return;
            }

            // Show config summary
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Configuration Summary", EditorStyles.boldLabel);
            
            var enabledAssemblies = config.GetEnabledAssemblyPaths();
            EditorGUILayout.LabelField($"Enabled Assemblies: {enabledAssemblies.Count}");
            EditorGUILayout.LabelField($"Include Namespaces: {(string.IsNullOrEmpty(config.includeNamespaces) ? "All" : config.includeNamespaces)}");
            EditorGUILayout.LabelField($"Exclude Namespaces: {config.excludeNamespaces}");
            EditorGUILayout.LabelField($"Field Prefix: {config.fieldPrefix}");
            EditorGUILayout.LabelField($"Create Backup: {config.createBackup}");
            
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();

            // Quick edit button
            if (GUILayout.Button("Edit Configuration", GUILayout.Height(25)))
            {
                Selection.activeObject = config;
                EditorGUIUtility.PingObject(config);
            }

            EditorGUILayout.Space();

            // Validation
            if (!config.Validate(out string errorMessage))
            {
                EditorGUILayout.HelpBox($"Configuration Error: {errorMessage}", MessageType.Error);
            }

            // Action buttons
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            
            GUI.enabled = config.Validate(out _);
            if (GUILayout.Button($"Instrument All Assemblies ({enabledAssemblies.Count})", GUILayout.Height(30)))
            {
                InstrumentWithConfig(config);
            }
            GUI.enabled = true;

            if (GUILayout.Button("Restore All from Backup", GUILayout.Height(30)))
            {
                RestoreWithConfig(config);
            }

            EditorGUILayout.Space();

            // Quick actions
            EditorGUILayout.LabelField("Quick Actions", EditorStyles.boldLabel);
            
            if (GUILayout.Button("Open Assembly Folder"))
            {
                var folder = Path.Combine(Application.dataPath, "..", "Library", "ScriptAssemblies");
                EditorUtility.RevealInFinder(folder);
            }

            if (GUILayout.Button("Recompile Scripts"))
            {
                AssetDatabase.Refresh();
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            }
        }

        private void CreateNewConfig()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "Create Invoke Tracker Config",
                "InvokeTrackerConfig",
                "asset",
                "Create a new configuration asset");

            if (!string.IsNullOrEmpty(path))
            {
                var newConfig = CreateInstance<InvokeTrackerConfig>();
                AssetDatabase.CreateAsset(newConfig, path);
                AssetDatabase.SaveAssets();
                config = newConfig;
                Selection.activeObject = newConfig;
                EditorGUIUtility.PingObject(newConfig);
            }
        }

        private void InstrumentWithConfig(InvokeTrackerConfig cfg)
        {
            var fullWeaverPath = Path.Combine(Application.dataPath, "..", cfg.weaverToolPath);

            if (!File.Exists(fullWeaverPath))
            {
                UnityEngine.Debug.LogError($"Error: Weaver tool not found at {fullWeaverPath}");
                return;
            }

            var enabledAssemblies = cfg.GetEnabledAssemblyPaths();
            if (enabledAssemblies.Count == 0)
            {
                UnityEngine.Debug.LogError("Error: No enabled assemblies!");
                EditorUtility.DisplayDialog("Error", "Please enable at least one assembly to instrument.", "OK");
                return;
            }

            UnityEngine.Debug.Log($"=== Instrumenting {enabledAssemblies.Count} assemblies ===");

            int successCount = 0;
            int failCount = 0;

            foreach (var assemblyPath in enabledAssemblies)
            {
                var fullAssemblyPath = Path.Combine(Application.dataPath, "..", assemblyPath);

                if (!File.Exists(fullAssemblyPath))
                {
                    UnityEngine.Debug.LogWarning($"[SKIP] {assemblyPath} - File not found");
                    failCount++;
                    continue;
                }

                UnityEngine.Debug.Log($"--- Processing: {assemblyPath} ---");

                // Use .instrumented as temporary output file to avoid locking issues
                var instrumentedPath = fullAssemblyPath + ".instrumented";

                // Build command-line arguments
                var args = $"\"{fullAssemblyPath}\" --output=\"{instrumentedPath}\"";
                
                if (!string.IsNullOrEmpty(cfg.includeNamespaces))
                {
                    args += $" --include={cfg.includeNamespaces}";
                }
                
                if (!string.IsNullOrEmpty(cfg.excludeNamespaces))
                {
                    args += $" --exclude={cfg.excludeNamespaces}";
                }
                
                if (!string.IsNullOrEmpty(cfg.fieldPrefix))
                {
                    args += $" --prefix={cfg.fieldPrefix}";
                }
                
                if (!cfg.createBackup)
                {
                    args += " --no-backup";
                }
                
                if (cfg.instrumentCompilerGenerated)
                {
                    args += " --instrument-compiler-generated";
                }

                // Add search directories for assembly dependencies
                var searchDirs = GetAssemblySearchDirectories(fullAssemblyPath);
                foreach (var dir in searchDirs)
                {
                    args += $" --search-dir=\"{dir}\"";
                }

                // Add backup directory (Assets同级目录/InvokeTrackerBackupFiles)
                var backupDir = GetBackupDirectory();
                args += $" --backup-dir=\"{backupDir}\"";

                // Run the weaver tool
                try
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = fullWeaverPath,
                            Arguments = args,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            WorkingDirectory = Path.GetDirectoryName(fullWeaverPath)
                        }
                    };

                    process.Start();
                    
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    
                    process.WaitForExit();

                    if (!string.IsNullOrEmpty(output))
                    {
                        UnityEngine.Debug.Log(output);
                    }
                    
                    if (!string.IsNullOrEmpty(error))
                    {
                        UnityEngine.Debug.LogError("Errors:\n" + error);
                    }

                    // Check if assembly was already instrumented
                    if (process.ExitCode == 0 && output.Contains("already instrumented"))
                    {
                        UnityEngine.Debug.Log($"[SKIPPED] {assemblyPath} - Already instrumented");
                        successCount++; // Count as success (not an error)
                        continue; // Skip the copy logic
                    }

                    if (process.ExitCode == 0)
                    {
                        // Weaver succeeded, now try to copy the instrumented file back
                        if (File.Exists(instrumentedPath))
                        {
                            try
                            {
                                // Try to copy the instrumented file to original location
                                File.Copy(instrumentedPath, fullAssemblyPath, true);
                                
                                // Also copy PDB file if it exists
                                // Check both standard naming (xxx.pdb) and non-standard naming (xxx.dll.pdb)
                                var standardPdbPath = Path.ChangeExtension(instrumentedPath, ".pdb");
                                var nonStandardPdbPath = instrumentedPath + ".pdb";
                                var targetPdbPath = Path.ChangeExtension(fullAssemblyPath, ".pdb");
                                var targetNonStandardPdbPath = fullAssemblyPath + ".pdb";
                                
                                if (File.Exists(standardPdbPath))
                                {
                                    File.Copy(standardPdbPath, targetPdbPath, true);
                                    File.Delete(standardPdbPath);
                                    UnityEngine.Debug.Log($"  - Copied PDB: {Path.GetFileName(standardPdbPath)} -> {Path.GetFileName(targetPdbPath)}");
                                }
                                else if (File.Exists(nonStandardPdbPath))
                                {
                                    File.Copy(nonStandardPdbPath, targetPdbPath, true);
                                    File.Delete(nonStandardPdbPath);
                                    UnityEngine.Debug.Log($"  - Copied PDB: {Path.GetFileName(nonStandardPdbPath)} -> {Path.GetFileName(targetPdbPath)}");
                                }
                                
                                // Clean up any residual .dll.pdb file at target location
                                if (File.Exists(targetNonStandardPdbPath) && targetNonStandardPdbPath != targetPdbPath)
                                {
                                    File.Delete(targetNonStandardPdbPath);
                                    UnityEngine.Debug.Log($"  - Cleaned up residual: {Path.GetFileName(targetNonStandardPdbPath)}");
                                }
                                
                                // Success! Clean up the instrumented file
                                File.Delete(instrumentedPath);
                                
                                UnityEngine.Debug.Log($"[SUCCESS] {assemblyPath}");
                                successCount++;
                            }
                            catch (IOException ex) when (ex.Message.Contains("being used by another process"))
                            {
                                // File is locked by Unity Editor
                                var warningMsg = $"⚠️ WARNING: Cannot copy to {assemblyPath}\n" +
                                    $"The file is currently locked by Unity Editor.\n" +
                                    $"\n📁 Instrumented file saved at:\n" +
                                    $"   {instrumentedPath}\n" +
                                    $"\n💡 Manual Steps:\n" +
                                    $"   1. Close Unity Editor\n" +
                                    $"   2. Copy the .instrumented file to original location:\n" +
                                    $"      From: {instrumentedPath}\n" +
                                    $"      To:   {fullAssemblyPath}\n" +
                                    $"   3. Delete the .instrumented file\n" +
                                    $"   4. Reopen Unity Editor\n" +
                                    $"\n[PARTIAL] {assemblyPath} - Instrumented but not copied (file locked)";
                                UnityEngine.Debug.LogWarning(warningMsg);
                                
                                // Count as partial success
                                successCount++;
                            }
                            catch (System.Exception ex)
                            {
                                UnityEngine.Debug.LogError($"[FAILED] {assemblyPath} - Copy error: {ex.Message}");
                                failCount++;
                            }
                        }
                        else
                        {
                            UnityEngine.Debug.LogError($"[FAILED] {assemblyPath} - Instrumented file not found");
                            failCount++;
                        }
                    }
                    else
                    {
                        UnityEngine.Debug.LogError($"[FAILED] {assemblyPath} - Exit code: {process.ExitCode}");
                        failCount++;
                    }
                }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogError($"[EXCEPTION] {assemblyPath}: {ex.Message}");
                    failCount++;
                }
            }

            var summary = $"\n=== Summary ===\n" +
                $"Total: {enabledAssemblies.Count}\n" +
                $"Success: {successCount}\n" +
                $"Failed: {failCount}";
            UnityEngine.Debug.Log(summary);

            if (failCount == 0)
            {
                UnityEngine.Debug.Log($"All {successCount} assemblies instrumented successfully!");
                EditorUtility.DisplayDialog("Success", 
                    $"Successfully instrumented {successCount} assemblies!", "OK");
            }
            else
            {
                UnityEngine.Debug.LogWarning($"Instrumentation completed with {failCount} failures. Check Console for details.");
                EditorUtility.DisplayDialog("Partial Success", 
                    $"Success: {successCount}\nFailed: {failCount}\n\nCheck Console for details.", "OK");
            }
        }

        /// <summary>
        /// Get search directories for assembly dependencies
        /// </summary>
        private List<string> GetAssemblySearchDirectories(string assemblyPath)
        {
            var searchDirs = new HashSet<string>();

            // Add the assembly's own directory
            var assemblyDir = Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                searchDirs.Add(Path.GetFullPath(assemblyDir));
            }

            // Get directories from all currently loaded assemblies
            // This ensures we find UnityEngine.CoreModule and other dependencies
            try
            {
                var loadedAssemblies = System.AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in loadedAssemblies)
                {
                    try
                    {
                        // Skip dynamic assemblies (they don't have a location)
                        if (assembly.IsDynamic)
                            continue;

                        var location = assembly.Location;
                        if (!string.IsNullOrEmpty(location))
                        {
                            var dir = Path.GetDirectoryName(location);
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            {
                                searchDirs.Add(Path.GetFullPath(dir));
                            }
                        }
                    }
                    catch
                    {
                        // Skip assemblies that throw exceptions when accessing Location
                        continue;
                    }
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed to get loaded assemblies: {ex.Message}");
            }

            return searchDirs.ToList();
        }

        /// <summary>
        /// Get backup directory (Assets同级目录/InvokeTrackerBackupFiles)
        /// </summary>
        private string GetBackupDirectory()
        {
            var assetsPath = Application.dataPath;
            var projectRoot = Path.GetDirectoryName(assetsPath);
            var backupDir = Path.Combine(projectRoot, "InvokeTrackerBackupFiles");
            return backupDir;
        }

        private void RestoreWithConfig(InvokeTrackerConfig cfg)
        {
            var backupDir = GetBackupDirectory();
            
            if (!Directory.Exists(backupDir))
            {
                UnityEngine.Debug.LogError($"Error: Backup directory not found: {backupDir}");
                EditorUtility.DisplayDialog("Error", "Backup directory not found!", "OK");
                return;
            }

            // Find all .backup files in backup directory
            var backupFiles = Directory.GetFiles(backupDir, "*.backup", SearchOption.TopDirectoryOnly);
            
            if (backupFiles.Length == 0)
            {
                UnityEngine.Debug.LogError("Error: No backup files found!");
                EditorUtility.DisplayDialog("Error", "No backup files found in backup directory!", "OK");
                return;
            }

            UnityEngine.Debug.Log($"=== Restoring {backupFiles.Length} files from backup ===");

            int successCount = 0;
            int failCount = 0;

            foreach (var backupPath in backupFiles)
            {
                var backupFileName = Path.GetFileName(backupPath);
                
                // Read original path from .txt file
                var pathRecordFile = backupPath + ".txt";
                if (!File.Exists(pathRecordFile))
                {
                    UnityEngine.Debug.LogWarning($"[SKIP] {backupFileName} - Path record file not found");
                    failCount++;
                    continue;
                }

                string originalPath;
                try
                {
                    originalPath = File.ReadAllText(pathRecordFile).Trim();
                }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogError($"[FAILED] {backupFileName} - Cannot read path record: {ex.Message}");
                    failCount++;
                    continue;
                }

                if (string.IsNullOrEmpty(originalPath))
                {
                    UnityEngine.Debug.LogWarning($"[SKIP] {backupFileName} - Empty path in record file");
                    failCount++;
                    continue;
                }

                try
                {
                    File.Copy(backupPath, originalPath, true);
                    UnityEngine.Debug.Log($"[SUCCESS] {backupFileName} -> {originalPath}");
                    successCount++;
                }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogError($"[FAILED] {backupFileName}: {ex.Message}");
                    failCount++;
                }
            }

            var summary = $"\n=== Summary ===\n" +
                $"Total: {backupFiles.Length}\n" +
                $"Success: {successCount}\n" +
                $"Failed: {failCount}";
            UnityEngine.Debug.Log(summary);

            if (failCount == 0)
            {
                UnityEngine.Debug.Log($"All {successCount} files restored successfully!");
                EditorUtility.DisplayDialog("Success", 
                    $"Successfully restored {successCount} files from backup!", "OK");
            }
            else
            {
                UnityEngine.Debug.LogWarning($"Restore completed with {failCount} failures. Check Console for details.");
                EditorUtility.DisplayDialog("Partial Success", 
                    $"Success: {successCount}\nFailed: {failCount}\n\nCheck Console for details.", "OK");
            }
        }
    }
}
