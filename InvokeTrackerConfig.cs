using UnityEngine;
using UnityEditorInternal;
using System.Collections.Generic;
using System.IO;

namespace InvokeTracker.Unity.Editor
{
    /// <summary>
    /// ScriptableObject configuration for invoke tracking instrumentation
    /// Can be saved as an asset and reused across different scenarios
    /// </summary>
    [CreateAssetMenu(fileName = "InvokeTrackerConfig", menuName = "Invoke Tracker/Configuration", order = 1)]
    public class InvokeTrackerConfig : ScriptableObject
    {
        [Header("Assembly Selection")]
        [Tooltip("List of assemblies to instrument. Use AssemblyDefinitionAsset for project assemblies or custom path for external DLLs.")]
        public List<AssemblyReference> targetAssemblies = new List<AssemblyReference>
        {
            new AssemblyReference { enabled = true }
        };

        [Header("Namespace Filtering")]
        [Tooltip("Only instrument methods in these namespaces (comma-separated). Leave empty to include all.")]
        public string includeNamespaces = "";

        [Tooltip("Exclude methods in these namespaces (comma-separated).")]
        public string excludeNamespaces = "UnityEngine,UnityEditor,System";

        [Header("Instrumentation Options")]
        [Tooltip("Prefix for the generated counter fields")]
        public string fieldPrefix = "_invokeCount_";

        [Tooltip("Create backup files before instrumentation")]
        public bool createBackup = true;

        [Tooltip("Instrument compiler-generated methods (e.g., property getters/setters)")]
        public bool instrumentCompilerGenerated = false;

        [Header("Tool Configuration")]
        [Tooltip("Path to the InvokeTracker.exe weaver tool")]
        public string weaverToolPath = "Tools/InvokeTracker.exe";

        /// <summary>
        /// Get list of enabled assembly paths
        /// </summary>
        public List<string> GetEnabledAssemblyPaths()
        {
            var paths = new List<string>();
            foreach (var assembly in targetAssemblies)
            {
                if (!assembly.enabled)
                    continue;

                var path = assembly.GetAssemblyPath();
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }
            return paths;
        }

        /// <summary>
        /// Add a new assembly reference by path
        /// </summary>
        public void AddAssembly(string path, bool enabled = true)
        {
            if (!string.IsNullOrEmpty(path))
            {
                targetAssemblies.Add(new AssemblyReference { customPath = path, enabled = enabled });
            }
        }

        /// <summary>
        /// Add a new assembly reference by AssemblyDefinitionAsset
        /// </summary>
        public void AddAssembly(AssemblyDefinitionAsset asmdef, bool enabled = true)
        {
            if (asmdef != null)
            {
                targetAssemblies.Add(new AssemblyReference { assemblyAsset = asmdef, enabled = enabled });
            }
        }

        /// <summary>
        /// Remove assembly at index
        /// </summary>
        public void RemoveAssemblyAt(int index)
        {
            if (index >= 0 && index < targetAssemblies.Count)
            {
                targetAssemblies.RemoveAt(index);
            }
        }

        /// <summary>
        /// Quick add common assemblies
        /// [Deprecated] Use Quick Add buttons in the Inspector instead
        /// </summary>
        [System.Obsolete("Use Quick Add buttons in the Inspector instead")]
        public void AddCommonAssemblies()
        {
            var common = new[]
            {
                "Library/ScriptAssemblies/Assembly-CSharp.dll",
                "Library/ScriptAssemblies/Assembly-CSharp-firstpass.dll"
            };

            foreach (var path in common)
            {
                bool exists = targetAssemblies.Exists(a => a.GetAssemblyPath() == path);
                if (!exists)
                {
                    AddAssembly(path);
                }
            }
        }

        /// <summary>
        /// Validate configuration
        /// </summary>
        public bool Validate(out string errorMessage)
        {
            if (targetAssemblies.Count == 0)
            {
                errorMessage = "No assemblies configured!";
                return false;
            }

            var enabledCount = GetEnabledAssemblyPaths().Count;
            if (enabledCount == 0)
            {
                errorMessage = "No assemblies enabled!";
                return false;
            }

            if (string.IsNullOrEmpty(weaverToolPath))
            {
                errorMessage = "Weaver tool path is empty!";
                return false;
            }

            errorMessage = null;
            return true;
        }
    }

    /// <summary>
    /// Reference to an assembly with enable/disable flag
    /// Supports both AssemblyDefinitionAsset (for project assemblies) and custom path (for external DLLs)
    /// </summary>
    [System.Serializable]
    public class AssemblyReference
    {
        [Tooltip("Enable/disable this assembly for instrumentation")]
        public bool enabled = true;

        [Header("Assembly Source (Choose One)")]
        [Tooltip("Unity Assembly Definition Asset (recommended for project assemblies)")]
        public AssemblyDefinitionAsset assemblyAsset;

        [Tooltip("Custom path to DLL (for external assemblies or manual override)")]
        public string customPath = "";

        [Space]
        [Tooltip("Optional description for this assembly")]
        [TextArea(2, 4)]
        public string description = "";

        /// <summary>
        /// Get the actual assembly path to use
        /// Priority: AssemblyDefinitionAsset > customPath
        /// </summary>
        public string GetAssemblyPath()
        {
            // Priority 1: AssemblyDefinitionAsset
            if (assemblyAsset != null)
            {
                var asmdefPath = UnityEditor.AssetDatabase.GetAssetPath(assemblyAsset);
                if (!string.IsNullOrEmpty(asmdefPath))
                {
                    // Get assembly name from asmdef
                    var asmdefName = Path.GetFileNameWithoutExtension(asmdefPath);
                    
                    // Try to find the compiled DLL
                    var dllPath = $"Library/ScriptAssemblies/{asmdefName}.dll";
                    
                    return dllPath;
                }
            }

            // Priority 2: Custom path
            if (!string.IsNullOrEmpty(customPath))
            {
                return customPath;
            }

            return null;
        }

        /// <summary>
        /// Get display name for this assembly reference
        /// </summary>
        public string GetDisplayName()
        {
            if (assemblyAsset != null)
            {
                return assemblyAsset.name;
            }

            if (!string.IsNullOrEmpty(customPath))
            {
                return Path.GetFileNameWithoutExtension(customPath);
            }

            return "<Not Set>";
        }

        /// <summary>
        /// Check if this reference is valid
        /// </summary>
        public bool IsValid()
        {
            return assemblyAsset != null || !string.IsNullOrEmpty(customPath);
        }
    }
}
