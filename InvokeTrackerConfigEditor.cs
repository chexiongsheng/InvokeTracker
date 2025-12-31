using UnityEngine;
using UnityEditor;
using System.IO;

namespace InvokeTracker.Unity.Editor
{
    /// <summary>
    /// Custom inspector for InvokeTrackerConfig
    /// Provides a better UI for managing assembly references
    /// </summary>
    [CustomEditor(typeof(InvokeTrackerConfig))]
    public class InvokeTrackerConfigEditor : UnityEditor.Editor
    {
        private SerializedProperty targetAssembliesProp;
        private SerializedProperty includeNamespacesProp;
        private SerializedProperty excludeNamespacesProp;
        private SerializedProperty fieldPrefixProp;
        private SerializedProperty createBackupProp;
        private SerializedProperty instrumentCompilerGeneratedProp;
        private SerializedProperty weaverToolPathProp;

        private void OnEnable()
        {
            targetAssembliesProp = serializedObject.FindProperty("targetAssemblies");
            includeNamespacesProp = serializedObject.FindProperty("includeNamespaces");
            excludeNamespacesProp = serializedObject.FindProperty("excludeNamespaces");
            fieldPrefixProp = serializedObject.FindProperty("fieldPrefix");
            createBackupProp = serializedObject.FindProperty("createBackup");
            instrumentCompilerGeneratedProp = serializedObject.FindProperty("instrumentCompilerGenerated");
            weaverToolPathProp = serializedObject.FindProperty("weaverToolPath");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var config = (InvokeTrackerConfig)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Invoke Tracker Configuration", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Configure assemblies and options for invoke tracking instrumentation.", MessageType.Info);
            EditorGUILayout.Space();

            // Tool Configuration
            EditorGUILayout.LabelField("Tool Configuration", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(weaverToolPathProp, new GUIContent("Weaver Tool Path"));
            
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Browse...", GUILayout.Width(80)))
            {
                var path = EditorUtility.OpenFilePanel("Select Weaver Tool", Application.dataPath, "exe");
                if (!string.IsNullOrEmpty(path))
                {
                    weaverToolPathProp.stringValue = MakeRelativePath(path);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // Assembly Selection
            EditorGUILayout.LabelField("Assembly Selection", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Use Assembly Definition Asset (recommended) or custom path for external DLLs.", MessageType.Info);
            
            // Assembly list
            for (int i = 0; i < targetAssembliesProp.arraySize; i++)
            {
                var assemblyProp = targetAssembliesProp.GetArrayElementAtIndex(i);
                var enabledProp = assemblyProp.FindPropertyRelative("enabled");
                var assemblyAssetProp = assemblyProp.FindPropertyRelative("assemblyAsset");
                var customPathProp = assemblyProp.FindPropertyRelative("customPath");
                var descProp = assemblyProp.FindPropertyRelative("description");

                EditorGUILayout.BeginVertical("box");
                
                // Header row
                EditorGUILayout.BeginHorizontal();
                enabledProp.boolValue = EditorGUILayout.Toggle(enabledProp.boolValue, GUILayout.Width(20));
                
                var assemblyRef = config.targetAssemblies[i];
                var displayName = assemblyRef.GetDisplayName();
                EditorGUILayout.LabelField($"Assembly {i + 1}: {displayName}", EditorStyles.boldLabel);
                
                // Check if file exists
                var assemblyPath = assemblyRef.GetAssemblyPath();
                if (!string.IsNullOrEmpty(assemblyPath))
                {
                    var fullPath = Path.Combine(Application.dataPath, "..", assemblyPath);
                    var exists = File.Exists(fullPath);
                    var statusColor = exists ? Color.green : Color.red;
                    var statusText = exists ? "✓" : "✗";
                    
                    var oldColor = GUI.color;
                    GUI.color = statusColor;
                    GUILayout.Label(statusText, GUILayout.Width(20));
                    GUI.color = oldColor;
                }
                
                GUILayout.FlexibleSpace();
                
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    targetAssembliesProp.DeleteArrayElementAtIndex(i);
                    serializedObject.ApplyModifiedProperties();
                    return;
                }
                EditorGUILayout.EndHorizontal();

                // Assembly Definition Asset field
                EditorGUILayout.PropertyField(assemblyAssetProp, new GUIContent("Assembly Definition"));
                
                // Custom path field (only show if no assembly asset)
                if (assemblyAssetProp.objectReferenceValue == null)
                {
                    EditorGUILayout.PropertyField(customPathProp, new GUIContent("Custom Path"));
                    
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Browse DLL", GUILayout.Height(20)))
                    {
                        var path = EditorUtility.OpenFilePanel("Select Assembly", 
                            Path.Combine(Application.dataPath, "..", "Library", "ScriptAssemblies"), "dll");
                        if (!string.IsNullOrEmpty(path))
                        {
                            customPathProp.stringValue = MakeRelativePath(path);
                        }
                    }
                    if (GUILayout.Button("Open Folder", GUILayout.Height(20)))
                    {
                        var folder = Path.Combine(Application.dataPath, "..", "Library", "ScriptAssemblies");
                        if (Directory.Exists(folder))
                        {
                            EditorUtility.RevealInFinder(folder);
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    // Show resolved path
                    if (!string.IsNullOrEmpty(assemblyPath))
                    {
                        EditorGUILayout.LabelField("Resolved Path:", assemblyPath, EditorStyles.miniLabel);
                    }
                }

                EditorGUILayout.PropertyField(descProp, new GUIContent("Description"));
                
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(5);
            }

            // Add assembly button
            if (GUILayout.Button("+ Add Assembly", GUILayout.Height(25)))
            {
                config.AddAssembly("Library/ScriptAssemblies/");
                EditorUtility.SetDirty(config);
            }

            EditorGUILayout.Space();

            // Quick add buttons for common assemblies
            EditorGUILayout.LabelField("Quick Add", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Quickly add common Unity assemblies. Note: firstpass assemblies only exist if you have Plugins or Standard Assets folders.", MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Assembly-CSharp"))
            {
                config.AddAssembly("Library/ScriptAssemblies/Assembly-CSharp.dll");
                EditorUtility.SetDirty(config);
            }
            if (GUILayout.Button("Assembly-CSharp-firstpass"))
            {
                config.AddAssembly("Library/ScriptAssemblies/Assembly-CSharp-firstpass.dll");
                EditorUtility.SetDirty(config);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // Namespace Filtering
            EditorGUILayout.LabelField("Namespace Filtering", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(includeNamespacesProp, new GUIContent("Include Namespaces"));
            EditorGUILayout.HelpBox("Comma-separated list. Leave empty to include all.", MessageType.Info);
            EditorGUILayout.PropertyField(excludeNamespacesProp, new GUIContent("Exclude Namespaces"));

            EditorGUILayout.Space();

            // Instrumentation Options
            EditorGUILayout.LabelField("Instrumentation Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(fieldPrefixProp, new GUIContent("Field Prefix"));
            EditorGUILayout.PropertyField(createBackupProp, new GUIContent("Create Backup"));
            EditorGUILayout.PropertyField(instrumentCompilerGeneratedProp, new GUIContent("Instrument Compiler-Generated"));

            EditorGUILayout.Space();

            // Summary
            var enabledCount = config.GetEnabledAssemblyPaths().Count;
            EditorGUILayout.HelpBox($"Total Assemblies: {config.targetAssemblies.Count}\nEnabled: {enabledCount}", MessageType.None);

            // Validation
            if (!config.Validate(out string errorMessage))
            {
                EditorGUILayout.HelpBox($"Configuration Error: {errorMessage}", MessageType.Warning);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private string MakeRelativePath(string absolutePath)
        {
            var projectPath = Path.GetDirectoryName(Application.dataPath);
            if (absolutePath.StartsWith(projectPath))
            {
                return absolutePath.Substring(projectPath.Length + 1).Replace('\\', '/');
            }
            return absolutePath;
        }
    }
}
