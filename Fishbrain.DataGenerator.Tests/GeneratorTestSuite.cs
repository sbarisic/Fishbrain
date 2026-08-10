namespace Fishbrain.DataGenerator.Tests;

internal static class GeneratorTestSuite
{
    public static void RunAll()
    {
        StateEnumerationStaysWithinBounds();
        CommandLineDefaultsAreConsistent();
        DuplicateCommandLineOptionsAreRejected();

        Console.WriteLine("PASS ALL GENERATOR TESTS");
    }

    private static void StateEnumerationStaysWithinBounds()
    {
        foreach (var index in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
        {
            DataGenerator.CorpusCompiler.StateFor(index).Validate();
        }

        Console.WriteLine("PASS STATE ENUMERATION BOUNDS");
    }

    private static void CommandLineDefaultsAreConsistent()
    {
        var defaults = DataGenerator.CliOptions.Parse([]);

        if (defaults.InputPath != Path.Combine("data", "compiled") || defaults.Count != 80_000)
        {
            throw new InvalidOperationException("CLI defaults are inconsistent.");
        }

        Console.WriteLine("PASS CLI DEFAULTS");
    }

    private static void DuplicateCommandLineOptionsAreRejected()
    {
        try
        {
            _ = DataGenerator.CliOptions.Parse(["--seed", "1", "--seed", "2"]);
        }
        catch (ArgumentException)
        {
            Console.WriteLine("PASS CLI VALIDATION");
            return;
        }

        throw new InvalidOperationException("Duplicate generator options were accepted.");
    }
}
