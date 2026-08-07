using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fishbrain;

public enum ToolValueType { String, Integer, Boolean }

public sealed record ToolParameter(string Name, ToolValueType Type, bool Required = true);
public sealed record ToolResultField(string Name, ToolValueType Type, bool Required = true);
public sealed record ToolResponseTemplate(
    string Id,
    string Text,
    IReadOnlyList<string> RequiredFields,
    bool ForSuccess = true);

public sealed record ToolSchema(
    string Name,
    IReadOnlyList<ToolParameter> Parameters,
    IReadOnlyList<ToolResultField> ResultFields,
    bool MutatesWorldState,
    IReadOnlyList<ToolResponseTemplate> PermittedResponseTemplates);

public sealed record GameToolInvocation(
    string ToolName,
    IReadOnlyDictionary<string, string> Arguments,
    string IdempotencyKey);

public sealed record GameToolResult(
    bool Success,
    IReadOnlyDictionary<string, string> Fields,
    string? ErrorCode = null);

public interface IGameTool
{
    ToolSchema Schema { get; }
    GameToolResult Execute(GameToolInvocation invocation);
}

public sealed class GameToolRegistry
{
    private static readonly Regex NamePattern = new("^[A-Z][A-Z0-9_]{0,47}$", RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, IGameTool> _tools;

    public static GameToolRegistry Empty { get; } = new([]);

    public GameToolRegistry(IEnumerable<IGameTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var result = new Dictionary<string, IGameTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ValidateSchema(tool.Schema);
            if (!result.TryAdd(tool.Schema.Name, tool))
                throw new ArgumentException($"Duplicate game tool '{tool.Schema.Name}'.", nameof(tools));
        }
        _tools = new ReadOnlyDictionary<string, IGameTool>(result);
    }

    public IReadOnlyCollection<ToolSchema> Schemas => _tools.Values.Select(tool => tool.Schema).ToArray();
    public bool Contains(string name) => _tools.ContainsKey(name);

    internal bool TryGet(string name, out IGameTool tool) => _tools.TryGetValue(name, out tool!);

    internal static string IdempotencyKey(string conversationId, string turnId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(conversationId + "\u001f" + turnId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static GameToolResult InvokeValidated(IGameTool tool, GameToolInvocation invocation)
    {
        ValidateArguments(tool.Schema, invocation.Arguments);
        GameToolResult result;
        try { result = tool.Execute(invocation); }
        catch (Exception exception)
        {
            var recoverableFields = tool.Schema.ResultFields
                .Where(field => invocation.Arguments.ContainsKey(field.Name))
                .ToDictionary(field => field.Name, field => invocation.Arguments[field.Name], StringComparer.Ordinal);
            return new GameToolResult(false,
                new ReadOnlyDictionary<string, string>(recoverableFields),
                "TOOL_EXCEPTION_" + exception.GetType().Name.ToUpperInvariant());
        }
        ArgumentNullException.ThrowIfNull(result);
        ValidateResult(tool.Schema, result);
        return result with
        {
            Fields = new ReadOnlyDictionary<string, string>(result.Fields.ToDictionary(
                item => CanonicalName(item.Key, "result field"),
                item => DialogueText.Normalize(item.Value), StringComparer.Ordinal))
        };
    }

    internal static string Render(ToolSchema schema, GameToolResult result)
    {
        var template = schema.PermittedResponseTemplates.FirstOrDefault(item => item.ForSuccess == result.Success &&
            item.RequiredFields.All(result.Fields.ContainsKey));
        if (template is null) throw new InvalidDataException($"Tool '{schema.Name}' has no eligible response template.");
        var text = template.Text;
        foreach (var field in template.RequiredFields)
            text = text.Replace("{" + field + "}", result.Fields[field], StringComparison.Ordinal);
        if (Regex.IsMatch(text, "\\{[A-Z][A-Z0-9_]*\\}", RegexOptions.CultureInvariant))
            throw new InvalidDataException($"Tool template '{template.Id}' contains an unresolved field.");
        return DialogueText.Normalize(text);
    }

    private static void ValidateSchema(ToolSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        CanonicalName(schema.Name, "tool");
        if (schema.Parameters is null || schema.ResultFields is null || schema.PermittedResponseTemplates is null ||
            schema.PermittedResponseTemplates.Count == 0)
            throw new ArgumentException($"Tool '{schema.Name}' has an incomplete schema.");
        Unique(schema.Parameters.Select(parameter => CanonicalName(parameter.Name, "parameter")), schema.Name, "parameter");
        Unique(schema.ResultFields.Select(field => CanonicalName(field.Name, "result field")), schema.Name, "result field");
        Unique(schema.PermittedResponseTemplates.Select(template => CanonicalName(template.Id, "template")), schema.Name, "template");
        var fields = schema.ResultFields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var template in schema.PermittedResponseTemplates)
        {
            if (template.Text != template.Text.ToUpperInvariant())
                throw new ArgumentException($"Tool template '{template.Id}' must be normalized uppercase text.");
            if (template.RequiredFields.Any(field => !fields.Contains(field)))
                throw new ArgumentException($"Tool template '{template.Id}' references an undeclared result field.");
            var placeholders = Regex.Matches(template.Text, "\\{([A-Z][A-Z0-9_]*)\\}", RegexOptions.CultureInvariant)
                .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            if (!placeholders.SetEquals(template.RequiredFields))
                throw new ArgumentException($"Tool template '{template.Id}' fields do not match its placeholders.");
        }
    }

    private static void ValidateArguments(ToolSchema schema, IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var declared = schema.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        if (arguments.Keys.Any(key => !declared.ContainsKey(key)))
            throw new ArgumentException($"Invocation for '{schema.Name}' contains an undeclared argument.");
        foreach (var parameter in schema.Parameters)
        {
            if (!arguments.TryGetValue(parameter.Name, out var value))
            {
                if (parameter.Required) throw new ArgumentException($"Invocation for '{schema.Name}' is missing {parameter.Name}.");
                continue;
            }
            ValidateValue(parameter.Type, value, parameter.Name);
        }
    }

    private static void ValidateResult(ToolSchema schema, GameToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result.Fields);
        var declared = schema.ResultFields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        if (result.Fields.Keys.Any(key => !declared.ContainsKey(key)))
            throw new InvalidDataException($"Tool '{schema.Name}' returned an undeclared field.");
        foreach (var field in schema.ResultFields)
        {
            if (!result.Fields.TryGetValue(field.Name, out var value))
            {
                if (field.Required && result.Success)
                    throw new InvalidDataException($"Tool '{schema.Name}' omitted required field {field.Name}.");
                continue;
            }
            ValidateValue(field.Type, value, field.Name);
        }
    }

    private static void ValidateValue(ToolValueType type, string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new ArgumentException($"{name} must contain 1-128 characters.");
        if (value != DialogueText.Normalize(value))
            throw new ArgumentException($"{name} must be normalized uppercase text.");
        if (type == ToolValueType.Integer &&
            (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0))
            throw new ArgumentException($"{name} must be a non-negative integer.");
        if (type == ToolValueType.Boolean && value is not "TRUE" and not "FALSE")
            throw new ArgumentException($"{name} must be TRUE or FALSE.");
    }

    private static string CanonicalName(string name, string kind)
    {
        if (!NamePattern.IsMatch(name)) throw new ArgumentException($"Invalid {kind} name '{name}'.");
        return name;
    }

    private static void Unique(IEnumerable<string> names, string tool, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (names.Any(name => !seen.Add(name))) throw new ArgumentException($"Tool '{tool}' has duplicate {kind} names.");
    }

    private static IReadOnlyDictionary<string, string> EmptyFields { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}

public static class DemoGameTools
{
    public static GameToolRegistry CreateMerchant() => new(new IGameTool[]
    {
        new LocationTool(), new MerchantTool("LIST_WARES", false), new MerchantTool("LOOKUP_PRICE", false),
        new MerchantTool("BUY", true), new MerchantTool("SELL", true)
    });

    private sealed class LocationTool : IGameTool
    {
        public ToolSchema Schema { get; } = new(
            "LOOKUP_LOCATION",
            [new("PLACE", ToolValueType.String)],
            [new("PLACE", ToolValueType.String), new("DIRECTION", ToolValueType.String)],
            false,
            [new("FOUND", "{PLACE} IS {DIRECTION}.", ["PLACE", "DIRECTION"]),
             new("NOT_FOUND", "I CANNOT LOCATE {PLACE}.", ["PLACE"], false)]);

        public GameToolResult Execute(GameToolInvocation invocation)
        {
            var place = invocation.Arguments["PLACE"];
            var direction = place switch { "INN" => "NORTH BY THE FOUNTAIN", "MARKET" => "EAST OF THE GATE", _ => null };
            return direction is null
                ? new(false, Fields(("PLACE", place)), "NOT_FOUND")
                : new(true, Fields(("PLACE", place), ("DIRECTION", direction)));
        }
    }

    private sealed class MerchantTool(string name, bool mutates) : IGameTool
    {
        private readonly ConcurrentDictionary<string, GameToolResult> _completed = new(StringComparer.Ordinal);
        private int _stock = 20;

        public ToolSchema Schema { get; } = CreateSchema(name, mutates);

        public GameToolResult Execute(GameToolInvocation invocation) =>
            _completed.GetOrAdd(invocation.IdempotencyKey, _ => ExecuteOnce(invocation));

        private GameToolResult ExecuteOnce(GameToolInvocation invocation)
        {
            return invocation.ToolName switch
            {
                "LIST_WARES" => new(true, Fields(("WARES", "IRON SWORD, HEALTH POTION, ROPE"))),
                "LOOKUP_PRICE" => Price(invocation.Arguments["ITEM"]),
                "BUY" => Trade(invocation, buying: true),
                "SELL" => Trade(invocation, buying: false),
                _ => new(false, Fields(("ERROR", "UNKNOWN TOOL")), "UNKNOWN_TOOL")
            };
        }

        private static GameToolResult Price(string item) => new(true,
            Fields(("ITEM", item), ("PRICE", item == "IRON SWORD" ? "25" : item == "HEALTH POTION" ? "8" : "3"), ("CURRENCY", "GOLD")));

        private GameToolResult Trade(GameToolInvocation invocation, bool buying)
        {
            var item = invocation.Arguments["ITEM"];
            var quantity = int.Parse(invocation.Arguments["QUANTITY"], CultureInfo.InvariantCulture);
            if (buying && quantity > _stock)
                return new(false, Fields(("ITEM", item), ("QUANTITY", quantity.ToString(CultureInfo.InvariantCulture))), "OUT_OF_STOCK");
            Interlocked.Add(ref _stock, buying ? -quantity : quantity);
            return new(true, Fields(("ITEM", item), ("QUANTITY", quantity.ToString(CultureInfo.InvariantCulture))));
        }

        private static ToolSchema CreateSchema(string name, bool mutates) => name switch
        {
            "LIST_WARES" => new(name, [], [new("WARES", ToolValueType.String)], false,
                [new("LIST", "I HAVE {WARES}.", ["WARES"])]),
            "LOOKUP_PRICE" => new(name, [new("ITEM", ToolValueType.String)],
                [new("ITEM", ToolValueType.String), new("PRICE", ToolValueType.Integer), new("CURRENCY", ToolValueType.String)], false,
                [new("PRICE", "{ITEM} COSTS {PRICE} {CURRENCY}.", ["ITEM", "PRICE", "CURRENCY"])]),
            "BUY" or "SELL" => new(name,
                [new("ITEM", ToolValueType.String), new("QUANTITY", ToolValueType.Integer)],
                [new("ITEM", ToolValueType.String), new("QUANTITY", ToolValueType.Integer)], mutates,
                [new("DONE", name == "BUY" ? "YOU BOUGHT {QUANTITY} {ITEM}." : "YOU SOLD {QUANTITY} {ITEM}.", ["QUANTITY", "ITEM"]),
                 new("FAILED", "I CANNOT COMPLETE THAT {ITEM} TRANSACTION.", ["ITEM"], false)]),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
    }

    private static IReadOnlyDictionary<string, string> Fields(params (string Name, string Value)[] values) =>
        new ReadOnlyDictionary<string, string>(values.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal));
}
