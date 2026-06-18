using System;
using System.Collections.Generic;
using System.Globalization;

namespace MapleLib.XmlImgPatcher.Parser
{
    /// <summary>
    /// Parses a single XML element line (server-style "thin" XML, self-closing leaves and
    /// open/close imgdir containers). Only the subset of tags we expect in diffs is handled.
    /// </summary>
    public static class XmlLineParser
    {
        public enum LineKind
        {
            Unknown,
            ImgDirOpen,    // <imgdir name="...">
            ImgDirSelfClosing, // <imgdir name="..."/>  (rare; treat as empty container)
            ImgDirClose,   // </imgdir>
            LeafSelfClosing, // <int|string|short|long|float|double|null|vector ... />
            XmlProlog,     // <?xml ...?>
            Comment,       // <!-- ... -->
        }

        public sealed class ParsedLine
        {
            public LineKind Kind { get; init; }
            public string Tag { get; init; } = "";
            public string Name { get; init; } = "";
            public Dictionary<string, string> Attrs { get; init; } = new();
        }

        /// <summary>Returns null if the trimmed line is not a recognized XML element.</summary>
        public static ParsedLine? TryParse(string trimmedLine)
        {
            string s = trimmedLine.Trim();
            if (s.Length == 0) return null;
            if (s.StartsWith("<?")) return new ParsedLine { Kind = LineKind.XmlProlog };
            if (s.StartsWith("<!--")) return new ParsedLine { Kind = LineKind.Comment };
            if (s.StartsWith("</"))
            {
                // Closing tag
                int gt = s.IndexOf('>');
                if (gt < 0) return null;
                string tag = s.Substring(2, gt - 2).Trim();
                if (tag.Equals("imgdir", StringComparison.OrdinalIgnoreCase))
                    return new ParsedLine { Kind = LineKind.ImgDirClose, Tag = tag };
                return null;
            }
            if (!s.StartsWith("<")) return null;

            // Find tag name
            int p = 1;
            while (p < s.Length && !char.IsWhiteSpace(s[p]) && s[p] != '>' && s[p] != '/')
                p++;
            string tagName = s.Substring(1, p - 1);
            // Parse attrs until '/>' or '>'
            var attrs = ParseAttributes(s, p, out bool selfClosing);

            attrs.TryGetValue("name", out string? name);
            name ??= "";

            if (tagName.Equals("imgdir", StringComparison.OrdinalIgnoreCase))
            {
                return new ParsedLine
                {
                    Kind = selfClosing ? LineKind.ImgDirSelfClosing : LineKind.ImgDirOpen,
                    Tag = tagName,
                    Name = name,
                    Attrs = attrs,
                };
            }
            if (selfClosing)
            {
                return new ParsedLine
                {
                    Kind = LineKind.LeafSelfClosing,
                    Tag = tagName,
                    Name = name,
                    Attrs = attrs,
                };
            }
            // open-tag for non-imgdir leaves: not supported (canvas etc. open tag)
            // Treat as opaque structural — return null so upstream skips it.
            return null;
        }

        private static Dictionary<string, string> ParseAttributes(string s, int start, out bool selfClosing)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            selfClosing = false;
            int i = start;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                if (s[i] == '/')
                {
                    selfClosing = true;
                    break;
                }
                if (s[i] == '>') break;

                int nameStart = i;
                while (i < s.Length && s[i] != '=' && !char.IsWhiteSpace(s[i]) && s[i] != '>')
                    i++;
                string attrName = s.Substring(nameStart, i - nameStart);

                // skip = and surrounding whitespace
                while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == '=')) i++;
                if (i >= s.Length || (s[i] != '"' && s[i] != '\''))
                {
                    // attribute without quoted value — bail
                    break;
                }
                char quote = s[i];
                i++;
                int valStart = i;
                while (i < s.Length && s[i] != quote) i++;
                string raw = i <= s.Length ? s.Substring(valStart, i - valStart) : "";
                if (i < s.Length) i++; // consume closing quote
                dict[attrName] = DecodeXmlEntities(raw);
            }
            return dict;
        }

        private static string DecodeXmlEntities(string s)
        {
            if (s.IndexOf('&') < 0) return s;
            return s
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&amp;", "&");
        }

        /// <summary>Parse the integer value attribute, falling back to 0 if missing.</summary>
        public static int ParseIntAttr(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        }
    }
}
