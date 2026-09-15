namespace Fishbrain.DataGenerator.Tests;

internal static class GeneratorTestSuite
{
    public static void RunAll()
    {
        StateEnumerationStaysWithinBounds();
        CommandLineDefaultsAreConsistent();
        DuplicateCommandLineOptionsAreRejected();
        foreach (var source in new[] { "PROJECT_CONTEXTUAL_ACTIONS", "PROJECT_CONTEXTUAL_MEMORY", "PROJECT_CONTEXTUAL_COMPOUND", "PROJECT_CONTEXTUAL_AGENDA" })
        {
            var rows = DataGenerator.CorpusCompiler.ContextualRows(source, 5000, 42).ToArray();
            if (rows.Length != 5000 || rows.Select(x => x.Input).Distinct().Count() != 5000)
                throw new InvalidOperationException("Contextual group contains duplicate inputs: " + source);
            foreach (var row in rows)
            {
                row.Contextual!.Validate(row.Turns![^1].Text);
                foreach (var slot in row.StructuredPerception.Slots)
                    if (row.Input.Substring(slot.Start, slot.Length) != slot.Value) throw new InvalidOperationException("Contextual slot offset mismatch.");
            }
        }
        Console.WriteLine("PASS CONTEXTUAL CORPUS TARGETS");

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

        if (defaults.InputPath != Path.Combine("data", "compiled") || defaults.Count != 100_000)
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
