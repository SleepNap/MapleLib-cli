using System.Collections.Generic;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleLib.XmlImgPatcher.Sync
{
    /// <summary>
    /// Walks a MapleLib <see cref="WzImage"/> (loaded from a client img) into a tree of
    /// <see cref="Node"/>s. Mirrors the contract used by the Java side: every leaf type
    /// maps to a corresponding <see cref="NodeTag"/>; container types (SubProperty,
    /// Canvas, Convex) are traversed recursively but canvas/sound/uol are intentionally
    /// NOT emitted — those are binary resources the server-side XML never describes, so
    /// the merge layer skips them by default (see ThreeWayMerge).
    ///
    /// Vector's X/Y are exposed via WzIntProperty children; we read them off as ints.
    /// </summary>
    public static class ImgNodeReader
    {
        public static Node Read(WzImage img)
        {
            var root = new Node(img.Name, NodeTag.ImgDir);
            foreach (WzImageProperty p in img.WzProperties)
            {
                var n = Convert(p);
                if (n != null) root.Children.Add(n);
            }
            return root;
        }

        private static Node? Convert(WzImageProperty p)
        {
            switch (p)
            {
                case WzSubProperty sub:
                {
                    var n = new Node(p.Name, NodeTag.ImgDir);
                    foreach (WzImageProperty child in sub.WzProperties)
                    {
                        var c = Convert(child);
                        if (c != null) n.Children.Add(c);
                    }
                    return n;
                }
                case WzStringProperty s: return new Node(p.Name, NodeTag.String, s.Value);
                case WzIntProperty i:     return new Node(p.Name, NodeTag.Int,    i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                case WzShortProperty sh:  return new Node(p.Name, NodeTag.Short,  sh.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                case WzLongProperty l:    return new Node(p.Name, NodeTag.Long,   l.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                case WzFloatProperty f:   return new Node(p.Name, NodeTag.Float,  f.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                case WzDoubleProperty d:  return new Node(p.Name, NodeTag.Double, d.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                case WzVectorProperty v:  return new Node(p.Name, NodeTag.Vector, v.X.Value, v.Y.Value);
                case WzNullProperty:      return new Node(p.Name, NodeTag.Null);
                // Binary resources / unknown: skip. Canvas/Sound/UOL extended types are filtered
                // out here so the merge layer never sees them — they exist in the client img
                // because the server doesn't manage them, and sync must preserve them.
                default: return null;
            }
        }
    }
}
