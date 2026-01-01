namespace InvokeTracker
{
    /// <summary>
    /// Configuration for the IL weaving process
    /// </summary>
    public class WeaverConfig
    {
        /// <summary>
        /// Path to the target assembly to be instrumented
        /// </summary>
        public string AssemblyPath { get; set; } = string.Empty;

        /// <summary>
        /// Prefix for the generated static counter fields
        /// </summary>
        public string FieldPrefix { get; set; } = "_invokeCount_";

        /// <summary>
        /// Namespaces to include (empty means all)
        /// </summary>
        public List<string> IncludeNamespaces { get; set; } = new();

        /// <summary>
        /// Namespaces to exclude
        /// </summary>
        public List<string> ExcludeNamespaces { get; set; } = new()
        {
            "UnityEngine",
            "UnityEditor",
            "System",
            "Mono",
            "Microsoft"
        };

        /// <summary>
        /// Whether to create backup of original assembly
        /// </summary>
        public bool CreateBackup { get; set; } = true;

        /// <summary>
        /// Whether to instrument compiler-generated methods (lambdas, async, etc.)
        /// </summary>
        public bool InstrumentCompilerGenerated { get; set; } = false;

        /// <summary>
        /// Output path for the instrumented assembly (empty means overwrite original)
        /// </summary>
        public string? OutputPath { get; set; }

        /// <summary>
        /// Additional directories to search for assembly dependencies
        /// </summary>
        public List<string> SearchDirectories { get; set; } = new();
    }
}
