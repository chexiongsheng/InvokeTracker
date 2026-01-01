using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace InvokeTracker.Unity
{
    /// <summary>
    /// Unity runtime component to collect and display invoke statistics
    /// </summary>
    public class InvokeStatsCollector : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("Field prefix used during IL weaving")]
        public string fieldPrefix = "_invokeCount_";

        [Tooltip("Helper type suffix for generic types")]
        public string helperTypeSuffix = "_InvokeCounters";

        [Tooltip("Assemblies to scan (empty = all loaded assemblies)")]
        public string[] assembliesToScan = Array.Empty<string>();

        [Tooltip("Namespaces to include (empty = all)")]
        public string[] includeNamespaces = new string[0];

        [Header("Output")]
        [Tooltip("Log results to console")]
        public bool logToConsole = false;

        [Tooltip("Export to JSON file")]
        public bool exportToJson = true;

        [Tooltip("JSON export path (relative to Application.persistentDataPath)")]
        public string jsonExportPath = "invoke_stats.json";

        private Dictionary<string, uint> _stats = new Dictionary<string, uint>();
        private Dictionary<string, string> _methodToAssembly = new Dictionary<string, string>();

        /// <summary>
        /// Collect invoke statistics from all instrumented assemblies
        /// </summary>
        public void CollectStats()
        {
            _stats.Clear();
            _methodToAssembly.Clear();

            var assemblies = GetTargetAssemblies();

            foreach (var assembly in assemblies)
            {
                ScanAssembly(assembly);
            }

            if (logToConsole)
            {
                LogStats();
            }

            if (exportToJson)
            {
                ExportToJson();
            }
        }

        /// <summary>
        /// Reset all counters to zero
        /// </summary>
        public void ResetCounters()
        {
            var assemblies = GetTargetAssemblies();

            int resetCount = 0;
            foreach (var assembly in assemblies)
            {
                resetCount += ResetAssemblyCounters(assembly);
            }

            Debug.Log($"[InvokeTracker] Reset {resetCount} counters");
        }

        /// <summary>
        /// Get current statistics dictionary
        /// </summary>
        public Dictionary<string, uint> GetStats()
        {
            return new Dictionary<string, uint>(_stats);
        }

        private Assembly[] GetTargetAssemblies()
        {
            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();

            if (assembliesToScan == null || assembliesToScan.Length == 0)
            {
                return allAssemblies;
            }

            return allAssemblies
                .Where(a => assembliesToScan.Contains(a.GetName().Name))
                .ToArray();
        }

        private void ScanAssembly(Assembly assembly)
        {
            try
            {
                var types = assembly.GetTypes();
                var assemblyName = assembly.GetName().Name;

                foreach (var type in types)
                {
                    // Check namespace filter
                    if (includeNamespaces.Length > 0)
                    {
                        bool included = false;
                        foreach (var ns in includeNamespaces)
                        {
                            if (type.Namespace != null && type.Namespace.StartsWith(ns))
                            {
                                included = true;
                                break;
                            }
                        }
                        if (!included) continue;
                    }

                    ScanType(type, assemblyName);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[InvokeTracker] Failed to scan assembly {assembly.GetName().Name}: {ex.Message}");
            }
        }

        private void ScanType(Type type, string assemblyName)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);

            foreach (var field in fields)
            {
                // Check if this is an invoke counter field
                if (field.Name.StartsWith(fieldPrefix) && field.FieldType == typeof(uint))
                {
                    var value = (uint)field.GetValue(null);
                    
                    if (value > 0)
                    {
                        // Extract method name from field name
                        var methodName = field.Name.Substring(fieldPrefix.Length);
                        
                        // Check if this is a helper type for generic class
                        var typeName = type.FullName;
                        if (type.Name.EndsWith(helperTypeSuffix))
                        {
                            // Remove the helper suffix to get sanitized generic type name
                            // e.g., "StaticTranslate_1_InvokeCounters" -> "StaticTranslate_1"
                            var sanitizedName = type.Name.Substring(0, type.Name.Length - helperTypeSuffix.Length);
                            
                            // Parse generic parameter count from sanitized name
                            // e.g., "StaticTranslate_1" -> "StaticTranslate<T>"
                            //       "Action_2" -> "Action<T1,T2>"
                            var underscoreIndex = sanitizedName.LastIndexOf('_');
                            if (underscoreIndex >= 0 && int.TryParse(sanitizedName.Substring(underscoreIndex + 1), out int genericParamCount))
                            {
                                var baseName = sanitizedName.Substring(0, underscoreIndex);
                                
                                // Generate generic parameter list: <T>, <T1,T2>, <T1,T2,T3>, etc.
                                string genericParams;
                                if (genericParamCount == 1)
                                {
                                    genericParams = "<T>";
                                }
                                else
                                {
                                    var paramNames = new string[genericParamCount];
                                    for (int i = 0; i < genericParamCount; i++)
                                    {
                                        paramNames[i] = "T" + (i + 1);
                                    }
                                    genericParams = "<" + string.Join(",", paramNames) + ">";
                                }
                                
                                var originalTypeName = baseName + genericParams;
                                
                                if (!string.IsNullOrEmpty(type.Namespace))
                                {
                                    typeName = type.Namespace + "." + originalTypeName;
                                }
                                else
                                {
                                    typeName = originalTypeName;
                                }
                            }
                        }
                        
                        var fullName = $"{typeName}.{methodName}";
                        
                        _stats[fullName] = value;
                        _methodToAssembly[fullName] = assemblyName;
                    }
                }
            }
        }

        private int ResetAssemblyCounters(Assembly assembly)
        {
            int count = 0;

            try
            {
                var types = assembly.GetTypes();

                foreach (var type in types)
                {
                    var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);

                    foreach (var field in fields)
                    {
                        if (field.Name.StartsWith(fieldPrefix) && field.FieldType == typeof(uint))
                        {
                            field.SetValue(null, (uint)0);
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[InvokeTracker] Failed to reset counters in {assembly.GetName().Name}: {ex.Message}");
            }

            return count;
        }

        private void LogStats()
        {
            Debug.Log($"[InvokeTracker] Collected {_stats.Count} invoke statistics:");

            // Sort by invoke count descending
            var sorted = _stats.OrderByDescending(kv => kv.Value);

            foreach (var kv in sorted)
            {
                Debug.Log($"  {kv.Key}: {kv.Value} invocations");
            }
        }

        private void ExportToJson()
        {
            try
            {
                // Group methods by assembly
                var assemblies = _stats
                    .GroupBy(kv => _methodToAssembly.ContainsKey(kv.Key) ? _methodToAssembly[kv.Key] : "Unknown")
                    .Select(g => new AssemblyInvokeData
                    {
                        assemblyName = g.Key,
                        totalMethods = g.Count(),
                        totalInvocations = g.Sum(kv => (long)kv.Value),
                        methods = g.Select(kv => new MethodInvokeData
                        {
                            fullName = kv.Key,
                            invocations = kv.Value
                        }).OrderByDescending(m => m.invocations).ToList()
                    })
                    .OrderByDescending(a => a.totalInvocations)
                    .ToList();

                var data = new InvokeStatsData
                {
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    totalAssemblies = assemblies.Count,
                    totalMethods = _stats.Count,
                    totalInvocations = _stats.Values.Sum(v => (long)v),
                    assemblies = assemblies
                };

                var json = JsonUtility.ToJson(data, true);
                var path = System.IO.Path.Combine(Application.persistentDataPath, jsonExportPath);
                System.IO.File.WriteAllText(path, json);

                Debug.Log($"[InvokeTracker] Exported stats to: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[InvokeTracker] Failed to export JSON: {ex.Message}");
            }
        }

        [Serializable]
        private class InvokeStatsData
        {
            public string timestamp;
            public int totalAssemblies;
            public int totalMethods;
            public long totalInvocations;
            public List<AssemblyInvokeData> assemblies;
        }

        [Serializable]
        private class AssemblyInvokeData
        {
            public string assemblyName;
            public int totalMethods;
            public long totalInvocations;
            public List<MethodInvokeData> methods;
        }

        [Serializable]
        private class MethodInvokeData
        {
            public string fullName;
            public uint invocations;
        }
    }
}
