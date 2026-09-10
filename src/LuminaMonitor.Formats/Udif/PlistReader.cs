using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace LuminaMonitor.Formats;

/// <summary>
/// Minimal reader for Apple XML property lists, just enough for the block
/// map a DMG embeds: dict, array, string, data, integer, real, true, false,
/// date.
/// </summary>
/// <remarks>
/// Values come back as plain CLR objects: <see cref="Dictionary{TKey,TValue}"/>
/// of string to object, <see cref="List{T}"/> of object, string, byte[],
/// long, double, bool, DateTime. Built on the BCL XmlReader with DTD
/// processing switched off, because every plist carries a DOCTYPE pointing at
/// apple.com and we must neither fetch it nor fail on it.
/// </remarks>
internal static class PlistReader
{
    public static object? Parse(byte[] xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
        };
        using var stream = new MemoryStream(xml, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new InvalidDataException("empty property list");
        var top = root.Name.LocalName == "plist" ? root.Elements().FirstOrDefault() : root;
        return top is null ? null : ReadValue(top);
    }

    private static object? ReadValue(XElement e) => e.Name.LocalName switch
    {
        "dict" => ReadDict(e),
        "array" => e.Elements().Select(ReadValue).ToList(),
        "string" => e.Value,
        "data" => DecodeBase64(e.Value),
        "integer" => long.Parse(e.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        "real" => double.Parse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "date" => DateTime.Parse(e.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        _ => throw new InvalidDataException($"unsupported plist element <{e.Name.LocalName}>"),
    };

    private static Dictionary<string, object?> ReadDict(XElement e)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? pendingKey = null;
        foreach (var child in e.Elements())
        {
            if (child.Name.LocalName == "key")
            {
                pendingKey = child.Value;
                continue;
            }
            if (pendingKey is null)
                throw new InvalidDataException("plist dict value without a preceding <key>");
            dict[pendingKey] = ReadValue(child);
            pendingKey = null;
        }
        return dict;
    }

    /// <summary>hdiutil wraps base64 with tabs and newlines; strip every control character first.</summary>
    private static byte[] DecodeBase64(string text)
    {
        var clean = new char[text.Length];
        int n = 0;
        foreach (char c in text)
            if (c > ' ')
                clean[n++] = c;
        return n == 0 ? [] : Convert.FromBase64CharArray(clean, 0, n);
    }

    /// <summary>Convenience for walking dictionaries without casts at every level.</summary>
    public static object? Get(object? node, string key)
        => node is Dictionary<string, object?> d && d.TryGetValue(key, out var v) ? v : null;
}
