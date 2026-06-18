using System.Collections.Generic;

namespace MapleLib.XmlImgPatcher.Model
{
    /// <summary>
    /// In-memory representation of a node parsed from a diff line, used for ADD operations
    /// where the new node may itself be a container with children.
    /// </summary>
    public sealed class SubTree
    {
        public string Name { get; }
        public ValueType Type { get; }

        /// <summary>Leaf value (null for containers).</summary>
        public string? Value { get; }

        /// <summary>X component for <see cref="ValueType.Vector"/>.</summary>
        public int VectorX { get; }

        /// <summary>Y component for <see cref="ValueType.Vector"/>.</summary>
        public int VectorY { get; }

        /// <summary>Children — only populated for <see cref="ValueType.Sub"/>.</summary>
        public List<SubTree> Children { get; } = new();

        public SubTree(string name, ValueType type, string? value)
        {
            Name = name;
            Type = type;
            Value = value;
        }

        public SubTree(string name, int x, int y)
        {
            Name = name;
            Type = ValueType.Vector;
            Value = null;
            VectorX = x;
            VectorY = y;
        }
    }
}
