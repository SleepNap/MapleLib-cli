using System.Collections.Generic;

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
