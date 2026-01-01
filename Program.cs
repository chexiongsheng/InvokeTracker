namespace InvokeTracker
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Unity Invoke Tracker - IL Weaver ===\n");

            // Debug: Print all arguments
            Console.WriteLine($"Received {args.Length} arguments:");
            for (int i = 0; i < args.Length; i++)
            {
                Console.WriteLine($"  args[{i}] = {args[i]}");
            }
            Console.WriteLine();

            if (args.Length == 0)
            {
                ShowUsage();
                return;
            }

            try
            {
                var config = ParseArguments(args);
                var weaver = new AssemblyWeaver(config);
                weaver.Weave();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Environment.Exit(1);
            }
        }

        static WeaverConfig ParseArguments(string[] args)
        {
            var config = new WeaverConfig
            {
                AssemblyPath = args[0]
            };

            for (int i = 1; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg.StartsWith("--prefix="))
                {
                    config.FieldPrefix = arg.Substring("--prefix=".Length);
                }
                else if (arg.StartsWith("--include="))
                {
                    var namespaces = arg.Substring("--include=".Length).Split(',');
                    config.IncludeNamespaces.AddRange(namespaces);
                }
                else if (arg.StartsWith("--exclude="))
                {
                    var namespaces = arg.Substring("--exclude=".Length).Split(',');
                    config.ExcludeNamespaces.AddRange(namespaces);
                }
                else if (arg.StartsWith("--output="))
                {
                    config.OutputPath = arg.Substring("--output=".Length);
                }
                else if (arg == "--no-backup")
                {
                    config.CreateBackup = false;
                }
                else if (arg == "--instrument-compiler-generated")
                {
                    config.InstrumentCompilerGenerated = true;
                }
                else if (arg.StartsWith("--search-dir="))
                {
                    var dir = arg.Substring("--search-dir=".Length);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        config.SearchDirectories.Add(dir);
                    }
                }
                else if (arg.StartsWith("--backup-dir="))
                {
                    config.BackupDirectory = arg.Substring("--backup-dir=".Length);
                }
            }

            // Debug: Print parsed config
            Console.WriteLine($"Parsed config:");
            Console.WriteLine($"  AssemblyPath: {config.AssemblyPath}");
            Console.WriteLine($"  SearchDirectories.Count: {config.SearchDirectories.Count}");
            foreach (var dir in config.SearchDirectories)
            {
                Console.WriteLine($"    - {dir}");
            }
            Console.WriteLine();

            return config;
        }

        static void ShowUsage()
        {
            Console.WriteLine("Usage: InvokeTracker <assembly-path> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --prefix=<prefix>              Field name prefix (default: _invokeCount_)");
            Console.WriteLine("  --include=<ns1,ns2,...>        Namespaces to include (empty = all)");
            Console.WriteLine("  --exclude=<ns1,ns2,...>        Namespaces to exclude");
            Console.WriteLine("  --output=<path>                Output path (default: overwrite original)");
            Console.WriteLine("  --no-backup                    Don't create backup file");
            Console.WriteLine("  --instrument-compiler-generated Include compiler-generated methods");
            Console.WriteLine("  --search-dir=<directory>       Add assembly search directory (can be used multiple times)");
            Console.WriteLine("  --backup-dir=<directory>       Directory to store backup files");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  InvokeTracker Assembly-CSharp.dll --include=MyGame --prefix=_count_");
            Console.WriteLine();
        }
    }
}
