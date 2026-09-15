using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fishbrain;

public sealed class DemoWorldState
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, CachedExecution> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _merchantStock = new(StringComparer.Ordinal)
    {
        ["IRON SWORD"] = 20,
        ["HEALTH POTION"] = 50,
        ["ROPE"] = 30
    };
    private readonly Dictionary<string, int> _playerInventory = new(StringComparer.Ordinal)
    {
        ["HEALTH POTION"] = 1,
        ["ROPE"] = 2
    };
    private readonly Dictionary<string, int> _prices = new(StringComparer.Ordinal)
    {
        ["IRON SWORD"] = 25,
        ["HEALTH POTION"] = 8,
        ["ROPE"] = 3
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
    private string _currentLocation = "VILLAGE MARKET";

    public string CurrentLocation
    {
        get
        {
            lock (_gate)
            {
                return _currentLocation;
            }
        }
        set
        {
            var normalized = DialogueText.Normalize(value);
            if (normalized.Length is < 1 or > 128)
                throw new ArgumentException("Current location must contain 1-128 canonical characters.", nameof(value));
            lock (_gate) _currentLocation = normalized;
        }
    }

    public int Balance
    {
        get
        {
            lock (_gate)
            {
                return _balance;
            }
        }
    }

    public IReadOnlyDictionary<string, int> Inventory
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(_playerInventory));
            }
        }
    }

    internal GameToolResult Once(GameToolInvocation invocation, Func<GameToolResult> action)
    {
        var key = invocation.ToolName + "\u001f" + invocation.IdempotencyKey;
        var fingerprint = InvocationFingerprint(invocation);
        var candidate = new CachedExecution(fingerprint, new Lazy<GameToolResult>(action,
            LazyThreadSafetyMode.ExecutionAndPublication));
        var cached = _completed.GetOrAdd(key, candidate);
        if (cached.Fingerprint == fingerprint) return cached.Result.Value;
        var fields = invocation.Arguments.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (fields.Count > 0) fields["REASON"] = "IDEMPOTENCY KEY REUSED WITH DIFFERENT ARGUMENTS";
        return new GameToolResult(false, new ReadOnlyDictionary<string, string>(fields), "IDEMPOTENCY_CONFLICT");
    }

    private static string InvocationFingerprint(GameToolInvocation invocation)
    {
        var canonical = string.Join("\u001e", invocation.Arguments.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key.Length}:{item.Key}{item.Value.Length}:{item.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record CachedExecution(string Fingerprint, Lazy<GameToolResult> Result);

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
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}: {item.Value} IN STOCK"));
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
            try
            {
                total = checked(price * quantity);
            }
            catch (OverflowException)
            {
                return TradeFailure("AMOUNT_OVERFLOW", item, quantity, "AMOUNT TOO LARGE");
            }
            if (_balance < total) return TradeFailure("INSUFFICIENT_FUNDS", item, quantity, "INSUFFICIENT FUNDS");
            int inventory;
            try
            {
                inventory = checked(_playerInventory.GetValueOrDefault(item) + quantity);
            }
            catch (OverflowException)
            {
                return TradeFailure("INVENTORY_OVERFLOW", item, quantity, "INVENTORY TOO LARGE");
            }
            var newStock = stock - quantity;
            var newBalance = _balance - total;
            _merchantStock[item] = newStock;
            _playerInventory[item] = inventory;
            _balance = newBalance;
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
            try
            {
                total = checked(Math.Max(1, price / 2) * quantity);
            }
            catch (OverflowException)
            {
                return TradeFailure("AMOUNT_OVERFLOW", item, quantity, "AMOUNT TOO LARGE");
            }
            int balance;
            int stock;
            try
            {
                balance = checked(_balance + total);
                stock = checked(_merchantStock.GetValueOrDefault(item) + quantity);
            }
            catch (OverflowException)
            {
                return TradeFailure("BALANCE_OVERFLOW", item, quantity, "BALANCE TOO LARGE");
            }
            _playerInventory[item] -= quantity;
            if (_playerInventory[item] == 0) _playerInventory.Remove(item);
            _merchantStock[item] = stock;
            _balance = balance;
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
