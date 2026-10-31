using System.Text;

namespace HackWizard.Core.Acpi;

/// <summary>Minimal ACPI Machine Language encoder for the opcodes HackWizard's SSDTs need (ACPI 6.x §20).</summary>
public static class Aml
{
    public const byte ZeroOp = 0x00, OneOp = 0x01, OnesOp = 0xFF;
    public const byte Local0 = 0x60, Arg0 = 0x68, Arg1 = 0x69, Arg2 = 0x6A;
    public const byte ObjTypeInteger = 0x01, ObjTypeDevice = 0x06, ObjTypeMethod = 0x08, ObjTypeProcessor = 0x0C;

    public static byte[] Cat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)]; var o = 0;
        foreach (var p in parts) { System.Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }

    public static byte[] B(params byte[] b) => b;

    /// <summary>PkgLength encoding; the encoded length includes the PkgLength bytes themselves.</summary>
    public static byte[] PkgLength(int contentLength)
    {
        if (contentLength + 1 <= 0x3F) return new[] { (byte)(contentLength + 1) };
        for (var n = 2; n <= 4; n++)
        {
            var total = contentLength + n;
            var max = (1 << (4 + 8 * (n - 1))) - 1;
            if (total > max) continue;
            var r = new byte[n];
            r[0] = (byte)(((n - 1) << 6) | (total & 0x0F));
            for (var i = 1; i < n; i++) r[i] = (byte)((total >> (4 + 8 * (i - 1))) & 0xFF);
            return r;
        }
        throw new ArgumentOutOfRangeException(nameof(contentLength), "AML package too large.");
    }

    private static byte[] WithPkg(byte[] opcode, params byte[][] content)
    {
        var body = Cat(content);
        return Cat(opcode, PkgLength(body.Length), body);
    }

    public static byte[] NameSeg(string seg)
    {
        seg = seg.TrimEnd('_');
        if (seg.Length == 0) seg = "_";
        if (seg.Length > 4) throw new ArgumentException($"Invalid ACPI name segment '{seg}'.");
        foreach (var c in seg) if (!(c is '_' or >= 'A' and <= 'Z' or >= '0' and <= '9')) throw new ArgumentException($"Invalid ACPI name segment '{seg}'.");
        return Encoding.ASCII.GetBytes(seg.PadRight(4, '_'));
    }

    /// <summary>Encodes "\_SB.PCI0.LPC0", "^EC0", "_OSI" or "\".</summary>
    public static byte[] NameString(string path)
    {
        var prefix = new List<byte>();
        var p = path;
        if (p.StartsWith('\\')) { prefix.Add(0x5C); p = p[1..]; }
        while (p.StartsWith('^')) { prefix.Add(0x5E); p = p[1..]; }
        var segs = p.Length == 0 ? Array.Empty<string>() : p.Split('.');
        byte[] namePath = segs.Length switch
        {
            0 => new byte[] { 0x00 },
            1 => NameSeg(segs[0]),
            2 => Cat(new byte[] { 0x2E }, NameSeg(segs[0]), NameSeg(segs[1])),
            _ => Cat(new byte[] { 0x2F, (byte)segs.Length }, Cat(segs.Select(NameSeg).ToArray()))
        };
        return Cat(prefix.ToArray(), namePath);
    }

    /// <summary>Normalises a Windows BIOS device name ("\_SB_.PCI0.LPCB") to DSL form ("\_SB.PCI0.LPCB").</summary>
    public static string DslPath(string path) =>
        (path.StartsWith('\\') ? "\\" : "") + string.Join('.', path.TrimStart('\\').Split('.', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('_') is { Length: > 0 } t ? t : "_"));

    public static byte[] Integer(ulong v) => v switch
    {
        0 => new byte[] { ZeroOp },
        1 => new byte[] { OneOp },
        ulong.MaxValue => new byte[] { OnesOp },
        <= 0xFF => new byte[] { 0x0A, (byte)v },
        <= 0xFFFF => Cat(new byte[] { 0x0B }, BitConverter.GetBytes((ushort)v)),
        <= 0xFFFFFFFF => Cat(new byte[] { 0x0C }, BitConverter.GetBytes((uint)v)),
        _ => Cat(new byte[] { 0x0E }, BitConverter.GetBytes(v))
    };

    public static byte[] Str(string s) => Cat(new byte[] { 0x0D }, Encoding.ASCII.GetBytes(s), new byte[] { 0x00 });

    /// <summary>EISA ID compression, e.g. "APP0002" / "PNP0C09".</summary>
    public static byte[] EisaId(string id)
    {
        if (id.Length != 7) throw new ArgumentException("EISA ID must be 7 characters.");
        var vendor = (ushort)(((id[0] - 0x40) & 0x1F) << 10 | ((id[1] - 0x40) & 0x1F) << 5 | ((id[2] - 0x40) & 0x1F));
        var product = Convert.ToUInt16(id[3..], 16);
        return new byte[] { 0x0C, (byte)(vendor >> 8), (byte)vendor, (byte)(product >> 8), (byte)product };
    }

    public static byte[] Scope(string path, params byte[][] body) => WithPkg(new byte[] { 0x10 }, NameString(path), Cat(body));
    public static byte[] Device(string name, params byte[][] body) => WithPkg(new byte[] { 0x5B, 0x82 }, NameString(name), Cat(body));
    public static byte[] Name(string name, byte[] value) => Cat(new byte[] { 0x08 }, NameString(name), value);
    public static byte[] Method(string name, int argCount, bool serialized, params byte[][] body) =>
        WithPkg(new byte[] { 0x14 }, NameString(name), new[] { (byte)((argCount & 7) | (serialized ? 0x08 : 0)) }, Cat(body));
    public static byte[] If(byte[] predicate, params byte[][] body) => WithPkg(new byte[] { 0xA0 }, predicate, Cat(body));
    public static byte[] Else(params byte[][] body) => WithPkg(new byte[] { 0xA1 }, Cat(body));
    public static byte[] Return(byte[] value) => Cat(new byte[] { 0xA4 }, value);
    public static byte[] External(string path, byte objectType, byte argCount) => Cat(new byte[] { 0x15 }, NameString(path), new[] { objectType, argCount });
    public static byte[] Buffer(params byte[] data) => WithPkg(new byte[] { 0x11 }, Integer((ulong)data.Length), data);
    public static byte[] Package(params byte[][] elements)
    {
        if (elements.Length > 255) throw new ArgumentException("Package too large for PackageOp.");
        return WithPkg(new byte[] { 0x12 }, new[] { (byte)elements.Length }, Cat(elements));
    }
    public static byte[] LEqual(byte[] a, byte[] b) => Cat(new byte[] { 0x93 }, a, b);
    public static byte[] LNot(byte[] a) => Cat(new byte[] { 0x92 }, a);
    public static byte[] Store(byte[] src, byte[] dst) => Cat(new byte[] { 0x70 }, src, dst);
    public static byte[] Match(byte[] pkg, byte op1, byte[] obj1, byte op2, byte[] obj2, byte[] start) => Cat(new byte[] { 0x89 }, pkg, new[] { op1 }, obj1, new[] { op2 }, obj2, start);
    public static byte[] Call(string name, params byte[][] args) => Cat(NameString(name), Cat(args));

    // ---- Resource descriptors (ACPI 6.x §6.4) ----
    public static byte[] ResIo16(ushort min, ushort max, byte align, byte len) => new byte[] { 0x47, 0x01, (byte)min, (byte)(min >> 8), (byte)max, (byte)(max >> 8), align, len };
    public static byte[] ResIrqNoFlags(int irq) { var mask = (ushort)(1 << irq); return new byte[] { 0x22, (byte)mask, (byte)(mask >> 8) }; }
    public static byte[] ResMemory32Fixed(bool readWrite, uint baseAddr, uint length) =>
        Cat(new byte[] { 0x86, 0x09, 0x00, (byte)(readWrite ? 1 : 0) }, BitConverter.GetBytes(baseAddr), BitConverter.GetBytes(length));
    public static byte[] ResourceTemplate(params byte[][] descriptors) => Buffer(Cat(Cat(descriptors), new byte[] { 0x79, 0x00 }));

    /// <summary>Builds a complete table with a valid checksum.</summary>
    public static byte[] Table(string signature, byte revision, string oemId, string oemTableId, uint oemRevision, byte[] body)
    {
        var header = new byte[36];
        Encoding.ASCII.GetBytes(signature).CopyTo(header, 0);
        BitConverter.GetBytes((uint)(36 + body.Length)).CopyTo(header, 4);
        header[8] = revision;
        Encoding.ASCII.GetBytes(oemId.PadRight(6)[..6]).CopyTo(header, 10);
        Encoding.ASCII.GetBytes(oemTableId.PadRight(8)[..8]).CopyTo(header, 16);
        BitConverter.GetBytes(oemRevision).CopyTo(header, 24);
        Encoding.ASCII.GetBytes("HKWZ").CopyTo(header, 28);
        BitConverter.GetBytes(1u).CopyTo(header, 32);
        var table = Cat(header, body);
        byte sum = 0; foreach (var b in table) sum += b;
        table[9] = (byte)(0x100 - sum);
        return table;
    }

    public static bool ChecksumValid(byte[] table) { byte s = 0; foreach (var b in table) s += b; return s == 0; }
}
