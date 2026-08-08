foreach (var index in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
	Fishbrain.DataGenerator.CorpusCompiler.StateFor(index).Validate();
Console.WriteLine("PASS STATE ENUMERATION BOUNDS");

var defaults = Fishbrain.DataGenerator.CliOptions.Parse([]);
if (defaults.InputPath != Path.Combine("data", "compiled") || defaults.Count != 60_000)
	throw new InvalidOperationException("CLI defaults are inconsistent.");
Console.WriteLine("PASS CLI DEFAULTS");

var duplicateRejected = false;
try { _ = Fishbrain.DataGenerator.CliOptions.Parse(["--seed", "1", "--seed", "2"]); }
catch (ArgumentException) { duplicateRejected = true; }
if (!duplicateRejected) throw new InvalidOperationException("Duplicate generator options were accepted.");
Console.WriteLine("PASS CLI VALIDATION");
