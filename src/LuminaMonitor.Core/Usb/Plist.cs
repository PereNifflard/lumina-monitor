using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace LuminaMonitor.Core.Usb;

/// <summary>
/// The subset of Apple's XML property-list format that usbmux and lockdown
/// speak: dict, array, string, integer, real, true/false, data, date.
/// </summary>
/// <remarks>
/// Written rather than imported on purpose. A plist is plain XML with six
/// value shapes, and every Apple service on the USB side of this project
/// exchanges nothing else — so this file is the whole "dependency", and it is
/// ours to read. Dictionaries come back as <see cref="Dictionary{TKey,TValue}"/>,
/// arrays as <see cref="List{T}"/>, integers as <see cref="long"/>, data as
/// <see cref="byte"/> arrays.
/// </remarks>
internal static class Plist
{
    private const string Doctype =
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">";

    public static byte[] Write(Dictionary<string, object> dict)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n").Append(Doctype).Append("\n<plist version=\"1.0\">\n");
        WriteValue(sb, dict);
        sb.Append("</plist>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void WriteValue(StringBuilder sb, object value)
    {
        switch (value)
        {
            case Dictionary<string, object> dict:
                sb.Append("<dict>");
                foreach (var (k, v) in dict)
                {
                    sb.Append("<key>").Append(Escape(k)).Append("</key>");
                    WriteValue(sb, v);
                }
                sb.Append("</dict>");
                break;
            case IEnumerable<object> list:
                sb.Append("<array>");
                foreach (var v in list) WriteValue(sb, v);
                sb.Append("</array>");
                break;
            case string s: sb.Append("<string>").Append(Escape(s)).Append("</string>"); break;
            case bool b: sb.Append(b ? "<true/>" : "<false/>"); break;
            case int or long: sb.Append("<integer>").Append(value).Append("</integer>"); break;
            case double d: sb.Append("<real>").Append(d.ToString(CultureInfo.InvariantCulture)).Append("</real>"); break;
            case byte[] bytes: sb.Append("<data>").Append(Convert.ToBase64String(bytes)).Append("</data>"); break;
            default: throw new ArgumentException($"Type non plist : {value.GetType().Name}");
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static object Read(byte[] xml)
    {
        var doc = XDocument.Parse(Encoding.UTF8.GetString(xml));
        var root = doc.Root ?? throw new FormatException("plist vide");
        var first = root.Elements().FirstOrDefault() ?? throw new FormatException("plist sans valeur");
        return ReadValue(first);
    }

    private static object ReadValue(XElement e) => e.Name.LocalName switch
    {
        "dict" => ReadDict(e),
        "array" => e.Elements().Select(ReadValue).ToList(),
        "string" => e.Value,
        "integer" => long.Parse(e.Value, CultureInfo.InvariantCulture),
        "real" => double.Parse(e.Value, CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "data" => Convert.FromBase64String(e.Value.Trim()),
        "date" => e.Value,
        _ => throw new FormatException($"Element plist inconnu : {e.Name.LocalName}"),
    };

    private static Dictionary<string, object> ReadDict(XElement e)
    {
        var dict = new Dictionary<string, object>();
        string? key = null;
        foreach (var child in e.Elements())
        {
            if (child.Name.LocalName == "key") { key = child.Value; continue; }
            if (key is null) throw new FormatException("valeur plist sans cle");
            dict[key] = ReadValue(child);
            key = null;
        }
        return dict;
    }

    /// <summary>Indented dump for the console, secrets shortened.</summary>
    public static string Dump(object value, int indent = 0)
    {
        var pad = new string(' ', indent * 2);
        switch (value)
        {
            case Dictionary<string, object> dict:
            {
                var sb = new StringBuilder();
                foreach (var (k, v) in dict)
                {
                    if (v is Dictionary<string, object> or List<object>)
                        sb.Append(pad).Append(k).Append(":\n").Append(Dump(v, indent + 1));
                    else
                        sb.Append(pad).Append(k).Append(" = ").Append(Dump(v, 0)).Append('\n');
                }
                return sb.ToString();
            }
            case List<object> list:
            {
                var sb = new StringBuilder();
                for (int i = 0; i < list.Count; i++)
                    sb.Append(pad).Append('[').Append(i).Append("]\n").Append(Dump(list[i], indent + 1));
                return sb.ToString();
            }
            case byte[] bytes:
                return $"<{bytes.Length} octets>";
            default:
                return value.ToString() ?? "";
        }
    }
}
