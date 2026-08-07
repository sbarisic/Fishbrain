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
}

/// <summary>Authoritative, thread-safe demo world shared by every registered demo tool.</summary>
public sealed class DemoWorldState
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, GameToolResult> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _merchantStock = new(StringComparer.Ordinal)
    {
        ["IRON SWORD"] = 20, ["HEALTH POTION"] = 50, ["ROPE"] = 30
    };
    private readonly Dictionary<string, int> _playerInventory = new(StringComparer.Ordinal)
    {
        ["HEALTH POTION"] = 1, ["ROPE"] = 2
    };
    private readonly Dictionary<string, int> _prices = new(StringComparer.Ordinal)
    {
        ["IRON SWORD"] = 25, ["HEALTH POTION"] = 8, ["ROPE"] = 3
    };
    private readonly Dictionary<string, string> _locations = new(StringComparer.Ordinal)
    {
        ["INN"] = "NORTH BY THE FOUNTAIN",
        ["THE INN"] = "NORTH BY THE FOUNTAIN",
        ["MARKET"] = "EAST OF THE GATE",
        ["THE MARKET"] = "EAST OF THE GATE",
        ["HELL"] = "DOWN BELOW",
        ["CASTLE"] = "ON THE HILL",
        ["THE CASTLE"] = "ON THE HILL",
        ["ZAGREB"] = "IN CROATIA, EUROPE"
    };
    private readonly Dictionary<string, string> _facts = new(StringComparer.Ordinal)
    {
        ["CASTLE"] = "THE CASTLE STANDS ON THE HILL ABOVE THE VILLAGE",
        ["VILLAGE"] = "THIS VILLAGE GUARDS THE EASTERN ROAD",
        ["REACTOR"] = "THE REACTOR POWERS THE STATION"
    };
    private int _balance = 100;

    public string CurrentLocation { get; set; } = "VILLAGE MARKET";

    public int Balance { get { lock (_gate) return _balance; } }
    public IReadOnlyDictionary<string, int> Inventory
    {
        get { lock (_gate) return new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(_playerInventory)); }
    }

    internal GameToolResult Once(GameToolInvocation invocation, Func<GameToolResult> action) =>
        _completed.GetOrAdd(invocation.IdempotencyKey, _ => action());

    internal GameToolResult Locate(string place) => _locations.TryGetValue(place, out var direction)
        ? Success(("PLACE", place), ("DIRECTION", direction))
        : Failure("NOT_FOUND", ("PLACE", place), ("REASON", "UNKNOWN PLACE"));

    internal GameToolResult WorldFact(string topic) => _facts.TryGetValue(topic, out var fact)
        ? Success(("TOPIC", topic), ("FACT", fact))
        : Failure("NOT_FOUND", ("TOPIC", topic), ("REASON", "UNKNOWN FACT"));

    internal GameToolResult ListWares()
    {
        lock (_gate)
        {
            var wares = string.Join(", ", _merchantStock.Where(item => item.Value > 0)
                .OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key} ({item.Value})"));
            return Success(("WARES", wares));
        }
    }

    internal GameToolResult ListInventory()
    {
        lock (_gate)
        {
            var items = _playerInventory.Count == 0 ? "NOTHING" : string.Join(", ", _playerInventory
                .Where(item => item.Value > 0).OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Value} {item.Key}"));
            return Success(("ITEMS", items));
        }
    }

    internal GameToolResult Price(string item)
    {
        lock (_gate)
            return _prices.TryGetValue(item, out var price)
                ? Success(("ITEM", item), ("PRICE", Number(price)), ("CURRENCY", "GOLD"))
                : Failure("UNKNOWN_ITEM", ("ITEM", item), ("REASON", "UNKNOWN ITEM"));
    }

    internal GameToolResult Buy(string item, int quantity)
    {
        lock (_gate)
        {
            if (quantity <= 0) return TradeFailure("INVALID_QUANTITY", item, quantity, "QUANTITY MUST BE POSITIVE");
            if (!_prices.TryGetValue(item, out var price) || !_merchantStock.TryGetValue(item, out var stock))
                return TradeFailure("UNKNOWN_ITEM", item, quantity, "UNKNOWN ITEM");
            if (stock < quantity) return TradeFailure("OUT_OF_STOCK", item, quantity, "OUT OF STOCK");
            int total;
            try { total = checked(price * quantity); }
            catch (OverflowException) { return TradeFailure("AMOUNT_OVERFLOW", item, quantity, "AMOUNT TOO LARGE"); }
            if (_balance < total) return TradeFailure("INSUFFICIENT_FUNDS", item, quantity, "INSUFFICIENT FUNDS");
            _merchantStock[item] -= quantity;
            _playerInventory[item] = checked(_playerInventory.GetValueOrDefault(item) + quantity);
            _balance -= total;
            return Success(("ITEM", item), ("QUANTITY", Number(quantity)), ("PRICE", Number(total)),
                ("CURRENCY", "GOLD"), ("BALANCE", Number(_balance)));
        }
    }

    internal GameToolResult Sell(string item, int quantity)
    {
        lock (_gate)
        {
            if (quantity <= 0) return TradeFailure("INVALID_QUANTITY", item, quantity, "QUANTITY MUST BE POSITIVE");
            if (!_prices.TryGetValue(item, out var price))
                return TradeFailure("UNKNOWN_ITEM", item, quantity, "UNKNOWN ITEM");
            if (_playerInventory.GetValueOrDefault(item) < quantity)
                return TradeFailure("INSUFFICIENT_INVENTORY", item, quantity, "YOU DO NOT HAVE THAT MANY");
            int total;
            try { total = checked(Math.Max(1, price / 2) * quantity); }
            catch (OverflowException) { return TradeFailure("AMOUNT_OVERFLOW", item, quantity, "AMOUNT TOO LARGE"); }
            try { _balance = checked(_balance + total); }
            catch (OverflowException) { return TradeFailure("BALANCE_OVERFLOW", item, quantity, "BALANCE TOO LARGE"); }
            _playerInventory[item] -= quantity;
            if (_playerInventory[item] == 0) _playerInventory.Remove(item);
            _merchantStock[item] = checked(_merchantStock.GetValueOrDefault(item) + quantity);
            return Success(("ITEM", item), ("QUANTITY", Number(quantity)), ("PRICE", Number(total)),
                ("CURRENCY", "GOLD"), ("BALANCE", Number(_balance)));
        }
    }

    private static GameToolResult TradeFailure(string code, string item, int quantity, string reason) =>
        Failure(code, ("ITEM", item), ("QUANTITY", Number(Math.Max(0, quantity))), ("REASON", reason));

    internal static GameToolResult Success(params (string Name, string Value)[] fields) =>
        new(true, Fields(fields));
    internal static GameToolResult Failure(string code, params (string Name, string Value)[] fields) =>
        new(false, Fields(fields), code);
    internal static IReadOnlyDictionary<string, string> Fields(params (string Name, string Value)[] values) =>
        new ReadOnlyDictionary<string, string>(values.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal));
    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

public static class DemoGameTools
{
    public static GameToolRegistry CreateMerchant() => CreateMerchant(new DemoWorldState());

    public static GameToolRegistry CreateMerchant(DemoWorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return new GameToolRegistry(new IGameTool[]
        {
            new DemoTool(world, LocationSchema, invocation => world.Locate(invocation.Arguments["PLACE"])),
            new DemoTool(world, ListWaresSchema, _ => world.ListWares()),
            new DemoTool(world, PriceSchema, invocation => world.Price(invocation.Arguments["ITEM"])),
            new DemoTool(world, BuySchema, invocation => world.Buy(invocation.Arguments["ITEM"], Quantity(invocation))),
            new DemoTool(world, SellSchema, invocation => world.Sell(invocation.Arguments["ITEM"], Quantity(invocation))),
            new DemoTool(world, BalanceSchema, _ => DemoWorldState.Success(("BALANCE", DemoWorldState.Number(world.Balance)), ("CURRENCY", "GOLD"))),
            new DemoTool(world, InventorySchema, _ => world.ListInventory()),
            new DemoTool(world, CurrentLocationSchema, _ => DemoWorldState.Success(("LOCATION", DialogueText.Normalize(world.CurrentLocation)))),
            new DemoTool(world, WorldFactSchema, invocation => world.WorldFact(invocation.Arguments["TOPIC"]))
        });
    }

    private static int Quantity(GameToolInvocation invocation) =>
        int.Parse(invocation.Arguments["QUANTITY"], NumberStyles.None, CultureInfo.InvariantCulture);

    private sealed class DemoTool(
        DemoWorldState world,
        ToolSchema schema,
        Func<GameToolInvocation, GameToolResult> execute) : IGameTool
    {
        public ToolSchema Schema { get; } = schema;
        public GameToolResult Execute(GameToolInvocation invocation) =>
            world.Once(invocation, () => execute(invocation));
    }

    private static readonly ToolResultField Reason = new("REASON", ToolValueType.String, false);
    private static readonly ToolResultField Item = new("ITEM", ToolValueType.String);
    private static readonly ToolResultField QuantityField = new("QUANTITY", ToolValueType.Integer);
    private static readonly ToolResultField PriceField = new("PRICE", ToolValueType.Integer, false);
    private static readonly ToolResultField Currency = new("CURRENCY", ToolValueType.String, false);
    private static readonly ToolResultField Balance = new("BALANCE", ToolValueType.Integer, false);

    private static readonly ToolSchema LocationSchema = new(
        "LOOKUP_LOCATION", [new("PLACE", ToolValueType.String)],
        [new("PLACE", ToolValueType.String), new("DIRECTION", ToolValueType.String, false), Reason], false,
        [new("FOUND", "{PLACE} IS {DIRECTION}.", ["PLACE", "DIRECTION"]),
         new("NOT_FOUND", "I CANNOT LOCATE {PLACE}.", ["PLACE"], false)]);
    private static readonly ToolSchema ListWaresSchema = new(
        "LIST_WARES", [], [new("WARES", ToolValueType.String)], false,
        [new("LIST", "I HAVE {WARES}.", ["WARES"])]);
    private static readonly ToolSchema PriceSchema = new(
        "LOOKUP_PRICE", [new("ITEM", ToolValueType.String)], [Item, PriceField, Currency, Reason], false,
        [new("PRICE", "{ITEM} COSTS {PRICE} {CURRENCY}.", ["ITEM", "PRICE", "CURRENCY"]),
         new("UNKNOWN", "I CANNOT PRICE {ITEM}: {REASON}.", ["ITEM", "REASON"], false)]);
    private static readonly ToolSchema BuySchema = TradeSchema("BUY", "BOUGHT");
    private static readonly ToolSchema SellSchema = TradeSchema("SELL", "SOLD");
    private static ToolSchema TradeSchema(string name, string past) => new(
        name, [new("ITEM", ToolValueType.String), new("QUANTITY", ToolValueType.Integer)],
        [Item, QuantityField, PriceField, Currency, Balance, Reason], true,
        [new("DONE", $"YOU {past} {{QUANTITY}} {{ITEM}} FOR {{PRICE}} {{CURRENCY}}. YOUR BALANCE IS {{BALANCE}} {{CURRENCY}}.",
             ["QUANTITY", "ITEM", "PRICE", "CURRENCY", "BALANCE"]),
         new("FAILED", "I CANNOT COMPLETE THE {QUANTITY} {ITEM} TRANSACTION: {REASON}.",
             ["QUANTITY", "ITEM", "REASON"], false)]);
    private static readonly ToolSchema BalanceSchema = new(
        "GET_BALANCE", [], [Balance, Currency], false,
        [new("BALANCE", "YOU HAVE {BALANCE} {CURRENCY}.", ["BALANCE", "CURRENCY"])]);
    private static readonly ToolSchema InventorySchema = new(
        "LIST_INVENTORY", [], [new("ITEMS", ToolValueType.String)], false,
        [new("INVENTORY", "YOU CARRY {ITEMS}.", ["ITEMS"])]);
    private static readonly ToolSchema CurrentLocationSchema = new(
        "GET_CURRENT_LOCATION", [], [new("LOCATION", ToolValueType.String)], false,
        [new("LOCATION", "YOU ARE AT {LOCATION}.", ["LOCATION"])]);
    private static readonly ToolSchema WorldFactSchema = new(
        "LOOKUP_WORLD_FACT", [new("TOPIC", ToolValueType.String)],
        [new("TOPIC", ToolValueType.String), new("FACT", ToolValueType.String, false), Reason], false,
        [new("FACT", "{FACT}.", ["FACT"]),
         new("UNKNOWN", "I DO NOT HAVE A RELIABLE FACT ABOUT {TOPIC}.", ["TOPIC"], false)]);
}
