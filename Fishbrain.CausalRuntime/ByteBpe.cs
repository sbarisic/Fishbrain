using System.Text;

namespace Fishbrain;

public sealed record BpeDefinition(string[] Pieces, int[][] Merges);

/// <summary>Byte BPE without text normalization or recognition of literal control-token spellings.</summary>
public sealed class ByteBpe
{
    public const int Pad = 0, Bos = 1, Eos = 2, System = 3, Player = 4, Assistant = 5, Call = 6, Result = 7, End = 8, Document = 9;
    public const int Reserved = 16;
    private readonly byte[][] _pieces;
    private readonly int[] _bytes = new int[256];
    private readonly Dictionary<long, (int Rank, int Id)> _merges = [];
    internal BpeDefinition Definition { get; }
    public int Count => _pieces.Length;
    internal ReadOnlySpan<byte> Piece(int id) => _pieces[id];

    public ByteBpe(BpeDefinition definition)
    {
        if (definition.Pieces.Length is < 272 or > 32768) throw new InvalidDataException("Invalid BPE vocabulary size.");
        Definition = new((string[])definition.Pieces.Clone(), definition.Merges.Select(m => (int[])m.Clone()).ToArray());
        _pieces = definition.Pieces.Select(Convert.FromHexString).ToArray();
        Array.Fill(_bytes, -1);
        for (var id = 0; id < Count; id++)
        {
            if (id < Reserved && _pieces[id].Length != 0 || id >= Reserved && _pieces[id].Length == 0)
                throw new InvalidDataException("Invalid reserved BPE entry.");
            if (id >= Reserved && _pieces[id].Length == 1)
            {
                if (_bytes[_pieces[id][0]] != -1) throw new InvalidDataException("Duplicate byte token.");
                _bytes[_pieces[id][0]] = id;
            }
        }
        if (_bytes.Any(i => i < 0)) throw new InvalidDataException("BPE must cover all 256 bytes.");
        var available = _bytes.ToHashSet();
        for (var rank = 0; rank < definition.Merges.Length; rank++)
        {
            var merge = definition.Merges[rank];
            if (merge.Length != 3 || merge.Any(id => id < Reserved || id >= Count) || !available.Contains(merge[0]) || !available.Contains(merge[1]) ||
                !available.Add(merge[2]) || !_pieces[merge[0]].Concat(_pieces[merge[1]]).SequenceEqual(_pieces[merge[2]]) ||
                !_merges.TryAdd(Key(merge[0], merge[1]), (rank, merge[2]))) throw new InvalidDataException("Invalid BPE merge graph.");
        }
        if (available.Count != Count - Reserved) throw new InvalidDataException("Unreachable BPE entries.");
    }

    public int[] Encode(string text)
    {
        // Strict encoding rejects unpaired UTF-16 surrogates instead of silently replacing them.
        var ids = new UTF8Encoding(false, true).GetBytes(text).Select(b => _bytes[b]).ToList();
        while (ids.Count > 1)
        {
            var best = int.MaxValue; var index = -1; var merged = 0;
            for (var i = 0; i + 1 < ids.Count; i++)
                if (_merges.TryGetValue(Key(ids[i], ids[i + 1]), out var item) && item.Rank < best)
                { best = item.Rank; index = i; merged = item.Id; }
            if (index < 0) break;
            ids[index] = merged; ids.RemoveAt(index + 1);
        }
        return ids.ToArray();
    }

    public string Decode(IEnumerable<int> ids)
    {
        using var bytes = new MemoryStream();
        foreach (var id in ids)
        {
            if (id < Reserved || id >= Count) throw new ArgumentException("Control token cannot be decoded as text.");
            bytes.Write(_pieces[id]);
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }
    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;
}
