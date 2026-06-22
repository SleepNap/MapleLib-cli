using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MapleLib.XmlImgPatcher.Model;
using ValueType = MapleLib.XmlImgPatcher.Model.ValueType;

namespace MapleLib.XmlImgPatcher.Parser
{
    /// <summary>
    /// Parses a git unified diff of a server-side .img.xml into a flat list of <see cref="Change"/>.
    /// Strategy: for each hunk, walk lines top-to-bottom maintaining an imgdir-name stack that is
    /// driven by context lines + by '-' lines (since "removed" content was structurally present
    /// before the change). '+' lines drive the rebuild of new structure inside the hunk.
    ///
    /// When a "full-XML" companion file is supplied (the server's post-diff XML), the parser uses
    /// the hunk header's "+" line number to seed the stack from the surrounding nesting in that
    /// file — recovering paths even when the hunk's own context does not include the enclosing
    /// imgdir-open lines. This is the common case for short hunks deep inside a long file.
    /// </summary>
    public sealed class DiffParser
    {
        private readonly string? _fullXmlPath;
        private string[]? _fullXmlLines;

        public DiffParser(string? fullXmlPath = null)
        {
            _fullXmlPath = fullXmlPath;
        }
        public List<Change> ParseFile(string diffPath)
        {
            using var sr = new StreamReader(diffPath);
            return Parse(sr.ReadToEnd());
        }

        public List<Change> Parse(string diffContent)
        {
            var result = new List<Change>();
            string[] lines = diffContent.Replace("\r\n", "\n").Split('\n');

            int i = 0;
            while (i < lines.Length)
            {
                string raw = lines[i];
                if (raw.StartsWith("@@"))
                {
                    int next = ParseHunk(lines, i, result);
                    i = next;
                    continue;
                }
                i++;
            }
            return result;
        }

        // Returns index of next line after hunk consumed.
        private int ParseHunk(string[] lines, int startIdx, List<Change> output)
        {
            // Stacks for context (the "old" tree) and for "+" (the "new" tree). They start equal,
            // diverge while inside +/- runs, and re-sync at the next ' ' context line.
            var oldStack = new Stack<string>();
            var newStack = new Stack<string>();

            // Seed from the full-XML companion if available, using the new-file line number from
            // the hunk header. This recovers the enclosing imgdir nesting that the hunk itself
            // does not carry as context.
            int newLineNumber = ParseHunkHeaderNewStart(lines[startIdx]);
            if (newLineNumber > 0)
            {
                var seeded = SeedStackFromFullXml(newLineNumber);
                foreach (var name in seeded)
                {
                    oldStack.Push(name);
                    newStack.Push(name);
                }
            }

            int i = startIdx + 1;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (line.StartsWith("@@") || line.StartsWith("diff ") || line.StartsWith("--- ") || line.StartsWith("+++ ") || line.StartsWith("index "))
                    return i;
                if (line.Length == 0)
                {
                    i++;
                    continue;
                }
                char prefix = line[0];
                string body = line.Length > 1 ? line.Substring(1) : "";

                switch (prefix)
                {
                    case ' ':
                        ApplyStructural(body, oldStack);
                        ApplyStructural(body, newStack);
                        i++;
                        break;
                    case '-':
                        i = HandleMinusBlock(lines, i, oldStack, newStack, output);
                        break;
                    case '+':
                        i = HandlePlusBlock(lines, i, newStack, output, modifyTarget: null);
                        break;
                    case '\\':
                        // "\ No newline at end of file" — ignore
                        i++;
                        break;
                    default:
                        // Unknown — terminate hunk.
                        return i;
                }
            }
            return i;
        }

        // Process a contiguous block of '-' lines, then check for an immediately-following '+' block.
        // If the - block contains a single leaf and the + block contains a single leaf with the same
        // node name and structural position, they merge into a MODIFY. Otherwise they become
        // independent DELETE / ADD records.
        private int HandleMinusBlock(string[] lines, int startIdx, Stack<string> oldStack, Stack<string> newStack, List<Change> output)
        {
            // Snapshot state at start so we can independently advance both stacks.
            int i = startIdx;
            var minusEntries = new List<(string body, int sourceLine, Stack<string> stackBefore)>();
            while (i < lines.Length && lines[i].Length > 0 && lines[i][0] == '-')
            {
                string body = lines[i].Substring(1);
                // Capture the oldStack as it was BEFORE this line was seen (for path of leaves).
                var snapshot = new Stack<string>(new Stack<string>(oldStack)); // clone-clone to preserve order
                minusEntries.Add((body, i + 1, snapshot));
                ApplyStructural(body, oldStack);
                i++;
            }

            // Look ahead for a contiguous '+' block.
            int afterMinus = i;
            var plusEntries = new List<(string body, int sourceLine)>();
            while (i < lines.Length && lines[i].Length > 0 && lines[i][0] == '+')
            {
                plusEntries.Add((lines[i].Substring(1), i + 1));
                i++;
            }

            // Try simple MODIFY merge: single self-closing leaf removed and single self-closing
            // leaf added with same name at same depth.
            if (minusEntries.Count == 1 && plusEntries.Count == 1)
            {
                var minus = minusEntries[0];
                var plus = plusEntries[0];
                var mLine = XmlLineParser.TryParse(minus.body);
                var pLine = XmlLineParser.TryParse(plus.body);
                if (mLine is { Kind: XmlLineParser.LineKind.LeafSelfClosing }
                    && pLine is { Kind: XmlLineParser.LineKind.LeafSelfClosing }
                    && mLine.Name == pLine.Name
                    && string.Equals(mLine.Tag, pLine.Tag, StringComparison.OrdinalIgnoreCase))
                {
                    // MODIFY at path = newStack (which still equals oldStack here, since context
                    // lines drive both equally and we haven't applied + yet).
                    var path = StackToPath(newStack);
                    path.Add(pLine.Name);
                    var (vt, val, vx, vy) = ExtractLeafValue(pLine);
                    output.Add(new Change(path, ChangeOp.Modify, vt, val, plus.sourceLine, null, vx, vy));
                    // Apply structural to + side (no-op for leaves)
                    ApplyStructural(plus.body, newStack);
                    return i;
                }
            }

            // Otherwise: emit DELETEs for the - lines, then process + as ADDs.
            foreach (var minus in minusEntries)
            {
                var ml = XmlLineParser.TryParse(minus.body);
                if (ml == null) continue;
                if (ml.Kind == XmlLineParser.LineKind.ImgDirClose) continue;
                if (ml.Kind == XmlLineParser.LineKind.XmlProlog || ml.Kind == XmlLineParser.LineKind.Comment) continue;
                if (string.IsNullOrEmpty(ml.Name)) continue;

                // Path = stackBefore + name
                var path = StackToPath(minus.stackBefore);
                path.Add(ml.Name);
                output.Add(new Change(path, ChangeOp.Delete, MapTagToValueType(ml.Tag), null, minus.sourceLine));
                // Note: structural already applied above on oldStack
            }

            // Re-process plus block as a structured ADD walk.
            if (plusEntries.Count > 0)
            {
                int plusIdx = 0;
                int dummy = HandlePlusEntries(plusEntries, ref plusIdx, newStack, output);
                _ = dummy;
            }

            return i;
        }

        // Handle a contiguous block of '+' lines beginning at startIdx.
        // 'modifyTarget' is unused here — kept for symmetry; ADD walks recursively.
        private int HandlePlusBlock(string[] lines, int startIdx, Stack<string> newStack, List<Change> output, object? modifyTarget)
        {
            var entries = new List<(string body, int sourceLine)>();
            int i = startIdx;
            while (i < lines.Length && lines[i].Length > 0 && lines[i][0] == '+')
            {
                entries.Add((lines[i].Substring(1), i + 1));
                i++;
            }
            int idx = 0;
            HandlePlusEntries(entries, ref idx, newStack, output);
            return i;
        }

        // Walk a list of '+' lines starting at idx. For top-level adds, emit Change(ADD).
        // Returns the (consumed) idx.
        private int HandlePlusEntries(List<(string body, int sourceLine)> entries, ref int idx, Stack<string> outerStack, List<Change> output)
        {
            // Clone the outer (context) stack. Track plus-block nesting with the clone.
            // After the plus block, any containers that were pushed but not popped by the
            // plus block's own entries are pushed back onto the outer stack — they will be
            // closed by subsequent context lines.
            var stack = new Stack<string>(new Stack<string>(outerStack));
            int initialDepth = stack.Count;

            while (idx < entries.Count)
            {
                var entry = entries[idx];
                var pl = XmlLineParser.TryParse(entry.body);
                if (pl == null || pl.Kind == XmlLineParser.LineKind.XmlProlog || pl.Kind == XmlLineParser.LineKind.Comment)
                {
                    idx++;
                    continue;
                }

                if (pl.Kind == XmlLineParser.LineKind.ImgDirClose)
                {
                    if (stack.Count > 1) stack.Pop();
                    idx++;
                    return idx;
                }

                if (pl.Kind == XmlLineParser.LineKind.LeafSelfClosing
                    || pl.Kind == XmlLineParser.LineKind.ImgDirSelfClosing)
                {
                    var path = StackToPath(stack);
                    path.Add(pl.Name);
                    var subTree = BuildLeafSubTree(pl);
                    output.Add(new Change(path, ChangeOp.Add, subTree.Type, subTree.Value, entry.sourceLine, subTree, subTree.VectorX, subTree.VectorY));
                    idx++;
                    continue;
                }

                if (pl.Kind == XmlLineParser.LineKind.ImgDirOpen)
                {
                    stack.Push(pl.Name);
                    var path = StackToPath(stack);
                    var sub = new SubTree(pl.Name, ValueType.Sub, null);
                    int childIdx = idx + 1;
                    BuildSubTreeFrom(entries, ref childIdx, sub, stack);
                    output.Add(new Change(path, ChangeOp.Add, ValueType.Sub, null, entry.sourceLine, sub));
                    idx = childIdx;
                    continue;
                }

                idx++;
            }

            // If the plus block pushed containers that were not closed (because the
            // closing </imgdir> is on a context line), preserve them on the outer stack
            // so subsequent context lines can pop them correctly.
            if (stack.Count > initialDepth)
            {
                // Transfer the extra depth to the outer stack.
                var extras = new List<string>();
                while (stack.Count > initialDepth)
                    extras.Add(stack.Pop());
                extras.Reverse();
                foreach (var name in extras)
                    outerStack.Push(name);
            }

            return idx;
        }

        // After encountering an ImgDirOpen, recursively build the SubTree by consuming entries
        // until the matching </imgdir>. Mutates idx.
        private void BuildSubTreeFrom(List<(string body, int sourceLine)> entries, ref int idx, SubTree parent, Stack<string> newStack)
        {
            while (idx < entries.Count)
            {
                var entry = entries[idx];
                var pl = XmlLineParser.TryParse(entry.body);
                if (pl == null || pl.Kind == XmlLineParser.LineKind.XmlProlog || pl.Kind == XmlLineParser.LineKind.Comment)
                {
                    idx++;
                    continue;
                }
                if (pl.Kind == XmlLineParser.LineKind.ImgDirClose)
                {
                    // Pop the container name that was pushed by the matching ImgDirOpen.
                    if (newStack.Count > 1) newStack.Pop();
                    idx++;
                    return;
                }
                if (pl.Kind == XmlLineParser.LineKind.LeafSelfClosing
                    || pl.Kind == XmlLineParser.LineKind.ImgDirSelfClosing)
                {
                    parent.Children.Add(BuildLeafSubTree(pl));
                    idx++;
                    continue;
                }
                if (pl.Kind == XmlLineParser.LineKind.ImgDirOpen)
                {
                    newStack.Push(pl.Name);
                    var sub = new SubTree(pl.Name, ValueType.Sub, null);
                    idx++;
                    BuildSubTreeFrom(entries, ref idx, sub, newStack);
                    parent.Children.Add(sub);
                    continue;
                }
                idx++;
            }
        }

        private static SubTree BuildLeafSubTree(XmlLineParser.ParsedLine pl)
        {
            if (pl.Kind == XmlLineParser.LineKind.ImgDirSelfClosing)
            {
                return new SubTree(pl.Name, ValueType.Sub, null);
            }
            string tag = pl.Tag.ToLowerInvariant();
            if (tag == "vector")
            {
                int x = XmlLineParser.ParseIntAttr(pl.Attrs.GetValueOrDefault("x"));
                int y = XmlLineParser.ParseIntAttr(pl.Attrs.GetValueOrDefault("y"));
                return new SubTree(pl.Name, x, y);
            }
            string? value = pl.Attrs.GetValueOrDefault("value");
            return new SubTree(pl.Name, MapTagToValueType(pl.Tag), value);
        }

        private static (ValueType type, string? value, int x, int y) ExtractLeafValue(XmlLineParser.ParsedLine pl)
        {
            string tag = pl.Tag.ToLowerInvariant();
            if (tag == "vector")
            {
                int x = XmlLineParser.ParseIntAttr(pl.Attrs.GetValueOrDefault("x"));
                int y = XmlLineParser.ParseIntAttr(pl.Attrs.GetValueOrDefault("y"));
                return (ValueType.Vector, null, x, y);
            }
            string? v = pl.Attrs.GetValueOrDefault("value");
            return (MapTagToValueType(pl.Tag), v, 0, 0);
        }

        private static ValueType MapTagToValueType(string tag) => tag.ToLowerInvariant() switch
        {
            "imgdir" => ValueType.Sub,
            "string" => ValueType.String,
            "int" => ValueType.Int,
            "short" => ValueType.Short,
            "long" => ValueType.Long,
            "float" => ValueType.Float,
            "double" => ValueType.Double,
            "vector" => ValueType.Vector,
            "null" => ValueType.Null,
            _ => ValueType.String, // safest default for unknown leaf-ish tags
        };

        // Drive the imgdir stack from a single XML line (called for context lines and after
        // applying +/- entries to their respective stacks).
        private static void ApplyStructural(string body, Stack<string> stack)
        {
            var pl = XmlLineParser.TryParse(body);
            if (pl == null) return;
            if (pl.Kind == XmlLineParser.LineKind.ImgDirOpen)
            {
                stack.Push(pl.Name);
            }
            else if (pl.Kind == XmlLineParser.LineKind.ImgDirClose)
            {
                // Never pop the root imgdir name (e.g. "Say.img"). In long hunks
                // the context lines may close everything down to the image root,
                // but Add/Modify/Delete paths must always include the root name
                // for consistency with other hunks and with the seed stack.
                if (stack.Count > 1) stack.Pop();
            }
        }

        // Stack preserves push-order top->bottom; flatten to a path array root-first.
        // We do NOT drop the bottom element: hunk context never includes the outer file-root
        // imgdir (e.g. "0403.img"), so every entry in the stack is a real path component.
        private static List<string> StackToPath(Stack<string> stack)
        {
            var arr = stack.ToArray(); // top-first
            var list = new List<string>(arr.Length);
            for (int i = arr.Length - 1; i >= 0; i--)
            {
                list.Add(arr[i]);
            }
            return list;
        }

        // Parse "+C,D" component of `@@ -A,B +C,D @@`. Returns C (1-based) or 0 if unparseable.
        private static int ParseHunkHeaderNewStart(string headerLine)
        {
            int plus = headerLine.IndexOf('+');
            if (plus < 0) return 0;
            int comma = headerLine.IndexOf(',', plus);
            int space = headerLine.IndexOf(' ', plus);
            int end = (comma >= 0 && (space < 0 || comma < space)) ? comma : space;
            if (end < 0) return 0;
            string num = headerLine.Substring(plus + 1, end - plus - 1);
            return int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        // Walk the full XML from line 1 up to (but not including) newLineNumber and build the
        // imgdir-open stack. Returns the seeded stack root-first so callers can push in order.
        private List<string> SeedStackFromFullXml(int newLineNumber)
        {
            var empty = new List<string>();
            if (string.IsNullOrEmpty(_fullXmlPath)) return empty;
            if (_fullXmlLines == null)
            {
                try
                {
                    if (!File.Exists(_fullXmlPath)) return empty;
                    string content = File.ReadAllText(_fullXmlPath);
                    _fullXmlLines = content.Replace("\r\n", "\n").Split('\n');
                }
                catch
                {
                    _fullXmlLines = Array.Empty<string>();
                    return empty;
                }
            }
            int upTo = Math.Min(newLineNumber - 1, _fullXmlLines.Length);
            var stack = new List<string>();
            for (int i = 0; i < upTo; i++)
            {
                var pl = XmlLineParser.TryParse(_fullXmlLines[i]);
                if (pl == null) continue;
                if (pl.Kind == XmlLineParser.LineKind.ImgDirOpen) stack.Add(pl.Name);
                else if (pl.Kind == XmlLineParser.LineKind.ImgDirClose && stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
            }
            return stack;
        }
    }
}
