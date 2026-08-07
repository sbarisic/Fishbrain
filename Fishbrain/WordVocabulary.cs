using System.Text.Json;

namespace Fishbrain;

/// <summary>Deterministic uppercase word vocabulary stored in every current checkpoint.</summary>
internal sealed class WordVocabulary
{
    private readonly Dictionary<string, int> _wordToInput;
    private readonly Dictionary<int, int> _inputToOutput;
    private readonly int[] _outputToInput;
    private readonly HashSet<int> _generatedTextOutputs;

    public WordVocabulary(IEnumerable<string> words, IEnumerable<string> outputWords)
    {
        Words = words.Select(Tokenizer.NormalizeWord).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        OutputWords = outputWords.Select(Tokenizer.NormalizeWord).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (Words.Length == 0) throw new InvalidDataException("The word vocabulary cannot be empty.");
        var wordSet = Words.ToHashSet(StringComparer.Ordinal);
        if (OutputWords.Any(word => !wordSet.Contains(word)))
            throw new InvalidDataException("Every output word must also exist in the input vocabulary.");

        _wordToInput = new Dictionary<string, int>(Words.Length, StringComparer.Ordinal);
        for (var index = 0; index < Words.Length; index++)
            _wordToInput.Add(Words[index], Tokenizer.WordStart + index);

        var outputs = new List<int>();
        for (var token = 0; token < Tokenizer.WordStart; token++) outputs.Add(token);
        outputs.AddRange(OutputWords.Select(word => _wordToInput[word]));
        _outputToInput = outputs.ToArray();
        _inputToOutput = new Dictionary<int, int>(_outputToInput.Length);
        for (var output = 0; output < _outputToInput.Length; output++)
            _inputToOutput.Add(_outputToInput[output], output);

        _generatedTextOutputs = new HashSet<int>
        {
            OutputId(Tokenizer.Eos), OutputId(Tokenizer.Period), OutputId(Tokenizer.Comma),
            OutputId(Tokenizer.Question), OutputId(Tokenizer.Exclamation), OutputId(Tokenizer.Colon)
        };
        foreach (var word in OutputWords) _generatedTextOutputs.Add(OutputId(_wordToInput[word]));
    }

    public string[] Words { get; }
    public string[] OutputWords { get; }
    public int InputSize => Tokenizer.WordStart + Words.Length;
    public int OutputSize => _outputToInput.Length;
    public IReadOnlyCollection<int> GeneratedTextOutputs => _generatedTextOutputs;

    public int InputId(string word) => _wordToInput.GetValueOrDefault(Tokenizer.NormalizeWord(word), Tokenizer.Unknown);
    public int OutputId(int inputToken) => _inputToOutput.GetValueOrDefault(inputToken, Tokenizer.Unknown);
    public int InputIdFromOutput(int outputToken) =>
        (uint)outputToken < (uint)_outputToInput.Length
            ? _outputToInput[outputToken]
            : throw new ArgumentOutOfRangeException(nameof(outputToken));

    public string WordForInput(int token) =>
        token >= Tokenizer.WordStart && token < InputSize
            ? Words[token - Tokenizer.WordStart]
            : throw new ArgumentOutOfRangeException(nameof(token));

    public bool IsWord(int inputToken) => inputToken >= Tokenizer.WordStart && inputToken < InputSize;

    public static WordVocabulary Build(string trainingPath)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var outputWords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(trainingPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            AddProperty(root, "input", words, null);
            AddProperty(root, "response", words, outputWords);
            AddProperty(root, "result", words, outputWords);
            AddProperty(root, "tool", words, outputWords);
            if (root.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
            {
                foreach (var argument in arguments.EnumerateArray())
                    if (argument.ValueKind == JsonValueKind.String)
                        AddText(argument.GetString()!, words, outputWords);
            }
        }
        return new WordVocabulary(words, outputWords);
    }

    public static WordVocabulary Testing() => new(
        ["PLAYER", "NPC", "HELLO", "FRIEND", "WHAT", "I", "AM", "LOOKING", "AROUND", "THANKS", "WAIT"],
        ["HELLO", "FRIEND", "WHAT", "I", "AM", "LOOKING", "AROUND", "THANKS", "WAIT"]);

    private static void AddProperty(
        JsonElement root,
        string name,
        HashSet<string> words,
        HashSet<string>? outputs)
    {
        if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            AddText(property.GetString()!, words, outputs);
    }

    private static void AddText(string text, HashSet<string> words, HashSet<string>? outputs)
    {
        foreach (var token in Tokenizer.Lex(Tokenizer.Normalize(text)))
        {
            if (token.Kind != LexicalTokenKind.Word) continue;
            words.Add(token.Text);
            outputs?.Add(token.Text);
        }
    }
}
