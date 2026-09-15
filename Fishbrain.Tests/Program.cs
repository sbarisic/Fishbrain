namespace Fishbrain.Tests;

internal static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 1 || args.Length == 1 && args[0] != "--unit") throw new ArgumentException("Usage: Fishbrain.Tests [--unit]");
        RuntimeTestSuite.RunAll(includeShippedArtifact: args.Length == 0);
    }
}
