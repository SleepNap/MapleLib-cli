using System.Collections.Generic;

namespace MapleLib.XmlImgPatcher.Sync
{
    /// <summary>
    /// Lightweight node model used by sync's three-way comparison. Same shape on both C#
    /// and Java implementations — the comparison engine (ThreeWayMerge) operates only on
    /// this contract, never on the raw WzImageProperty / StAX stream.
    ///
    /// Lightweight by design: only the fields three-way merge needs, in a normal order
    /// (children preserve insertion order). No parent pointer; paths are reconstructed
    /// by the caller from the recursive call stack.
    /// </summary>
    public sealed class Node
    {
        public string Name { get; }
        public NodeTag Tag { get; }

        /// <summary>Leaf value. null for containers (imgdir).</summary>
        public string? Value { get; }

        /// <summary>X / Y components. Only meaningful when <see cref="Tag"/> == Vector.</summary>
        public int VectorX { get; }
        public int VectorY { get; }

        /// <summary>Ordered children. Empty for leaves.</summary>
        public List<Node> Children { get; } = new();

        public Node(string name, NodeTag tag)
        {
            Name = name;
            Tag = tag;
        }

        public Node(string name, NodeTag tag, string? value)
        {
            Name = name;
            Tag = tag;
            Value = value;
        }

        public Node(string name, NodeTag tag, int x, int y)
        {
            Name = name;
            Tag = tag;
            VectorX = x;
            VectorY = y;
        }

        /// <summary>True for container nodes (imgdir). Children are meaningful.</summary>
        public bool IsContainer => Tag == NodeTag.ImgDir;
    }

    /// <summary>
    /// Node tag. Mirrors the server-side XML tag set with one-to-one mapping. canvas / sound
    /// / uol are excluded by intent — the sync contract treats them as skip-only (see
    /// <see cref="NodeTag"/> documentation in the merge layer).
    /// </summary>
    public enum NodeTag
    {
        ImgDir,
        String,
        Int,
        Short,
        Long,
        Float,
        Double,
        Vector,
        Null,
    }
}
