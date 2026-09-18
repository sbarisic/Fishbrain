using System.Text;
using System.Text.Json;

namespace Fishbrain;

internal sealed record ProtocolMessage(string Type, string? Text, string? Name, Dictionary<string, string>? Arguments);

/// <summary>Byte-level schema constraints. Grammar validity never grants execution eligibility.</summary>
internal sealed class ProtocolGrammar
{
    private enum Kind { Literal, String, Number, Choice }
    private sealed record Atom(Kind Kind, byte[][] Choices, int Limit = 256);
    private readonly record struct State(int Atom = 0, int Offset = 0, int Escape = 0, int Unicode = 0,
        int Bytes = 0, int Utf8 = 0, int Min = 128, int Max = 191, long Number = 0, ulong Choices = ulong.MaxValue, bool Dead = false);
    private readonly Atom[][] _paths;
    private State[] _states;
    internal bool Complete => _states.Where((s, i) => !s.Dead && s.Atom == _paths[i].Length).Any();

    internal ProtocolGrammar(IReadOnlyCollection<ToolSchema> schemas)
    {
        var paths = new List<Atom[]> { new Atom[] { Literal("{\"type\":\"text\",\"text\":\""), new(Kind.String, [], 1024), Literal("}") } };
        foreach (var schema in schemas.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var optional = schema.Parameters.Count(p => !p.Required);
            if (optional > 4 || schema.Parameters.Count > 16 || schemas.Count > 32) throw new ArgumentException("Tool schema exceeds decoding bounds.");
            for (var subset = 0; subset < (1 << optional); subset++)
            {
                var atoms = new List<Atom> { Literal("{\"type\":\"tool_call\",\"name\":" + JsonSerializer.Serialize(schema.Name) + ",\"arguments\":{") };
                var first = true; var option = 0;
                foreach (var p in schema.Parameters)
                {
                    if (!p.Required && (subset & (1 << option++)) == 0) continue;
                    atoms.Add(Literal((first ? "" : ",") + JsonSerializer.Serialize(p.Name) + ":")); first = false;
                    if (p.EnumValues is { Count: > 0 } values)
                    {
                        if (values.Count > 32) throw new ArgumentException("Enum exceeds decoding bound.");
                        atoms.Add(new(Kind.Choice, values.Select(v => Encoding.UTF8.GetBytes(JsonDefaults.Write(v))).ToArray()));
                    }
                    else if (p.Type == ToolValueType.String) { atoms.Add(Literal("\"")); atoms.Add(new(Kind.String, [], 1024)); }
                    else if (p.Type == ToolValueType.Integer) atoms.Add(new(Kind.Number, []));
                    else atoms.Add(new(Kind.Choice, ["true"u8.ToArray(), "false"u8.ToArray()]));
                }
                atoms.Add(Literal("}}")); paths.Add(atoms.ToArray());
            }
        }
        _paths = paths.ToArray(); _states = new State[_paths.Length];
        for (var i = 0; i < _states.Length; i++) _states[i] = new State(Choices: ulong.MaxValue, Min: 128, Max: 191);
    }
    private static Atom Literal(string s) => new(Kind.Literal, [Encoding.UTF8.GetBytes(s)]);
    internal bool Allows(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return false;
        for (var i = 0; i < _paths.Length; i++) if (!_states[i].Dead && Advance(_paths[i], _states[i], bytes, out _)) return true;
        return false;
    }
    internal void Append(ReadOnlySpan<byte> bytes)
    {
        var any = false;
        for (var i = 0; i < _paths.Length; i++)
        {
            if (_states[i].Dead) continue;
            if (Advance(_paths[i], _states[i], bytes, out var state)) { _states[i] = state; any = true; }
            else _states[i] = _states[i] with { Dead = true };
        }
        if (!any) throw new InvalidDataException("Generated token violates protocol grammar.");
    }
    private static bool Advance(Atom[] path, State state, ReadOnlySpan<byte> bytes, out State result)
    {
        foreach (var b in bytes)
        {
            var consumed = false;
            while (!consumed)
            {
                if (state.Atom >= path.Length) { result = state; return false; }
                var atom = path[state.Atom];
                switch (atom.Kind)
                {
                    case Kind.Literal:
                        if (b != atom.Choices[0][state.Offset]) { result = state; return false; }
                        state = state with { Offset = state.Offset + 1 }; consumed = true;
                        if (state.Offset == atom.Choices[0].Length) state = Next(state);
                        break;
                    case Kind.Choice:
                        ulong mask = 0; var done = false;
                        for (var i = 0; i < atom.Choices.Length; i++)
                            if ((state.Choices & (1UL << i)) != 0 && state.Offset < atom.Choices[i].Length && atom.Choices[i][state.Offset] == b)
                            { mask |= 1UL << i; done |= state.Offset + 1 == atom.Choices[i].Length; }
                        if (mask == 0) { result = state; return false; }
                        state = done ? Next(state) : state with { Offset = state.Offset + 1, Choices = mask }; consumed = true;
                        break;
                    case Kind.Number:
                        if (b is >= 48 and <= 57)
                        {
                            var number = state.Number * 10 + b - 48;
                            if (number > int.MaxValue || state.Offset > 0 && state.Number == 0) { result = state; return false; }
                            state = state with { Number = number, Offset = state.Offset + 1 }; consumed = true;
                        }
                        else if (state.Offset == 0) { result = state; return false; }
                        else state = Next(state);
                        break;
                    case Kind.String:
                        if (state.Bytes > atom.Limit) { result = state; return false; }
                        if (state.Unicode > 0)
                        {
                            if (!(b is >= 48 and <= 57 or >= 65 and <= 70 or >= 97 and <= 102)) { result = state; return false; }
                            state = state with { Unicode = state.Unicode - 1, Bytes = state.Bytes + 1 };
                        }
                        else if (state.Escape > 0)
                        {
                            if (!"\"\\/bfnrtu"u8.Contains(b)) { result = state; return false; }
                            state = state with { Escape = 0, Unicode = b == 'u' ? 4 : 0, Bytes = state.Bytes + 1 };
                        }
                        else if (state.Utf8 > 0)
                        {
                            if (b < state.Min || b > state.Max) { result = state; return false; }
                            state = state with { Utf8 = state.Utf8 - 1, Min = 128, Max = 191, Bytes = state.Bytes + 1 };
                        }
                        else if (b == '"') state = Next(state);
                        else if (b == '\\') state = state with { Escape = 1, Bytes = state.Bytes + 1 };
                        else if (b < 32 || b >= 128 && b < 194 || b > 244) { result = state; return false; }
                        else state = state with { Bytes = state.Bytes + 1, Utf8 = b < 128 ? 0 : b < 224 ? 1 : b < 240 ? 2 : 3,
                            Min = b == 224 ? 160 : b == 240 ? 144 : 128, Max = b == 237 ? 159 : b == 244 ? 143 : 191 };
                        consumed = true; break;
                }
            }
        }
        result = state; return true;
    }
    private static State Next(State state) => new(Atom: state.Atom + 1, Choices: ulong.MaxValue, Min: 128, Max: 191);

    internal static ProtocolMessage Parse(string text, GameToolRegistry tools)
    {
        using var doc = JsonDocument.Parse(text); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct().Count() != root.EnumerateObject().Count())
            throw new InvalidDataException("Invalid protocol object.");
        var type = root.GetProperty("type").GetString();
        if (type == "text" && root.EnumerateObject().Count() == 2)
            return new(type, root.GetProperty("text").GetString() ?? throw new InvalidDataException("Missing text."), null, null);
        if (type != "tool_call" || root.EnumerateObject().Count() != 3) throw new InvalidDataException("Invalid protocol kind.");
        var name = root.GetProperty("name").GetString()!;
        if (!tools.TryGet(name, out var tool)) throw new InvalidDataException("Unavailable tool.");
        var arguments = new Dictionary<string, string>();
        foreach (var property in root.GetProperty("arguments").EnumerateObject())
        {
            var parameter = tool.Schema.Parameters.SingleOrDefault(p => p.Name == property.Name) ?? throw new InvalidDataException("Unknown argument.");
            if (parameter.Type == ToolValueType.String && property.Value.ValueKind != JsonValueKind.String ||
                parameter.Type == ToolValueType.Integer && (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out _)) ||
                parameter.Type == ToolValueType.Boolean && property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Wrong JSON argument type.");
            if (!arguments.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText()))
                throw new InvalidDataException("Duplicate argument.");
        }
        GameToolRegistry.ValidateArguments(tool.Schema, arguments);
        return new(type, null, name, arguments);
    }
}
