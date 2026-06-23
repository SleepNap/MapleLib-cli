using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MapleLib.WzLib;
using MapleLib.XmlImgPatcher.Model;
using ValueType = MapleLib.XmlImgPatcher.Model.ValueType;

namespace MapleLib.XmlImgPatcher.Patcher
{
    /// <summary>Outcome of a patch run, used for reporting and exit-code selection.</summary>
    public sealed class PatchResult
    {
        public int Applied { get; set; }
        public int Failed { get; set; }
    }

    /// <summary>
    /// Orchestrates load → apply each <see cref="Change"/> → save. Logs each change with
    /// a stable, scriptable prefix ([ok]/[err]).
    /// </summary>
    public sealed class ImgPatcher
    {
        private readonly MapleLibAdapter _adapter;
        private readonly bool _verbose;
        private readonly bool _strict;
        private readonly bool _dryRun;
        private readonly TextWriter _out;
        private readonly TextWriter _err;

        public ImgPatcher(MapleLibAdapter adapter, bool verbose, bool strict, bool dryRun, TextWriter @out, TextWriter err)
        {
            _adapter = adapter;
            _verbose = verbose;
            _strict = strict;
            _dryRun = dryRun;
            _out = @out;
            _err = err;
        }

        public PatchResult Patch(string inputImg, IReadOnlyList<Change> changes, string outputImg)
        {
            _out.WriteLine($"[parse] {changes.Count} changes from diff");

            WzImage img = _adapter.LoadImg(inputImg);
            var result = new PatchResult();

            // Apply in a fixed phase order — Delete, then Modify, then Add — rather than the
            // diff's textual order. git unified diffs interleave - and + runs at the sibling
            // level, so an "ADD <imgdir X>" can textually precede "DELETE <imgdir X>" even
            // though the DELETE must win (it speaks to the old tree, the ADD to the new one).
            // Running deletes first makes the upsert-on-ADD path see a clean slot and avoids
            // the "ADD then DELETE same node" race that wipes freshly-added subtrees.

            // Before phasing, cancel out "false rename" pairs: when git fails to detect a
            // rename and emits `- <imgdir X>` (DELETE) + `+ <imgdir X>` with NO plus-body
            // children (the real children are the following context lines, which belong to X
            // in both old and new trees), the DELETE + empty ADD pair is a no-op — X exists in
            // both trees with identical content, only its sibling ordering changed. Applying
            // the DELETE would drop X's children, then the empty ADD would re-add X as an empty
            // container, losing data. Cancel both so the original subtree is preserved.
            changes = CancelFalseRenames(changes);

            var ordered = new List<Change>(changes.Count);
            ordered.AddRange(changes.Where(c => c.Op == ChangeOp.Delete));
            ordered.AddRange(changes.Where(c => c.Op == ChangeOp.Modify));
            ordered.AddRange(changes.Where(c => c.Op == ChangeOp.Add));

            foreach (var c in ordered)
            {
                try
                {
                    switch (c.Op)
                    {
                        case ChangeOp.Modify:
                            _adapter.ApplyModify(img, c);
                            break;
                        case ChangeOp.Add:
                            _adapter.ApplyAdd(img, c);
                            break;
                        case ChangeOp.Delete:
                            _adapter.ApplyDelete(img, c);
                            break;
                    }
                    result.Applied++;
                    LogOk(c);
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    LogErr(c, ex);
                    if (_strict) throw;
                }
            }

            if (!_dryRun)
            {
                img.Changed = true;
                _adapter.SaveImg(img, outputImg);
            }

            long size = -1;
            try { size = new FileInfo(outputImg).Length; } catch { /* ignore */ }
            string suffix = _dryRun ? " (dry-run, not written)" : $" Output: {outputImg} ({size:N0} bytes)";
            _out.WriteLine($"{result.Applied} applied, {result.Failed} failed.{suffix}");

            return result;
        }

        private void LogOk(Change c)
        {
            string detail = c.Op switch
            {
                ChangeOp.Modify => $"{c.PathString} = {FormatValue(c)}",
                ChangeOp.Add when c.ValueType == ValueType.Sub && c.SubTree != null
                    => $"{c.PathString} (subtree, {CountNodes(c.SubTree)} nodes)",
                ChangeOp.Add => $"{c.PathString} = {FormatValue(c)}",
                ChangeOp.Delete => c.PathString,
                _ => c.PathString,
            };
            _out.WriteLine($"[ok]  {c.Op.ToString().ToUpperInvariant(),-6}  {detail}");
        }

        private void LogErr(Change c, Exception ex)
        {
            _err.WriteLine($"[err] {c.Op.ToString().ToUpperInvariant(),-6}  {c.PathString} — {ex.Message}");
            if (_verbose)
            {
                _err.WriteLine(ex);
            }
        }

        private static string FormatValue(Change c)
        {
            if (c.ValueType == ValueType.Vector) return $"({c.VectorX},{c.VectorY})";
            return c.Value == null ? "<null>" : $"\"{c.Value}\"";
        }

        private static int CountNodes(SubTree t)
        {
            int n = 1;
            foreach (var ch in t.Children) n += CountNodes(ch);
            return n;
        }

        // Cancel "false rename" pairs produced when git doesn't recognize a rename and emits
        // `- <imgdir X>` + `+ <imgdir X>` (empty body) with the real children as context lines.
        // Such a DELETE + empty-ADD pair is a no-op: X exists in both trees with the same
        // content. Remove both so the patcher leaves the original subtree untouched.
        // An ADD is "empty" only when it's a Sub container with zero children — a genuinely
        // new empty container (e.g. `<imgdir name="0"/>` self-closing, or an imgdir the diff
        // truly empties) is NOT cancelled, because there's no matching DELETE-with-same-path
        // for a truly new node and the pair check below guards real empties too.
        private static IReadOnlyList<Change> CancelFalseRenames(IReadOnlyList<Change> changes)
        {
            var deletePaths = new HashSet<string>(
                changes.Where(c => c.Op == ChangeOp.Delete).Select(c => c.PathString));

            if (deletePaths.Count == 0) return changes;

            bool changed = false;
            var result = new List<Change>(changes.Count);
            foreach (var c in changes)
            {
                if (c.Op == ChangeOp.Add
                    && c.ValueType == ValueType.Sub
                    && c.SubTree != null
                    && c.SubTree.Children.Count == 0
                    && deletePaths.Contains(c.PathString))
                {
                    // This empty ADD matches a DELETE at the same path → false rename.
                    changed = true;
                    continue;
                }
                result.Add(c);
            }

            if (!changed) return changes;

            // Also drop the DELETEs whose path matched a cancelled empty ADD.
            var cancelledAddPaths = new HashSet<string>(
                changes.Where(c => c.Op == ChangeOp.Add
                    && c.ValueType == ValueType.Sub
                    && c.SubTree != null
                    && c.SubTree.Children.Count == 0
                    && deletePaths.Contains(c.PathString))
                    .Select(c => c.PathString));

            var final = new List<Change>(result.Count);
            foreach (var c in result)
            {
                if (c.Op == ChangeOp.Delete && cancelledAddPaths.Contains(c.PathString))
                    continue;
                final.Add(c);
            }
            return final;
        }
    }
}
