using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fishbrain;

public enum ToolValueType { String, Integer, Boolean }

public sealed record ToolParameter(string Name, ToolValueType Type, bool Required = true, IReadOnlyList<string>? EnumValues = null);
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
    IReadOnlyList<ToolResponseTemplate> PermittedResponseTemplates)
{
    public string Description { get; init; } = "";
}

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

public sealed record ToolExecutionContext(ReplyRequest Request, IReadOnlyList<ChatMessage> RetainedMessages);
public interface IContextualGameTool : IGameTool
{
    GameToolResult Execute(GameToolInvocation invocation, ToolExecutionContext context);
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
    public GameToolRegistry WithTools(IEnumerable<IGameTool> tools) => new(_tools.Values.Concat(tools));
    public bool Contains(string name) => _tools.ContainsKey(name);
    internal bool TryGet(string name, out IGameTool tool) => _tools.TryGetValue(name, out tool!);

    internal static string IdempotencyKey(string conversationId, string turnId, int callOrdinal = 0)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonDefaults.Write(new[] { conversationId, turnId, callOrdinal.ToString(CultureInfo.InvariantCulture) })));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static GameToolResult InvokeValidated(IGameTool tool, GameToolInvocation invocation, ToolExecutionContext? context = null)
    {
        ValidateArguments(tool.Schema, invocation.Arguments);
        GameToolResult result;
        try
        {
            result = tool is IContextualGameTool contextual && context is not null ? contextual.Execute(invocation, context) : tool.Execute(invocation);
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
        try { ArgumentNullException.ThrowIfNull(result); ValidateResult(tool.Schema, result); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        { return new(false, new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()), "INVALID_TOOL_RESULT"); }
        return result with
        {
            Fields = new ReadOnlyDictionary<string, string>(result.Fields.ToDictionary(
                item => CanonicalName(item.Key, "result field"),
                item => item.Value, StringComparer.Ordinal))
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
        // Replace only placeholders in the trusted template. A value containing
        // braces is data and must not become a second template substitution.
        return Regex.Replace(template.Text, "\\{([A-Z][A-Z0-9_]*)\\}",
            match => result.Fields[match.Groups[1].Value], RegexOptions.CultureInvariant);
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
        if (schema.Parameters.Any(p => p.EnumValues is not null && (p.Type != ToolValueType.String || p.EnumValues.Count is < 1 or > 32 ||
            p.EnumValues.Distinct(StringComparer.Ordinal).Count() != p.EnumValues.Count || p.EnumValues.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256))))
            throw new ArgumentException("Invalid tool enum values.");
        Unique(schema.ResultFields.Select(field => CanonicalName(field.Name, "result field")), schema.Name, "result field");
        Unique(schema.PermittedResponseTemplates.Select(template => CanonicalName(template.Id, "template")), schema.Name, "template");
        var fields = schema.ResultFields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var template in schema.PermittedResponseTemplates)
        {
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
                ? null! : new ToolParameter(parameter.Name, parameter.Type, parameter.Required,
                    parameter.EnumValues is null ? null : Array.AsReadOnly(parameter.EnumValues.ToArray()))).ToArray()),
            Array.AsReadOnly(schema.ResultFields.Select(field => field is null
                ? null! : new ToolResultField(field.Name, field.Type, field.Required)).ToArray()),
            schema.MutatesWorldState,
            Array.AsReadOnly(schema.PermittedResponseTemplates.Select(template => template is null
                ? null! : new ToolResponseTemplate(template.Id, template.Text,
                    template.RequiredFields is null ? null! : Array.AsReadOnly(template.RequiredFields.ToArray()),
                    template.ForSuccess)).ToArray())) { Description = schema.Description };
    }

    private sealed class RegisteredTool(IGameTool implementation, ToolSchema schema) : IContextualGameTool
    {
        public ToolSchema Schema { get; } = schema;
        public GameToolResult Execute(GameToolInvocation invocation) => implementation.Execute(invocation);
        public GameToolResult Execute(GameToolInvocation invocation, ToolExecutionContext context) =>
            implementation is IContextualGameTool contextual ? contextual.Execute(invocation, context) : implementation.Execute(invocation);
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
            if (value.Length > 256) throw new ArgumentException("Tool arguments cannot exceed 256 characters.");
            if (parameter.EnumValues is not null && !parameter.EnumValues.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException($"{parameter.Name} is not an allowed enum value.");
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
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(c => c == '\0'))
            throw new ArgumentException($"{name} must contain 1-4096 non-null characters.");
        if (type == ToolValueType.Integer &&
            (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0))
            throw new ArgumentException($"{name} must be a non-negative integer.");
        if (type == ToolValueType.Boolean && value is not "TRUE" and not "FALSE" and not "true" and not "false")
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
