using System;
using System.Collections.Generic;
using System.Globalization;
using MapleLib.XmlImgPatcher.Model;
using ValueType = MapleLib.XmlImgPatcher.Model.ValueType;

namespace MapleLib.XmlImgPatcher.Sync
{
    /// <summary>
    /// Reason a Change was emitted. Mirrors the Part 1.4 action set, with `Auto` and
    /// `ThirdDefault` distinguishing the two MODIFY motivations, and `TypeConflict`
    /// signalling a tag mismatch that we kept the client node for. Both sync
    /// implementations (C# / Java) must emit this same set with the same spelling so
    /// reports and CSV/JSON consumers stay portable.
    /// </summary>
    public enum ChangeAction
    {
        /// <summary>client value matches old → safe to overwrite with new (auto).</summary>
        ModifyAuto,
        /// <summary>client value matches neither old nor new → flagged for human review.</summary>
        ModifyThirdDefault,
        /// <summary>intent was ADD but client has a different value → overwrite with new (no flag).</summary>
        ModifyDefault,
        /// <summary>client missing → append from new (no flag).</summary>
        AddDefault,
        /// <summary>client has it, server removed it, old had it → safe to delete.</summary>
        Delete,
        /// <summary>server and client disagree on tag at this path → kept client, flagged.</summary>
        TypeConflict,
    }

    public sealed class MergeResult
    {
        public List<Change> Changes { get; } = new();
        public List<ChangeAction> Actions { get; } = new();
        public int ReviewCount { get; set; }

        /// <summary>Human-readable review entries (missing-unmodified etc.) that don't map to a Change.</summary>
        public List<string> ReviewItems { get; } = new();
    }

    /// <summary>
    /// Three-way merge engine: takes (old, new, client) Node trees and produces a flat
    /// list of Change records ready to feed into the existing ImgPatcher. Path semantics
    /// follow Part 1.3 of the plan:
    ///   - For MODIFY / DELETE: path is built from the client tree (the node that exists).
    ///   - For ADD: path is built from the new (server) tree (the node that doesn't exist yet).
    /// ADD's path is checked against the existing img by ApplyAdd's EnsureParentChain — that
    /// helper creates any missing intermediate containers.
    ///
    /// Mirrors wz-sync.py's build_target / merge_children. Direct port; no text-diff
    /// intermediate. The python script's "third value" typo (wz-sync.py:381) is fixed
    /// here by using the proper ModifyThirdDefault literal.
    /// </summary>
    public static class ThreeWayMerge
    {
        /// <summary>
        /// Run the merge. <paramref name="oldRoot"/> may be null — in that case the merge
        /// degenerates into a two-way (new vs client) full match: change detection still
        /// works, but DELETE intent is unreachable (no old to disambiguate "removed" vs
        /// "server never managed"). <paramref name="trustMode"/> controls how two-way value
        /// differences are labelled: trust=true → ModifyDefault (silent), trust=false →
        /// ModifyThirdDefault (flagged for review).
        /// </summary>
        public static MergeResult Merge(Node? oldRoot, Node newRoot, Node? clientRoot, bool trustMode, bool strict)
        {
            var result = new MergeResult();
            // Path excludes the root imgdir name (e.g. "Say.img") — segments start at the
            // root's children. SiblingIndices is parallel to path: indices[i] is the ordinal of
            // path[i] among same-named siblings. This matches the Java implementation.
            BuildTarget(
                oldRoot,
                newRoot,
                clientRoot,
                new List<string>(),
                new List<int>(),
                oldRoot == null,
                trustMode,
                strict,
                result);
            return result;
        }

        // ------------- core comparator -------------

        private static bool SameValue(Node? a, Node? b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Tag != b.Tag) return false;
            string? va = a.Value;
            string? vb = b.Value;
            if (va == vb) return true;
            if (va == null || vb == null) return false;
            // Numeric tolerance (wz-sync.py:98-108): if both parse as double, compare
            // numerically. Otherwise strict string equality.
            if (double.TryParse(va, NumberStyles.Float, CultureInfo.InvariantCulture, out var da)
                && double.TryParse(vb, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            {
                return da == db;
            }
            return false;
        }

        private static bool SubtreeEqual(Node? a, Node? b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Tag != b.Tag) return false;
            if (!a.IsContainer) return SameValue(a, b);
            // Container: pair children by name, preserving order; multiple children with the
            // same name are matched in the order they appear (wz-sync.py:124-136).
            var bb = b.Children;
            var used = new HashSet<int>();
            foreach (var ca in a.Children)
            {
                bool matched = false;
                for (int i = 0; i < bb.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    if (bb[i].Name == ca.Name)
                    {
                        if (!SubtreeEqual(ca, bb[i])) return false;
                        used.Add(i);
                        matched = true;
                        break;
                    }
                }
                if (!matched) return false;
            }
            return used.Count == bb.Count;
        }

        // ------------- build_target (recursive) -------------

        private static void BuildTarget(
            Node? oldNode,
            Node? newNode,
            Node? clientNode,
            List<string> path,
            List<int> indices,
            bool twoWay,
            bool trustMode,
            bool strict,
            MergeResult result)
        {
            // Server has nothing at this path.
            if (newNode == null)
            {
                if (oldNode != null && clientNode != null)
                {
                    // Old + client, but server removed → DELETE (three-way mode).
                    EmitDelete(clientNode, path, indices, result);
                }
                else if (twoWay && strict && clientNode != null)
                {
                    // Two-way + strict: client has a business node the server doesn't → DELETE.
                    // Binary resources were never materialised into the Node tree (ImgNodeReader
                    // skips canvas/sound/uol), so this only touches real business nodes.
                    EmitDelete(clientNode, path, indices, result);
                }
                // else: client had it but server never managed it → keep client (no Change).
                return;
            }

            // Server has something. Determine server's intent for this path.
            string intent;
            if (oldNode == null) intent = "ADD";
            else if (SubtreeEqual(oldNode, newNode)) intent = "UNCHANGED";
            else intent = "MODIFY";

            if (clientNode == null)
            {
                // Client missing → ADD from new (matches wz-sync.py: only ADD/MODIFY add).
                if (intent == "ADD" || intent == "MODIFY")
                {
                    EmitAddFromSubtree(newNode, path, indices, result);
                }
                else if (intent == "UNCHANGED" && oldNode != null)
                {
                    // Incremental mode + old==new (server didn't change) + client missing →
                    // uncertain: the server has this node but never changed it, and the client
                    // lacks it entirely. Flag for review, don't auto-add (user decision).
                    result.ReviewCount++;
                    result.ReviewItems.Add(
                        $"missing-unmodified {string.Join("/", path)} (client 缺失、服务端未改动，不自动补)");
                }
                return;
            }

            // Client has something. Tag conflict check first.
            if (clientNode.Tag != newNode.Tag)
            {
                // type-conflict: keep client, flag for review (no Change emitted).
                result.Actions.Add(ChangeAction.TypeConflict);
                result.ReviewCount++;
                return;
            }

            // Leaf case. Java parity: we do NOT short-circuit on intent == UNCHANGED. When the
            // server didn't change between old and new (old == new) but the client diverged from
            // that value, the client holds stale/wrong data — align it to new and flag it for
            // review, exactly like the old != new + client-diverged case. Short-circuiting would
            // leave client bugs (e.g. a "#pHill_ID#" placeholder, an outdated translation) in
            // place forever.
            if (!newNode.IsContainer)
            {
                if (SameValue(clientNode, newNode))
                {
                    // Already at the new value — no change.
                    return;
                }

                if (intent == "MODIFY" || intent == "UNCHANGED")
                {
                    // oldNode is non-null here (intent==MODIFY/UNCHANGED implies old exists).
                    if (SameValue(clientNode, oldNode))
                    {
                        // Client == old → auto (server changed old→new).
                        EmitModify(newNode, path, indices, result, ChangeAction.ModifyAuto);
                    }
                    else
                    {
                        // Client ⊄ {old, new} → third-default (flagged). Covers both the true
                        // three-way conflict (old != new) and the historical-divergence case
                        // (old == new, client drifted from it).
                        EmitModify(newNode, path, indices, result, ChangeAction.ModifyThirdDefault);
                        result.ReviewCount++;
                    }
                }
                else // intent == ADD
                {
                    // Client has a different value at a path server intends to add.
                    // Two-way (old==null): every value diff is "server has it, client diverged" —
                    // flag unless trustMode. Three-way ADD-with-client-value → default (no flag).
                    if (twoWay && !trustMode)
                    {
                        EmitModify(newNode, path, indices, result, ChangeAction.ModifyThirdDefault);
                        result.ReviewCount++;
                    }
                    else
                    {
                        EmitModify(newNode, path, indices, result, ChangeAction.ModifyDefault);
                    }
                }
                return;
            }

            // Container: recurse with merge_children.
            MergeChildren(oldNode, newNode, clientNode, path, indices, twoWay, trustMode, strict, result);
        }

        // ------------- merge_children -------------

        private static void MergeChildren(
            Node? oldNode,
            Node newNode,
            Node clientNode,
            List<string> path,
            List<int> indices,
            bool twoWay,
            bool trustMode,
            bool strict,
            MergeResult result)
        {
            if (!newNode.IsContainer || !clientNode.IsContainer)
            {
                // Defensive: shouldn't happen given the buildTarget dispatch above.
                return;
            }

            var newKids = newNode.Children;
            var clientKids = clientNode.Children;
            var oldKids = oldNode?.Children ?? new List<Node>();

            var newByName = new Dictionary<string, List<int>>();
            for (int i = 0; i < newKids.Count; i++)
                newByName.GetOrAdd(newKids[i].Name).Add(i);
            var newUsed = new HashSet<int>();

            // Old children are consumed in order too (like new), so each client sibling is
            // compared against its own old counterpart — not always the first same-named one.
            // Without this, duplicated names (e.g. two "2077" quest blocks) would compare the
            // client's second instance against the old's first, misjudging client==old as
            // third-default.
            var oldByName = new Dictionary<string, List<int>>();
            for (int i = 0; i < oldKids.Count; i++)
                oldByName.GetOrAdd(oldKids[i].Name).Add(i);

            // Walk client first, preserving client order. Track how many same-named siblings
            // we've already consumed at this level so the emitted Change can carry the correct
            // sibling ordinal for the path segment (disambiguates duplicated names like 8034).
            var nameSeen = new Dictionary<string, int>();
            foreach (var cc in clientKids)
            {
                string nm = cc.Name;
                int seen = nameSeen.GetValueOrDefault(nm, 0);
                nameSeen[nm] = seen + 1;

                int? ni = null;
                if (newByName.TryGetValue(nm, out var list) && list.Count > 0)
                {
                    ni = list[0];
                    list.RemoveAt(0);
                    newUsed.Add(ni.Value);
                }
                Node? nc = ni.HasValue ? newKids[ni.Value] : null;

                Node? oc = null;
                if (oldByName.TryGetValue(nm, out var oldList) && oldList.Count > 0)
                {
                    int oi = oldList[0];
                    oldList.RemoveAt(0);
                    oc = oi < oldKids.Count ? oldKids[oi] : null;
                }

                BuildTarget(oc, nc, cc, AppendPath(path, nm), AppendIndex(indices, seen), twoWay, trustMode, strict, result);
            }

            // Anything in new that wasn't paired with a client sibling (and wasn't found in
            // client at all) is a server-only addition — append it.
            for (int i = 0; i < newKids.Count; i++)
            {
                if (newUsed.Contains(i)) continue;
                string nm = newKids[i].Name;
                bool clientHas = FindChildByName(clientKids, nm) != null;
                Node? oc = null;
                if (oldByName.TryGetValue(nm, out var oldList) && oldList.Count > 0)
                {
                    int oi = oldList[0];
                    oldList.RemoveAt(0);
                    oc = oi < oldKids.Count ? oldKids[oi] : null;
                }
                if (!clientHas)
                {
                    // New-only node: ADD at ordinal 0 (it's the first client-visible instance).
                    BuildTarget(oc, newKids[i], null, AppendPath(path, nm), AppendIndex(indices, 0), twoWay, trustMode, strict, result);
                }
                // Else: client had it but its sibling-slot was already paired, OR new skipped
                // it. We've already handled it above — don't double-emit.
            }
        }

        private static Node? FindChildByName(List<Node> kids, string name)
        {
            foreach (var c in kids)
                if (c.Name == name) return c;
            return null;
        }

        // ------------- emit helpers -------------

        private static void EmitDelete(Node clientNode, List<string> path, List<int> indices, MergeResult result)
        {
            var deletePath = new List<string>(path);
            var c = new Change(deletePath, ChangeOp.Delete, ToValueType(clientNode.Tag), null, sourceLine: 0)
            {
                Action = ChangeAction.Delete,
                SiblingIndices = CopyIndices(indices),
            };
            result.Changes.Add(c);
            result.Actions.Add(ChangeAction.Delete);
        }

        private static void EmitModify(
            Node newNode,
            List<string> path,
            List<int> indices,
            MergeResult result,
            ChangeAction action)
        {
            var modifyPath = new List<string>(path);
            var c = new Change(
                modifyPath,
                ChangeOp.Modify,
                ToValueType(newNode.Tag),
                newNode.Value,
                sourceLine: 0,
                vectorX: newNode.VectorX,
                vectorY: newNode.VectorY)
            {
                Action = action,
                SiblingIndices = CopyIndices(indices),
            };
            result.Changes.Add(c);
            result.Actions.Add(action);
        }

        private static void EmitAddFromSubtree(Node newNode, List<string> path, List<int> indices, MergeResult result)
        {
            var addPath = new List<string>(path);
            var c = new Change(
                addPath,
                ChangeOp.Add,
                ToValueType(newNode.Tag),
                newNode.Value,
                sourceLine: 0,
                subTree: BuildSubTree(newNode),
                vectorX: newNode.VectorX,
                vectorY: newNode.VectorY)
            {
                Action = ChangeAction.AddDefault,
                SiblingIndices = CopyIndices(indices),
            };
            result.Changes.Add(c);
            result.Actions.Add(ChangeAction.AddDefault);
        }

        private static List<int> CopyIndices(List<int> src)
        {
            var r = new List<int>(src.Count);
            r.AddRange(src);
            return r;
        }

        private static List<int> AppendIndex(List<int> base_, int index)
        {
            var r = new List<int>(base_.Count + 1);
            r.AddRange(base_);
            r.Add(index);
            return r;
        }

        private static Model.SubTree BuildSubTree(Node n)
        {
            switch (n.Tag)
            {
                case NodeTag.ImgDir:
                {
                    var sub = new Model.SubTree(n.Name, ValueType.Sub, null);
                    foreach (var ch in n.Children) sub.Children.Add(BuildSubTree(ch));
                    return sub;
                }
                case NodeTag.Vector:
                    return new Model.SubTree(n.Name, n.VectorX, n.VectorY);
                default:
                    return new Model.SubTree(n.Name, ToValueType(n.Tag), n.Value);
            }
        }

        private static List<string> AppendPath(List<string> path, string name)
        {
            var p = new List<string>(path.Count + 1);
            p.AddRange(path);
            p.Add(name);
            return p;
        }

        private static ValueType ToValueType(NodeTag tag) => tag switch
        {
            NodeTag.ImgDir => ValueType.Sub,
            NodeTag.String => ValueType.String,
            NodeTag.Int    => ValueType.Int,
            NodeTag.Short  => ValueType.Short,
            NodeTag.Long   => ValueType.Long,
            NodeTag.Float  => ValueType.Float,
            NodeTag.Double => ValueType.Double,
            NodeTag.Vector => ValueType.Vector,
            NodeTag.Null   => ValueType.Null,
            _ => ValueType.String,
        };
    }

    internal static class DictionaryExtensions
    {
        public static List<TValue> GetOrAdd<TKey, TValue>(this Dictionary<TKey, List<TValue>> d, TKey key)
            where TKey : notnull
        {
            if (!d.TryGetValue(key, out var list))
            {
                list = new List<TValue>();
                d[key] = list;
            }
            return list;
        }
    }
}
