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
            var schema = SnapshotSchema(tool.Schema);
            ValidateSchema(schema);
            if (!result.TryAdd(schema.Name, new RegisteredTool(tool, schema)))
                throw new ArgumentException($"Duplicate game tool '{schema.Name}'.", nameof(tools));
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
        try
        {
            result = tool.Execute(invocation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var recoverableFields = tool.Schema.ResultFields
                .Where(field => invocation.Arguments.ContainsKey(field.Name))
                .ToDictionary(field => field.Name, field => invocation.Arguments[field.Name], StringComparer.Ordinal);
            return new GameToolResult(false,
                new ReadOnlyDictionary<string, string>(recoverableFields),
                ExceptionCode(exception));
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
        if (template is null)
        {
            if (!result.Success) return "THE GAME TOOL FAILED.";
            throw new InvalidDataException($"Tool '{schema.Name}' has no eligible success response template.");
        }
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
        if (schema.Parameters.Any(parameter => parameter is null || !Enum.IsDefined(parameter.Type)) ||
            schema.ResultFields.Any(field => field is null || !Enum.IsDefined(field.Type)) ||
            schema.PermittedResponseTemplates.Any(template => template is null || template.RequiredFields is null ||
                string.IsNullOrWhiteSpace(template.Text)))
            throw new ArgumentException($"Tool '{schema.Name}' contains invalid schema members.");
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

    private static ToolSchema SnapshotSchema(ToolSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(schema.Parameters);
        ArgumentNullException.ThrowIfNull(schema.ResultFields);
        ArgumentNullException.ThrowIfNull(schema.PermittedResponseTemplates);
        return new ToolSchema(schema.Name,
            Array.AsReadOnly(schema.Parameters.Select(parameter => parameter is null
                ? null! : new ToolParameter(parameter.Name, parameter.Type, parameter.Required)).ToArray()),
            Array.AsReadOnly(schema.ResultFields.Select(field => field is null
                ? null! : new ToolResultField(field.Name, field.Type, field.Required)).ToArray()),
            schema.MutatesWorldState,
            Array.AsReadOnly(schema.PermittedResponseTemplates.Select(template => template is null
                ? null! : new ToolResponseTemplate(template.Id, template.Text,
                    template.RequiredFields is null ? null! : Array.AsReadOnly(template.RequiredFields.ToArray()),
                    template.ForSuccess)).ToArray()));
    }

    private sealed class RegisteredTool(IGameTool implementation, ToolSchema schema) : IGameTool
    {
        public ToolSchema Schema { get; } = schema;
        public GameToolResult Execute(GameToolInvocation invocation) => implementation.Execute(invocation);
    }

    internal static void ValidateArguments(ToolSchema schema, IReadOnlyDictionary<string, string> arguments)
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
        if (result.Success && result.ErrorCode is not null || !result.Success &&
            (result.ErrorCode is null || result.ErrorCode.Length > 64 || !NamePattern.IsMatch(result.ErrorCode)))
            throw new InvalidDataException($"Tool '{schema.Name}' returned invalid success/error metadata.");
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
        if (string.IsNullOrWhiteSpace(name) || !NamePattern.IsMatch(name))
            throw new ArgumentException($"Invalid {kind} name '{name}'.");
        return name;
    }

    private static void Unique(IEnumerable<string> names, string tool, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (names.Any(name => !seen.Add(name))) throw new ArgumentException($"Tool '{tool}' has duplicate {kind} names.");
    }

    private static string ExceptionCode(Exception exception)
    {
        var type = new string(exception.GetType().Name.ToUpperInvariant()
            .Where(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_').ToArray());
        if (type.Length == 0) type = "UNKNOWN";
        var code = "TOOL_EXCEPTION_" + type;
        return code.Length <= 64 ? code : code[..64];
    }
}

/// <summary>Authoritative, thread-safe demo world shared by every registered demo tool.</summary>
