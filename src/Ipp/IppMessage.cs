using System.Buffers.Binary;
using System.Text;

namespace PrintDesk.Ipp;

/// <summary>IPP/1.1 value tags (RFC 8010 §3.5).</summary>
static class Tag
{
    // delimiters
    public const byte OperationGroup = 0x01, JobGroup = 0x02, End = 0x03, PrinterGroup = 0x04, Unsupported = 0x05;
    // out-of-band
    public const byte UnsupportedValue = 0x10, Unknown = 0x12, NoValue = 0x13;
    // integer family
    public const byte Integer = 0x21, Boolean = 0x22, Enum = 0x23;
    // octet-string family
    public const byte OctetString = 0x30, DateTime = 0x31, Resolution = 0x32, RangeOfInteger = 0x33, BegCollection = 0x34,
        TextWithLanguage = 0x35, NameWithLanguage = 0x36, EndCollection = 0x37;
    // character-string family
    public const byte TextWithoutLanguage = 0x41, NameWithoutLanguage = 0x42, Keyword = 0x44, Uri = 0x45,
        UriScheme = 0x46, Charset = 0x47, NaturalLanguage = 0x48, MimeMediaType = 0x49, MemberAttrName = 0x4A;
}

/// <summary>One attribute value: its tag and raw bytes, with typed readers.</summary>
readonly record struct IppValue(byte Tag, byte[] Raw)
{
    public bool IsString => Tag is >= 0x40 and <= 0x5F || Tag is PrintDesk.Ipp.Tag.TextWithLanguage or PrintDesk.Ipp.Tag.NameWithLanguage;

    public string AsString()
    {
        if (Tag is PrintDesk.Ipp.Tag.TextWithLanguage or PrintDesk.Ipp.Tag.NameWithLanguage && Raw.Length >= 4)
        {
            // u16 language length, language, u16 text length, text
            int langLen = BinaryPrimitives.ReadUInt16BigEndian(Raw);
            int textAt = 2 + langLen;
            if (textAt + 2 <= Raw.Length)
            {
                int textLen = BinaryPrimitives.ReadUInt16BigEndian(Raw.AsSpan(textAt));
                return Encoding.UTF8.GetString(Raw, textAt + 2, Math.Min(textLen, Raw.Length - textAt - 2));
            }
        }
        return Encoding.UTF8.GetString(Raw);
    }

    public int AsInt() => Raw.Length >= 4 ? BinaryPrimitives.ReadInt32BigEndian(Raw) : Raw.Length == 1 ? Raw[0] : 0;
    public bool AsBool() => Raw.Length >= 1 && Raw[0] != 0;

    /// <summary>A resolution value: cross-feed, feed, units (3 = dots per inch, 4 = dots per cm).</summary>
    public (int X, int Y, int Units) AsResolution() => Raw.Length >= 9
        ? (BinaryPrimitives.ReadInt32BigEndian(Raw), BinaryPrimitives.ReadInt32BigEndian(Raw.AsSpan(4)), Raw[8])
        : (0, 0, 0);

    public (int Lo, int Hi) AsRange() => Raw.Length >= 8
        ? (BinaryPrimitives.ReadInt32BigEndian(Raw), BinaryPrimitives.ReadInt32BigEndian(Raw.AsSpan(4)))
        : (0, 0);

    public override string ToString() => IsString ? AsString() : Raw.Length == 4 ? AsInt().ToString() : $"<{Tag:X2}>";
}

/// <summary>An attribute group (operation, job, printer): name to values. Collections are skipped (flattened away).</summary>
sealed class IppGroup(byte tag)
{
    public byte Tag { get; } = tag;
    public Dictionary<string, List<IppValue>> Attrs { get; } = new(StringComparer.Ordinal);

    public List<IppValue> Get(string name) => Attrs.TryGetValue(name, out var v) ? v : [];
    public string? Str(string name) { var v = Get(name); return v.Count > 0 ? v[0].AsString() : null; }
    public int? Int(string name) { var v = Get(name); return v.Count > 0 && v[0].Raw.Length == 4 ? v[0].AsInt() : null; }
    public List<string> Strs(string name) => Get(name).Where(v => v.IsString).Select(v => v.AsString()).ToList();
    public List<int> Ints(string name) => Get(name).Where(v => v.Raw.Length == 4).Select(v => v.AsInt()).ToList();
}

/// <summary>A decoded IPP response.</summary>
sealed class IppResponse
{
    public ushort Status { get; init; }
    public int RequestId { get; init; }
    public List<IppGroup> Groups { get; } = [];

    /// <summary>successful-ok (0x0000) through successful-ok-events-complete (0x0007): everything below 0x0100.</summary>
    public bool Ok => Status < 0x0100;
    public IppGroup? First(byte tag) => Groups.FirstOrDefault(g => g.Tag == tag);
    public IEnumerable<IppGroup> All(byte tag) => Groups.Where(g => g.Tag == tag);

    public string StatusText => Status switch
    {
        0x0000 => "ok",
        0x0001 => "ok (some attributes ignored)",
        0x0400 => "bad request",
        0x0401 => "forbidden",
        0x0402 => "not authenticated",
        0x0403 => "not authorized",
        0x0404 => "not possible",
        0x0405 => "timeout",
        0x0406 => "not found",
        0x0407 => "gone",
        0x040A => "document format not supported",
        0x040B => "attributes or values not supported",
        0x0500 => "printer internal error",
        0x0503 => "printer busy",
        0x0506 => "printer not accepting jobs",
        0x0507 => "printer busy",
        _ => $"status 0x{Status:X4}",
    };

    public static IppResponse Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8) throw new IppException("The printer sent an empty or truncated reply.");
        var r = new IppResponse
        {
            Status = BinaryPrimitives.ReadUInt16BigEndian(b[2..]),
            RequestId = BinaryPrimitives.ReadInt32BigEndian(b[4..]),
        };
        int i = 8, depth = 0;
        IppGroup? group = null;
        string last = "";
        while (i < b.Length)
        {
            byte tag = b[i++];
            if (tag == Tag.End) break;
            if (tag < 0x10)
            {
                group = new IppGroup(tag);
                r.Groups.Add(group);
                continue;
            }
            if (i + 2 > b.Length) break;
            int nameLen = BinaryPrimitives.ReadUInt16BigEndian(b[i..]); i += 2;
            if (i + nameLen + 2 > b.Length) break;
            string name = Encoding.ASCII.GetString(b.Slice(i, nameLen)); i += nameLen;
            int valLen = BinaryPrimitives.ReadUInt16BigEndian(b[i..]); i += 2;
            if (i + valLen > b.Length) break;
            var raw = b.Slice(i, valLen).ToArray(); i += valLen;

            // Collections (media-col and friends): skip their members; only top-level attributes are kept.
            if (tag == Tag.BegCollection) { depth++; continue; }
            if (tag == Tag.EndCollection) { if (depth > 0) depth--; continue; }
            if (depth > 0 || group is null) continue;

            if (nameLen > 0) last = name;
            if (last.Length == 0) continue;
            if (!group.Attrs.TryGetValue(last, out var list)) group.Attrs[last] = list = [];
            list.Add(new IppValue(tag, raw));
        }
        return r;
    }
}

/// <summary>Builds an IPP request body.</summary>
sealed class IppRequest
{
    private readonly MemoryStream _s = new();
    private static int s_nextId = 1;

    public IppRequest(ushort operation, string printerUri)
    {
        _s.WriteByte(1); _s.WriteByte(1);                       // version 1.1
        U16(operation);
        U32(Interlocked.Increment(ref s_nextId));
        _s.WriteByte(Tag.OperationGroup);
        Str(Tag.Charset, "attributes-charset", "utf-8");
        Str(Tag.NaturalLanguage, "attributes-natural-language", "en");
        Str(Tag.Uri, "printer-uri", printerUri);
        Str(Tag.NameWithoutLanguage, "requesting-user-name", Environment.UserName);
    }

    public IppRequest Group(byte tag) { _s.WriteByte(tag); return this; }

    public IppRequest Str(byte tag, string name, string value) => Raw(tag, name, Encoding.UTF8.GetBytes(value));

    /// <summary>A multi-valued attribute: the first value carries the name, the rest an empty name.</summary>
    public IppRequest Strs(byte tag, string name, params string[] values)
    {
        for (int k = 0; k < values.Length; k++) Raw(tag, k == 0 ? name : "", Encoding.UTF8.GetBytes(values[k]));
        return this;
    }

    public IppRequest Int(byte tag, string name, int value)
    {
        Span<byte> v = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(v, value);
        return Raw(tag, name, v.ToArray());
    }

    public IppRequest Bool(string name, bool value) => Raw(Tag.Boolean, name, [(byte)(value ? 1 : 0)]);

    private IppRequest Raw(byte tag, string name, byte[] value)
    {
        _s.WriteByte(tag);
        var n = Encoding.ASCII.GetBytes(name);
        U16(n.Length); _s.Write(n);
        U16(value.Length); _s.Write(value);
        return this;
    }

    /// <summary>The finished request: the end tag, then the document data (if any).</summary>
    public byte[] Build(ReadOnlySpan<byte> document = default)
    {
        _s.WriteByte(Tag.End);
        _s.Write(document);
        return _s.ToArray();
    }

    private void U16(int v) { _s.WriteByte((byte)(v >> 8)); _s.WriteByte((byte)v); }
    private void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
}

sealed class IppException(string message) : Exception(message);
