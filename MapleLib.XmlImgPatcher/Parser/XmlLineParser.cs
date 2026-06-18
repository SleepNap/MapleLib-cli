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

            // Tolerate malformed diff lines that lost their leading '<' (observed in real server
            // diffs, e.g. "-        string name=\"h1\" ..."). If the line starts with a known tag
            // name followed by whitespace, prepend '<' so it parses. Only do this for lines that
            // also contain name="..." to avoid false positives on prose.
            if (!s.StartsWith("<") && !s.StartsWith("</"))
            {
                int sp = s.IndexOf(' ');
                if (sp > 0)
                {
                    string maybeTag = s.Substring(0, sp);
                    if (IsKnownTag(maybeTag) && s.Contains("name=\""))
                        s = "<" + s;
                }
            }

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

            // Handle numeric character references (&#xA; / &#10;) first, since the named-entity
            // pass below would otherwise turn their leading '&' into '&amp;'. This matters for
            // game text: servers store real newlines and serialise them as &#xA; in XML, so the
            // diff's value attribute carries &#xA; which must decode back to '\n' before writing
            // into the img — otherwise the img ends up holding the literal 5-char "&#xA;" string.
            var sb = new System.Text.StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '&')
                {
                    int semi = s.IndexOf(';', i + 1);
                    if (semi > i && semi - i <= 8 && s[i + 1] == '#')
                    {
                        string body = s.Substring(i + 2, semi - i - 2);
                        int code = -1;
                        if (body.Length > 0 && (body[0] == 'x' || body[0] == 'X'))
                            int.TryParse(body.Substring(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code);
                        else
                            int.TryParse(body, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out code);
                        if (code >= 0 && code <= 0x10FFFF)
                        {
                            sb.Append((char)code); // BMP only; sufficient for \n \r \t etc.
                            i = semi + 1;
                            continue;
                        }
                    }
                }
                sb.Append(s[i]);
                i++;
            }
            string decoded = sb.ToString();

            return decoded
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

        private static bool IsKnownTag(string tag)
        {
            switch (tag.ToLowerInvariant())
            {
                case "imgdir":
                case "string":
                case "int":
                case "short":
                case "long":
                case "float":
                case "double":
                case "vector":
                case "null":
                case "canvas":
                case "uol":
                case "sound":
                case "extended":
                    return true;
                default:
                    return false;
            }
        }
    }
}
