using System.Collections;
using System.Globalization;

namespace HackWizard.Core.Plist;

public sealed class PlistException : Exception
{
    public PlistException(string message) : base(message) { }
}

/// <summary>Base type of all property-list nodes. Nodes are mutable; use Clone() for copies.</summary>
public abstract class PNode
{
    public abstract PNode Clone();
    public abstract string TypeName { get; }
}

/// <summary>Ordered dictionary (key order is preserved, which keeps output deterministic).</summary>
public sealed class PDict : PNode, IEnumerable<KeyValuePair<string, PNode>>
{
    private readonly List<KeyValuePair<string, PNode>> _items = new();
    public override string TypeName => "dict";
    public int Count => _items.Count;
    public IEnumerable<string> Keys => _items.Select(i => i.Key);

    public PNode? this[string key]
    {
        get { var i = IndexOf(key); return i < 0 ? null : _items[i].Value; }
        set
        {
            if (value is null) { Remove(key); return; }
            var i = IndexOf(key);
            if (i < 0) _items.Add(new(key, value)); else _items[i] = new(key, value);
        }
    }

    public int IndexOf(string key)
    {
        for (var i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].Key, key, StringComparison.Ordinal)) return i;
        return -1;
    }

    public bool ContainsKey(string key) => IndexOf(key) >= 0;
    public bool Remove(string key) { var i = IndexOf(key); if (i < 0) return false; _items.RemoveAt(i); return true; }

    public T Get<T>(string key) where T : PNode =>
        this[key] as T ?? throw new PlistException($"Key '{key}' is missing or is not a {typeof(T).Name}.");

    public PDict Dict(string key) => Get<PDict>(key);
    public PArray Array(string key) => Get<PArray>(key);
    public string? Str(string key) => (this[key] as PString)?.Value;

    public PDict Set(string key, PNode value) { this[key] = value; return this; }
    public PDict Set(string key, string value) => Set(key, new PString(value));
    public PDict Set(string key, bool value) => Set(key, new PBool(value));
    public PDict Set(string key, long value) => Set(key, new PInteger(value));
    public PDict Set(string key, byte[] value) => Set(key, new PData(value));

    public override PNode Clone()
    {
        var d = new PDict();
        foreach (var kv in _items) d._items.Add(new(kv.Key, kv.Value.Clone()));
        return d;
    }

    public IEnumerator<KeyValuePair<string, PNode>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class PArray : PNode, IEnumerable<PNode>
{
    public List<PNode> Items { get; } = new();
    public override string TypeName => "array";
    public PArray() { }
    public PArray(IEnumerable<PNode> items) => Items.AddRange(items);
    public int Count => Items.Count;
    public PNode this[int i] => Items[i];
    public void Add(PNode n) => Items.Add(n);
    public override PNode Clone() => new PArray(Items.Select(i => i.Clone()));
    public IEnumerator<PNode> GetEnumerator() => Items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class PString : PNode
{
    public string Value { get; }
    public PString(string v) => Value = v ?? string.Empty;
    public override string TypeName => "string";
    public override PNode Clone() => new PString(Value);
    public override string ToString() => Value;
}

public sealed class PInteger : PNode
{
    public long Value { get; }
    public PInteger(long v) => Value = v;
    public override string TypeName => "integer";
    public override PNode Clone() => new PInteger(Value);
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class PReal : PNode
{
    public double Value { get; }
    public PReal(double v) => Value = v;
    public override string TypeName => "real";
    public override PNode Clone() => new PReal(Value);
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public sealed class PBool : PNode
{
    public bool Value { get; }
    public PBool(bool v) => Value = v;
    public override string TypeName => "bool";
    public override PNode Clone() => new PBool(Value);
    public override string ToString() => Value ? "true" : "false";
}

public sealed class PData : PNode
{
    public byte[] Value { get; }
    public PData(byte[] v) => Value = v ?? System.Array.Empty<byte>();
    public static PData FromHex(string hex) => new(Convert.FromHexString(hex.Replace(" ", "")));
    public override string TypeName => "data";
    public override PNode Clone() => new PData((byte[])Value.Clone());
    public override string ToString() => Convert.ToHexString(Value);
}

public sealed class PDate : PNode
{
    public DateTime Value { get; }
    public PDate(DateTime v) => Value = v.ToUniversalTime();
    public override string TypeName => "date";
    public override PNode Clone() => new PDate(Value);
    public override string ToString() => Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Dotted-path helpers ("Kernel.Quirks.ProvideCurrentCpuInfo").</summary>
public static class PlistPath
{
    public static string[] Split(string path) => path.Split('.', StringSplitOptions.RemoveEmptyEntries);

    public static PNode? Get(PDict root, string path)
    {
        PNode? cur = root;
        foreach (var part in Split(path))
        {
            if (cur is not PDict d) return null;
            cur = d[part];
        }
        return cur;
    }

    /// <summary>Sets a value. When mustExist is true the leaf key must already exist with the same type
    /// (guards against writing keys the matching OpenCore version does not know about).</summary>
    public static void Set(PDict root, string path, PNode value, bool mustExist = true)
    {
        var parts = Split(path);
        var cur = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var next = cur[parts[i]];
            if (next is null)
            {
                if (mustExist) throw new PlistException($"Path '{path}': section '{parts[i]}' not found in Sample.plist.");
                next = new PDict();
                cur[parts[i]] = next;
            }
            cur = next as PDict ?? throw new PlistException($"Path '{path}': '{parts[i]}' is not a dict.");
        }
        var leaf = parts[^1];
        var existing = cur[leaf];
        if (mustExist)
        {
            if (existing is null) throw new PlistException($"Path '{path}' does not exist in Sample.plist (OpenCore schema changed?).");
            if (existing.GetType() != value.GetType())
                throw new PlistException($"Path '{path}' type mismatch: Sample has {existing.TypeName}, rule supplies {value.TypeName}.");
        }
        cur[leaf] = value;
    }
}
