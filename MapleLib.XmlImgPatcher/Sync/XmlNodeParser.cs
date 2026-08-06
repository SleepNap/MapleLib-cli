using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MapleLib.XmlImgPatcher.Parser;

namespace MapleLib.XmlImgPatcher.Sync
{
    /// <summary>
    /// Parses a server-side "thin" XML into a tree of <see cref="Node"/>. The root element
    /// is the file's own imgdir (e.g. &lt;imgdir name="Say.img"&gt;); its children are the
    /// full content of the file.
    ///
    /// Line-based parser reusing <see cref="XmlLineParser"/>. Deliberately does NOT use a
    /// full XML parser (XDocument / StAX): those normalise whitespace inside attribute values
    /// (TAB → space per the XML spec), which corrupts game text that legitimately carries
    /// trailing TABs. The line parser preserves them byte-for-byte, matching the Java side.
    ///
    /// Multiline values: a <string ... value="..."> whose value spans physical lines is
    /// accumulated until the closing quote (the parser joins the continuation lines). This
    /// covers the rare quest-dialogue strings that embed real newlines.
    ///
    /// Binary resource nodes (canvas/sound/uol/extended) are kept as leaf placeholders with a
    /// marker child so the merge layer can recognise and skip them.
    /// </summary>
    public static class XmlNodeParser
    {
        private static readonly HashSet<string> BinaryTags = new()
        { "canvas", "sound", "uol", "extended", "convex", "raw_data" };

        public static Node ParseFile(string path)
        {
            using var sr = new StreamReader(path);
            return Parse(sr.ReadToEnd());
        }

        public static Node Parse(string xmlText)
        {
            string[] lines = xmlText.Replace("\r\n", "\n").Split('\n');

            // Dummy root; the file's own root imgdir becomes its single child, which we hoist.
            var root = new Node("", NodeTag.ImgDir);
            var stack = new Stack<Node>();
            stack.Push(root);

            int i = 0;
            while (i < lines.Length)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("<?") || trimmed.StartsWith("<!--"))
                {
                    i++;
                    continue;
                }

                // Multiline value: a line that begins an attribute value but never closes the
                // quote/self-close. Accumulate continuation lines until a line ends with '>'.
                // A line already ending in '>' is complete — don't merge (every normal leaf
                // carries `value="..."` and ends with `/>`).
                string line = trimmed;
                if (!line.TrimEnd().EndsWith(">") && LooksLikeOpenValue(line))
                {
                    var sb = new StringBuilder(line);
                    int j = i + 1;
                    while (j < lines.Length && !LinesClosed(lines[j]))
                    {
                        sb.Append('\n').Append(lines[j]);
                        j++;
                    }
                    if (j < lines.Length)
                    {
                        sb.Append('\n').Append(lines[j]);
                        line = sb.ToString();
                        i = j;
                    }
                }

                var pl = XmlLineParser.TryParse(line);
                if (pl == null)
                {
                    i++;
                    continue;
                }

                switch (pl.Kind)
                {
                    case XmlLineParser.LineKind.ImgDirOpen:
                    {
                        var n = new Node(pl.Name, NodeTag.ImgDir);
                        stack.Peek().Children.Add(n);
                        stack.Push(n);
                        break;
                    }
                    case XmlLineParser.LineKind.ImgDirSelfClosing:
                        stack.Peek().Children.Add(new Node(pl.Name, NodeTag.ImgDir));
                        break;
                    case XmlLineParser.LineKind.ImgDirClose:
                        if (stack.Count > 1) stack.Pop();
                        break;
                    case XmlLineParser.LineKind.LeafSelfClosing:
                    {
                        if (BinaryTags.Contains(pl.Tag.ToLowerInvariant()))
                        {
                            // Binary placeholder: leaf with a marker child so merge can skip it.
                            var ph = new Node(pl.Name, NodeTag.ImgDir);
                            ph.Children.Add(new Node("", NodeTag.Null));
                            stack.Peek().Children.Add(ph);
                        }
                        else
                        {
                            var n = BuildLeaf(pl);
                            if (n != null) stack.Peek().Children.Add(n);
                        }
                        break;
                    }
                }
                i++;
            }

            return root.Children.Count == 1 && root.Children[0].Tag == NodeTag.ImgDir
                ? root.Children[0]
                : root;
        }

        // A line that starts an attribute value but may continue across lines: it has a
        // `value="` (or x=/y=) yet no closing `>` on this line.
        private static bool LooksLikeOpenValue(string line)
        {
            return line.Contains("value=\"") || line.Contains("value='");
        }

        private static bool LinesClosed(string line)
        {
            string t = line.Trim();
            return t.Length > 0 && t.EndsWith(">");
        }

        private static Node? BuildLeaf(XmlLineParser.ParsedLine pl)
        {
            string tag = pl.Tag.ToLowerInvariant();
            string name = pl.Name;
            string? value = pl.Attrs.TryGetValue("value", out string? v) ? v : null;

            switch (tag)
            {
                case "string": return new Node(name, NodeTag.String, value);
                case "int":    return new Node(name, NodeTag.Int,    value);
                case "short":  return new Node(name, NodeTag.Short,  value);
                case "long":   return new Node(name, NodeTag.Long,   value);
                case "float":  return new Node(name, NodeTag.Float,  value);
                case "double": return new Node(name, NodeTag.Double, value);
                case "vector":
                {
                    int x = XmlLineParser.ParseIntAttr(pl.Attrs.GetValueOrDefault("x"));
                    int y = XmlLineParser.ParseIntAttr(pl.Attrs.GetValueOrDefault("y"));
                    return new Node(name, NodeTag.Vector, x, y);
                }
                case "null": return new Node(name, NodeTag.Null);
                default: return null;
            }
        }
    }
}
