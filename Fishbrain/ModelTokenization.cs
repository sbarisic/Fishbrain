using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

internal sealed record TeachingRecovery(
    string ProjectPath,
    string CorpusDirectory,
    string CheckpointPath,
    int PlannedSteps,
    int UntilStep)
{
    public string TeachCommand(int untilStep) =>
        $"dotnet run -c Release --project {Quote(ProjectPath)} -- teach {Quote(CorpusDirectory)} " +
        $"{Quote(CheckpointPath)} --planned {PlannedSteps} --until {untilStep}";

    public string EvaluateCommand() =>
        $"dotnet run -c Release --project {Quote(ProjectPath)} -- evaluate " +
        $"{Quote(Path.Combine(CorpusDirectory, "test.jsonl"))} {Quote(CheckpointPath)}";

    internal static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
internal static class Tokenizer
{
    public const int Bos = 0;
    public const int Sep = 1;
    public const int Eos = 2;
    public const int Text = 3;
    public const int Call = 4;
    public const int Result = 5;
    public const int State = 6;
    public const int Decide = 7;
    public const int RapportStart = 8;
    public const int MoodStart = 12;
    public const int IntentStart = 16;
    public const int ActionStart = 36;
    public const int ToneStart = 41;
    public const int TopicStart = 45;
    public const int GoalStart = 52;
    public const int AffectStart = 60;
    public const int ExpectedFalse = 65;
    public const int ExpectedTrue = 66;
    public const int Period = 67;
    public const int Comma = 68;
    public const int Question = 69;
    public const int Exclamation = 70;
    public const int Colon = 71;
    public const int ArgumentSeparator = 72;
    public const int Quote = 73;
    public const int WordBegin = 74;
    public const int WordEnd = 75;
    public const int CharacterStart = 76;
    public const int CharacterCount = 38;
    public const int WordStart = CharacterStart + CharacterCount;
    public const int Unknown = WordBegin;

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new StringBuilder(text.Length);
        var pendingSpace = false;
        var inQuote = false;

        foreach (var original in text)
        {
            if (char.IsWhiteSpace(original))
            {
                pendingSpace = output.Length > 0;
                continue;
            }

            var character = original switch
            {
                '\u2018' or '\u2019' => '\'',
                '\u201c' or '\u201d' => '"',
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' => '-',
                _ => char.ToUpperInvariant(original)
            };
            if (!IsVisibleCharacter(character))
                throw new ArgumentException(
                    $"Unsupported character '{original}'. Only A-Z, 0-9, whitespace, and . , ? ! ' \" - : are allowed.");

            if (character is '.' or '?' or '!')
            {
                TrimTrailingSpace(output);
                if (output.Length > 0 && output[^1] is '.' or '?' or '!')
                    output[^1] = MergeTerminal(output[^1], character);
                else
                    output.Append(character);
                pendingSpace = true;
                continue;
            }

            if (character is ',' or ':')
            {
                TrimTrailingSpace(output);
                if (output.Length == 0 || output[^1] != character) output.Append(character);
                pendingSpace = true;
                continue;
            }

            if (character == '"')
            {
                if (!inQuote && pendingSpace && output.Length > 0) output.Append(' ');
                else if (inQuote) TrimTrailingSpace(output);
                output.Append(character);
                inQuote = !inQuote;
                pendingSpace = !inQuote;
                continue;
            }

            if (character is '\'' or '-')
            {
                TrimTrailingSpace(output);
                output.Append(character);
                pendingSpace = false;
                continue;
            }

            if (pendingSpace && output.Length > 0 && output[^1] is not ('\'' or '-')) output.Append(' ');
            output.Append(character);
            pendingSpace = false;
        }

        if (inQuote) throw new ArgumentException("Quoted text must contain a closing quote.", nameof(text));
        return output.ToString();
    }

    private static void TrimTrailingSpace(StringBuilder output)
    {
        if (output.Length > 0 && output[^1] == ' ') output.Length--;
    }

    private static char MergeTerminal(char first, char second) =>
        first == '?' || second == '?' ? '?' : first == '!' || second == '!' ? '!' : '.';

    public static IReadOnlyList<LexicalToken> Lex(string normalized)
    {
        var result = new List<LexicalToken>();
        var word = new StringBuilder();
        void FlushWord()
        {
            if (word.Length == 0) return;
            result.Add(new LexicalToken(LexicalTokenKind.Word, word.ToString()));
            word.Clear();
        }

        foreach (var character in normalized)
        {
            if (IsIdentifierCharacter(character) ||
                character is '\'' or '-' && word.Length > 0)
            {
                word.Append(character);
                continue;
            }
            FlushWord();
            if (character is '.' or ',' or '?' or '!' or ':' or '"')
                result.Add(new LexicalToken(LexicalTokenKind.Punctuation, character.ToString()));
        }
        FlushWord();
        return result;
    }

    public static string NormalizeWord(string word)
    {
        var normalized = Normalize(word);
        var tokens = Lex(normalized);
        if (tokens.Count != 1 || tokens[0].Kind != LexicalTokenKind.Word || tokens[0].Text != normalized)
            throw new InvalidDataException($"'{word}' is not one canonical word.");
        return normalized;
    }

    public static bool IsVisibleCharacter(char character) =>
        IsIdentifierCharacter(character) || character is ' ' or '.' or ',' or '?' or '!' or '\'' or '-' or ':' or '"';

    public static bool IsIdentifierCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= '0' and <= '9';

    public static int Character(char character) => character switch
    {
        >= 'A' and <= 'Z' => CharacterStart + character - 'A',
        >= '0' and <= '9' => CharacterStart + 26 + character - '0',
        '\'' => CharacterStart + 36,
        '-' => CharacterStart + 37,
        _ => throw new ArgumentOutOfRangeException(nameof(character))
    };

    public static char DecodeCharacter(int token) => token switch
    {
        >= CharacterStart and < CharacterStart + 26 => (char)('A' + token - CharacterStart),
        >= CharacterStart + 26 and < CharacterStart + 36 => (char)('0' + token - CharacterStart - 26),
        CharacterStart + 36 => '\'',
        CharacterStart + 37 => '-',
        _ => throw new ArgumentOutOfRangeException(nameof(token))
    };

    public static int Mood(NpcMood value) => MoodStart + (int)value;
    public static int Intent(DialogueIntent value) => IntentStart + (int)value;
    public static int Action(ResponseAction value) => ActionStart + (int)value;
    public static int Tone(ResponseTone value) => ToneStart + (int)value;
    public static int Topic(DialogueTopic value) => TopicStart + (int)value;
    public static int Goal(NpcGoal value) => GoalStart + (int)value;
    public static int Affect(UserAffect value) => AffectStart + (int)value;

    public static DialogueIntent DecodeIntent(int token) =>
        token >= IntentStart && token < IntentStart + Enum.GetValues<DialogueIntent>().Length
            ? (DialogueIntent)(token - IntentStart)
            : throw new ArgumentOutOfRangeException(nameof(token));

    public static UserAffect DecodeAffect(int token) =>
        token >= AffectStart && token < AffectStart + Enum.GetValues<UserAffect>().Length
            ? (UserAffect)(token - AffectStart)
            : throw new ArgumentOutOfRangeException(nameof(token));
}

/// <summary>Immutable vocabulary-bound tokenizer owned by one model.</summary>
internal sealed class DialogueTokenizer
{
    private readonly WordVocabulary _vocabulary;

    public DialogueTokenizer(WordVocabulary vocabulary) =>
        _vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));

    public WordVocabulary Vocabulary => _vocabulary;
    public int VocabularySize => _vocabulary.InputSize;
    public int OutputSize => _vocabulary.OutputSize;
    public IReadOnlyCollection<int> GeneratedTextOutputs => _vocabulary.GeneratedTextOutputs;
    public bool ContainsUnknown(string text) => Encode(text).Contains(Tokenizer.WordBegin);
    public IReadOnlyList<string> UnknownWords(string text) => Tokenizer.Lex(DialogueText.Normalize(text))
        .Where(token => token.Kind == LexicalTokenKind.Word &&
                        _vocabulary.InputId(token.Text) == Tokenizer.Unknown)
        .Select(token => token.Text)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public int[] Encode(string normalized)
    {
        var result = new List<int>();
        foreach (var token in Tokenizer.Lex(DialogueText.Normalize(normalized)))
        {
            if (token.Kind == LexicalTokenKind.Word)
            {
                var known = _vocabulary.InputId(token.Text);
                if (known != Tokenizer.Unknown) result.Add(known);
                else
                {
                    result.Add(Tokenizer.WordBegin);
                    result.AddRange(token.Text.Select(Tokenizer.Character));
                    result.Add(Tokenizer.WordEnd);
                }
                continue;
            }
            result.Add(token.Text[0] switch
            {
                '.' => Tokenizer.Period,
                ',' => Tokenizer.Comma,
                '?' => Tokenizer.Question,
                '!' => Tokenizer.Exclamation,
                ':' => Tokenizer.Colon,
                '"' => Tokenizer.Quote,
                _ => throw new InvalidDataException($"Unsupported punctuation token '{token.Text}'.")
            });
        }
        return result.ToArray();
    }

    public string DecodeInputToken(int token) => token switch
    {
        Tokenizer.Period => ".",
        Tokenizer.Comma => ",",
        Tokenizer.Question => "?",
        Tokenizer.Exclamation => "!",
        Tokenizer.Colon => ":",
        Tokenizer.Quote => "\"",
        Tokenizer.WordBegin => "<WORD_BEGIN>",
        Tokenizer.WordEnd => "<WORD_END>",
        _ when token >= Tokenizer.CharacterStart && token < Tokenizer.WordStart => Tokenizer.DecodeCharacter(token).ToString(),
        _ when _vocabulary.IsWord(token) => _vocabulary.WordForInput(token),
        _ => throw new ArgumentOutOfRangeException(nameof(token), "Control tokens are not visible text.")
    };

    public string DetokenizeOutput(IEnumerable<int> outputTokens) =>
        DetokenizeInput(outputTokens.Select(_vocabulary.InputIdFromOutput));

    public string DetokenizeInput(IEnumerable<int> inputTokens)
    {
        var text = new StringBuilder();
        var oov = new StringBuilder();
        var inOov = false;
        var inQuote = false;
        foreach (var inputToken in inputTokens)
        {
            if (inputToken == Tokenizer.Eos) break;
            if (inputToken == Tokenizer.WordBegin)
            {
                if (inOov) throw new InvalidDataException("Nested OOV word markers are invalid.");
                inOov = true;
                oov.Clear();
                continue;
            }
            if (inputToken == Tokenizer.WordEnd)
            {
                if (!inOov || oov.Length == 0) throw new InvalidDataException("Invalid OOV word boundary.");
                if (text.Length > 0 && !(inQuote && text[^1] == '"')) text.Append(' ');
                text.Append(oov);
                inOov = false;
                continue;
            }
            if (inOov)
            {
                oov.Append(Tokenizer.DecodeCharacter(inputToken));
                continue;
            }
            if (inputToken == Tokenizer.Quote)
            {
                if (!inQuote && text.Length > 0 && text[^1] != ' ') text.Append(' ');
                else if (inQuote && text.Length > 0 && text[^1] == ' ') text.Length--;
                text.Append('"');
                inQuote = !inQuote;
                continue;
            }
            if (inputToken is Tokenizer.Period or Tokenizer.Comma or Tokenizer.Question or
                Tokenizer.Exclamation or Tokenizer.Colon)
            {
                if (text.Length > 0 && text[^1] == ' ') text.Length--;
                text.Append(DecodeInputToken(inputToken));
                continue;
            }
            if (!_vocabulary.IsWord(inputToken)) continue;
            if (text.Length > 0 && !(inQuote && text[^1] == '"')) text.Append(' ');
            text.Append(_vocabulary.WordForInput(inputToken));
        }
        if (inOov) throw new InvalidDataException("Unterminated OOV word.");
        if (inQuote) throw new InvalidDataException("Unterminated quoted text.");
        return text.ToString();
    }

    public int OutputId(int inputToken) => _vocabulary.OutputId(inputToken);
    public int InputIdFromOutput(int outputToken) => _vocabulary.InputIdFromOutput(outputToken);
}

internal enum LexicalTokenKind { Word, Punctuation }
internal readonly record struct LexicalToken(LexicalTokenKind Kind, string Text);

internal enum TrainingTask { Language, Perception, Tool }

[Flags]
internal enum PerceptionFields { None = 0, Intent = 1, Affect = 2, Expected = 4, All = Intent | Affect | Expected }

internal sealed record TrainingSample(
    int[] Tokens,
    int PositionOffset,
    int FirstTargetIndex,
    TrainingTask Task = TrainingTask.Language,
    string Bucket = "",
    string Source = "synthetic",
    TurnPerception? PerceptionTarget = null,
    string Family = "",
    PerceptionFields TargetFields = PerceptionFields.All,
    int? UnlikelihoodTargetIndex = null,
    int? UnlikelihoodToken = null,
    double UnlikelihoodWeight = 0.0);


internal static class DialogueKeys
{
    public static string Catalog(DialogueIntent intent, ResponseTone tone) => $"{intent}|{tone}";

    public static string StateInput(string input, NpcState state) =>
        $"{state.Rapport}|{(int)state.Mood}|{(int)state.LastIntent}|{(int)state.LastAffect}|" +
        $"{(int)state.ActiveTopic}|{(int)state.ActiveGoal}|{input}";

    public static string Example(
        string input,
        NpcState state,
        TurnPerception perception,
        TurnDecision decision,
        ResponseTone tone) =>
        $"{StateInput(input, state)}|{(int)perception.Intent}|{(int)perception.Affect}|" +
        $"{perception.ResponseExpected}|{(int)decision.Action}|{(int)tone}";
}
