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
        private string lastOutput = "";

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

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
                
                // Use TextArea with scroll bar (built-in)
                EditorGUILayout.TextArea(lastOutput, GUILayout.MaxHeight(150));
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

            EditorGUILayout.Space();

            // Output section
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            
            // Use TextArea with scroll bar (built-in)
            // GUILayout.MaxHeight will automatically add scroll bar when content exceeds height
            EditorGUILayout.TextArea(lastOutput, GUILayout.MaxHeight(150));
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
                lastOutput = $"Error: Weaver tool not found at {fullWeaverPath}";
                UnityEngine.Debug.LogError(lastOutput);
                return;
            }

            var enabledAssemblies = cfg.GetEnabledAssemblyPaths();
            if (enabledAssemblies.Count == 0)
            {
                lastOutput = "Error: No enabled assemblies!";
                UnityEngine.Debug.LogError(lastOutput);
                EditorUtility.DisplayDialog("Error", "Please enable at least one assembly to instrument.", "OK");
                return;
            }

            var results = new System.Text.StringBuilder();
            results.AppendLine($"=== Instrumenting {enabledAssemblies.Count} assemblies ===");

            int successCount = 0;
            int failCount = 0;

            foreach (var assemblyPath in enabledAssemblies)
            {
                var fullAssemblyPath = Path.Combine(Application.dataPath, "..", assemblyPath);

                if (!File.Exists(fullAssemblyPath))
                {
                    var error = $"[SKIP] {assemblyPath} - File not found";
                    results.AppendLine(error);
                    UnityEngine.Debug.LogWarning(error);
                    failCount++;
                    continue;
                }

                results.AppendLine($"\n--- Processing: {assemblyPath} ---");

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

                    results.AppendLine(output);
                    
                    if (!string.IsNullOrEmpty(error))
                    {
                        results.AppendLine("Errors:");
                        results.AppendLine(error);
                    }

                    // Check if assembly was already instrumented
                    if (process.ExitCode == 0 && output.Contains("already instrumented"))
                    {
                        results.AppendLine($"[SKIPPED] {assemblyPath} - Already instrumented");
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
                                
                                // Success! Clean up the instrumented file
                                File.Delete(instrumentedPath);
                                
                                results.AppendLine($"[SUCCESS] {assemblyPath}");
                                successCount++;
                            }
                            catch (IOException ex) when (ex.Message.Contains("being used by another process"))
                            {
                                // File is locked by Unity Editor
                                results.AppendLine($"\n⚠️ WARNING: Cannot copy to {assemblyPath}");
                                results.AppendLine($"The file is currently locked by Unity Editor.");
                                results.AppendLine($"\n📁 Instrumented file saved at:");
                                results.AppendLine($"   {instrumentedPath}");
                                results.AppendLine($"\n💡 Manual Steps:");
                                results.AppendLine($"   1. Close Unity Editor");
                                results.AppendLine($"   2. Copy the .instrumented file to original location:");
                                results.AppendLine($"      From: {instrumentedPath}");
                                results.AppendLine($"      To:   {fullAssemblyPath}");
                                results.AppendLine($"   3. Delete the .instrumented file");
                                results.AppendLine($"   4. Reopen Unity Editor");
                                results.AppendLine($"\n[PARTIAL] {assemblyPath} - Instrumented but not copied (file locked)");
                                
                                // Count as partial success
                                successCount++;
                            }
                            catch (System.Exception ex)
                            {
                                results.AppendLine($"[FAILED] {assemblyPath} - Copy error: {ex.Message}");
                                failCount++;
                            }
                        }
                        else
                        {
                            results.AppendLine($"[FAILED] {assemblyPath} - Instrumented file not found");
                            failCount++;
                        }
                    }
                    else
                    {
                        results.AppendLine($"[FAILED] {assemblyPath} - Exit code: {process.ExitCode}");
                        failCount++;
                    }
                }
                catch (System.Exception ex)
                {
                    var error = $"[EXCEPTION] {assemblyPath}: {ex.Message}";
                    results.AppendLine(error);
                    UnityEngine.Debug.LogError(error);
                    failCount++;
                }
            }

            results.AppendLine($"\n=== Summary ===");
            results.AppendLine($"Total: {enabledAssemblies.Count}");
            results.AppendLine($"Success: {successCount}");
            results.AppendLine($"Failed: {failCount}");

            lastOutput = results.ToString();

            if (failCount == 0)
            {
                UnityEngine.Debug.Log($"All {successCount} assemblies instrumented successfully!");
                EditorUtility.DisplayDialog("Success", 
                    $"Successfully instrumented {successCount} assemblies!", "OK");
            }
            else
            {
                UnityEngine.Debug.LogWarning($"Instrumentation completed with {failCount} failures. Check output for details.");
                EditorUtility.DisplayDialog("Partial Success", 
                    $"Success: {successCount}\nFailed: {failCount}\n\nCheck output for details.", "OK");
            }
        }

        private void RestoreWithConfig(InvokeTrackerConfig cfg)
        {
            var enabledAssemblies = cfg.GetEnabledAssemblyPaths();
            if (enabledAssemblies.Count == 0)
            {
                lastOutput = "Error: No enabled assemblies!";
                UnityEngine.Debug.LogError(lastOutput);
                EditorUtility.DisplayDialog("Error", "Please enable at least one assembly to restore.", "OK");
                return;
            }

            var results = new System.Text.StringBuilder();
            results.AppendLine($"=== Restoring {enabledAssemblies.Count} assemblies from backup ===");

            int successCount = 0;
            int failCount = 0;

            foreach (var assemblyPath in enabledAssemblies)
            {
                var fullAssemblyPath = Path.Combine(Application.dataPath, "..", assemblyPath);
                var backupPath = fullAssemblyPath + ".backup";

                if (!File.Exists(backupPath))
                {
                    var error = $"[SKIP] {assemblyPath} - Backup not found";
                    results.AppendLine(error);
                    UnityEngine.Debug.LogWarning(error);
                    failCount++;
                    continue;
                }

                try
                {
                    File.Copy(backupPath, fullAssemblyPath, true);
                    var success = $"[SUCCESS] {assemblyPath} - Restored from backup";
                    results.AppendLine(success);
                    successCount++;

                    // Also restore PDB file if backup exists
                    var pdbPath = Path.ChangeExtension(fullAssemblyPath, ".pdb");
                    var pdbBackupPath = pdbPath + ".backup";
                    if (File.Exists(pdbBackupPath))
                    {
                        File.Copy(pdbBackupPath, pdbPath, true);
                        results.AppendLine($"  └─ PDB restored");
                    }
                }
                catch (System.Exception ex)
                {
                    var error = $"[FAILED] {assemblyPath}: {ex.Message}";
                    results.AppendLine(error);
                    UnityEngine.Debug.LogError(error);
                    failCount++;
                }
            }

            results.AppendLine($"\n=== Summary ===");
            results.AppendLine($"Total: {enabledAssemblies.Count}");
            results.AppendLine($"Success: {successCount}");
            results.AppendLine($"Failed: {failCount}");

            lastOutput = results.ToString();

            if (failCount == 0)
            {
                UnityEngine.Debug.Log($"All {successCount} assemblies restored successfully!");
                EditorUtility.DisplayDialog("Success", 
                    $"Successfully restored {successCount} assemblies from backup!", "OK");
            }
            else
            {
                UnityEngine.Debug.LogWarning($"Restore completed with {failCount} failures. Check output for details.");
                EditorUtility.DisplayDialog("Partial Success", 
                    $"Success: {successCount}\nFailed: {failCount}\n\nCheck output for details.", "OK");
            }
        }
    }
}
