using System.Collections.Generic;
using MapleLib.XmlImgPatcher.Sync;

namespace MapleLib.XmlImgPatcher.Model
{
    /// <summary>
    /// One change extracted from a unified diff. Path is the node path under the root img,
    /// e.g. ["04031786", "info", "quest"].
    /// </summary>
    public sealed class Change
    {
        public IReadOnlyList<string> Path { get; }
        public ChangeOp Op { get; }
        public ValueType ValueType { get; }

        /// <summary>Leaf value for MODIFY / leaf-ADD.</summary>
        public string? Value { get; }

        /// <summary>Vector X (only when <see cref="ValueType"/> == Vector).</summary>
        public int VectorX { get; }

        /// <summary>Vector Y (only when <see cref="ValueType"/> == Vector).</summary>
        public int VectorY { get; }

        /// <summary>Sub-tree to insert for ADD operations on container nodes.</summary>
        public SubTree? SubTree { get; }

        /// <summary>Originating diff line number — used for error reporting.</summary>
        public int SourceLine { get; }

        /// <summary>
        /// Action marker for sync (three-way merge) changes. Null for diff-parsed changes —
        /// only sync populates this. Used to drive the human-review list and report.
        /// </summary>
        public ChangeAction? Action { get; init; }

        /// <summary>
        /// Sibling indices, parallel to <see cref="Path"/>: for each path segment, the 0-based
        /// ordinal among same-named siblings at that level (0 = first occurrence). Empty for
        /// diff-parsed changes (all segments resolve to the first occurrence), populated by sync
        /// to disambiguate duplicated container/leaf names (e.g. two "8034" quest blocks).
        /// </summary>
        public IReadOnlyList<int>? SiblingIndices { get; init; }

        /// <summary>
        /// Sibling ordinal for path segment i (excluding the root name). Returns 0 when
        /// <see cref="SiblingIndices"/> is null or shorter than i+1.
        /// </summary>
        public int SiblingIndexAt(int i)
        {
            if (SiblingIndices == null || i >= SiblingIndices.Count) return 0;
            return SiblingIndices[i];
        }

        public Change(
            IReadOnlyList<string> path,
            ChangeOp op,
            ValueType valueType,
            string? value,
            int sourceLine,
            SubTree? subTree = null,
            int vectorX = 0,
            int vectorY = 0)
        {
            Path = path;
            Op = op;
            ValueType = valueType;
            Value = value;
            SubTree = subTree;
            SourceLine = sourceLine;
            VectorX = vectorX;
            VectorY = vectorY;
        }

        public string PathString => string.Join("/", Path);
    }
}
