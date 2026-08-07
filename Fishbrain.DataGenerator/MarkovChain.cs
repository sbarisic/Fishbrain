namespace Fishbrain.DataGenerator;

/// <summary>A tiny second-order, word-level Markov chain with weighted transitions.</summary>
internal sealed class MarkovChain
{
    private const string Start = "\u0001START";
    private const string End = "\u0001END";
    private readonly Dictionary<State, Dictionary<string, int>> _transitions = [];

    public MarkovChain(IEnumerable<string> sentences)
    {
        var count = 0;
        foreach (var sentence in sentences)
        {
            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            count++;

            var first = Start;
            var second = Start;
            foreach (var next in words.Append(End))
            {
                var state = new State(first, second);
                if (!_transitions.TryGetValue(state, out var choices))
                    _transitions[state] = choices = new Dictionary<string, int>(StringComparer.Ordinal);
                choices[next] = choices.GetValueOrDefault(next) + 1;
                first = second;
                second = next;
            }
        }

        if (count == 0) throw new ArgumentException("A Markov chain requires at least one nonempty sentence.");
    }

    public string? Generate(Random random, int maximumWords, out bool terminated)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (maximumWords <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWords));

        var words = new List<string>();
        var state = new State(Start, Start);
        for (var i = 0; i < maximumWords; i++)
        {
            if (!_transitions.TryGetValue(state, out var choices)) break;
            var next = PickWeighted(choices, random);
            if (next == End)
            {
                terminated = true;
                return string.Join(' ', words);
            }

            words.Add(next);
            state = new State(state.Second, next);
        }

        terminated = false;
        return null;
    }

    internal IReadOnlyDictionary<string, int> GetTransitions(string first, string second)
    {
        var state = new State(first, second);
        return _transitions.TryGetValue(state, out var choices)
            ? choices
            : new Dictionary<string, int>();
    }

    internal static string StartMarker => Start;
    internal static string EndMarker => End;

    private static string PickWeighted(IReadOnlyDictionary<string, int> choices, Random random)
    {
        var total = choices.Values.Sum();
        var selected = random.Next(total);
        foreach (var choice in choices.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            selected -= choice.Value;
            if (selected < 0) return choice.Key;
        }
        throw new InvalidOperationException("Markov transition weights are invalid.");
    }

    private readonly record struct State(string First, string Second);
}
