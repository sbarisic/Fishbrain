using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Fishbrain;

[AttributeUsage(AttributeTargets.Method)]
public sealed class GameToolAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>An allow-list of synchronous game functions available to a loaded Brain.</summary>
public sealed class ToolRegistry
{
    private static readonly Regex NamePattern = new("^[A-Z][A-Z0-9]{0,31}$", RegexOptions.CultureInvariant);
    private readonly HashSet<string> _trainedNames;
    private readonly Dictionary<string, ToolMethod> _methods = new(StringComparer.Ordinal);

    internal ToolRegistry(IEnumerable<string> trainedNames) =>
        _trainedNames = new HashSet<string>(trainedNames, StringComparer.Ordinal);

    public ToolRegistry Register(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var additions = new List<ToolMethod>();

        foreach (var method in instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            var attribute = method.GetCustomAttribute<GameToolAttribute>();
            if (attribute is null) continue;

            var name = attribute.Name;
            if (!NamePattern.IsMatch(name))
                throw new InvalidOperationException($"Tool name '{name}' must be 1-32 uppercase alphanumeric characters.");
            if (!_trainedNames.Contains(name))
                throw new InvalidOperationException($"Tool '{name}' was not present in this model's training data.");
            if (_methods.ContainsKey(name) || additions.Any(x => x.Name == name))
                throw new InvalidOperationException($"Tool '{name}' is already registered.");
            if (method.IsGenericMethodDefinition)
                throw new InvalidOperationException($"Tool '{name}' cannot be generic.");
            if (method.ReturnType != typeof(string) && method.ReturnType != typeof(int) && method.ReturnType != typeof(bool))
                throw new InvalidOperationException($"Tool '{name}' must return string, int, or bool.");

            var parameters = method.GetParameters();
            if (parameters.Any(p => p.ParameterType != typeof(string) && p.ParameterType != typeof(int)))
                throw new InvalidOperationException($"Tool '{name}' parameters must be string identifiers or non-negative integers.");

            additions.Add(new ToolMethod(name, instance, method, parameters.Select(p => p.ParameterType).ToArray()));
        }

        foreach (var addition in additions) _methods.Add(addition.Name, addition);
        return this;
    }

    internal IReadOnlyCollection<string> RegisteredNames => _methods.Keys;

    internal bool TryGet(string name, out ToolMethod method) => _methods.TryGetValue(name, out method!);

    internal bool TryInvoke(string name, IReadOnlyList<string> rawArguments, out string result)
    {
        result = string.Empty;
        if (!_methods.TryGetValue(name, out var tool) || rawArguments.Count != tool.ParameterTypes.Length) return false;

        try
        {
            var arguments = new object[rawArguments.Count];
            for (var i = 0; i < arguments.Length; i++)
            {
                var raw = rawArguments[i];
                if (tool.ParameterTypes[i] == typeof(int))
                {
                    if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedNumber) || parsedNumber < 0)
                        return false;
                    arguments[i] = parsedNumber;
                }
                else
                {
                    if (raw.Length is < 1 or > 32 || raw.Any(c => !Tokenizer.IsIdentifierCharacter(c))) return false;
                    arguments[i] = raw;
                }
            }

            var invocationResult = tool.Method.Invoke(tool.Instance, arguments);
            result = invocationResult switch
            {
                int number => number.ToString(CultureInfo.InvariantCulture),
                bool boolean => boolean ? "TRUE" : "FALSE",
                string text => Tokenizer.Normalize(text),
                _ => string.Empty
            };

            return result.Length is > 0 and <= 64;
        }
        catch
        {
            result = string.Empty;
            return false;
        }
    }

    internal sealed record ToolMethod(string Name, object Instance, MethodInfo Method, Type[] ParameterTypes);
}
