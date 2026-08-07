namespace Fishbrain.DataGenerator;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Usage(); return 1; }
            var command = args[0].ToLowerInvariant();
            var options = CliOptions.Parse(args[1..]);
            switch (command)
            {
                case "fetch":
                    await CorpusPipeline.FetchAsync(options);
                    return 0;
                case "compile":
                    CorpusPipeline.Compile(options);
                    return 0;
                case "audit":
                    CorpusPipeline.Audit(options);
                    return 0;
                case "selftest" when args.Length == 1:
                    SelfTests.Run();
                    return 0;
                default:
                    Usage();
                    return 1;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR {exception.Message}");
            return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("FISHBRAIN TEACHING DATA");
        Console.WriteLine("  fetch [--manifest Fishbrain.DataGenerator/sources.json] [--raw datasets/raw]");
        Console.WriteLine("  compile [--count 10000] [--seed 42] [--raw datasets/raw] [--output datasets/compiled]");
        Console.WriteLine("  audit [--input datasets/compiled] [--manifest Fishbrain.DataGenerator/sources.json]");
        Console.WriteLine("  selftest");
    }
}

internal sealed record CliOptions(
    string ManifestPath,
    string RawPath,
    string OutputPath,
    string InputPath,
    int Count,
    int Seed)
{
    public static CliOptions Parse(string[] args)
    {
        var rootManifest = Path.Combine("Fishbrain.DataGenerator", "sources.json");
        var manifest = File.Exists(rootManifest) ? rootManifest : Path.Combine(AppContext.BaseDirectory, "sources.json");
        var result = new CliOptions(manifest, Path.Combine("datasets", "raw"), Path.Combine("datasets", "compiled"), Path.Combine("datasets", "compiled"), 10_000, 42);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) || !seen.Add(name))
                throw new ArgumentException($"Unknown or duplicate option '{name}'.");
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for '{name}'.");
            var value = args[index + 1];
            result = name.ToLowerInvariant() switch
            {
                "--manifest" => result with { ManifestPath = value },
                "--raw" => result with { RawPath = value },
                "--output" => result with { OutputPath = value },
                "--input" => result with { InputPath = value },
                "--count" => result with { Count = Positive(value, name) },
                "--seed" => result with { Seed = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException($"Unknown option '{name}'.")
            };
        }
        return result;
    }

    private static int Positive(string value, string name)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0) throw new ArgumentException($"{name} must be positive.");
        return parsed;
    }
}
