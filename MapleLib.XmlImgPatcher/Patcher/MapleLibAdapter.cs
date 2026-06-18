using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.WzProperties;
using MapleLib.XmlImgPatcher.Model;
using ValueType = MapleLib.XmlImgPatcher.Model.ValueType;

namespace MapleLib.XmlImgPatcher.Patcher
{
    /// <summary>
    /// Wraps MapleLib's WzImage I/O and exposes the small surface area we need for patching:
    /// load, find by path, set leaf value, add a (sub)tree, remove by path, save.
    /// </summary>
    public sealed class MapleLibAdapter
    {
        private readonly WzMapleVersion _mapleVersion;

        public MapleLibAdapter(WzMapleVersion mapleVersion)
        {
            _mapleVersion = mapleVersion;
        }

        public WzImage LoadImg(string inputPath)
        {
            byte[] iv = MapleLib.WzLib.Util.WzTool.GetIvByMapleVersion(_mapleVersion);
            var deserializer = new WzImgDeserializer(freeResources: true);
            string name = Path.GetFileName(inputPath);
            WzImage img = deserializer.WzImageFromIMGFile(inputPath, iv, name, out bool ok);
            if (!ok)
                throw new InvalidDataException($"Failed to parse img: {inputPath}");
            return img;
        }

        public void SaveImg(WzImage img, string outputPath)
        {
            // Force output path verbatim (MapleLib's serializer auto-appends .img otherwise).
            // Write through the .img-suffixed call only if file already ends with .img;
            // otherwise create the file and stream directly.
            string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using FileStream stream = File.Create(outputPath);
            byte[] outIv = MapleLib.WzLib.Util.WzTool.GetIvByMapleVersion(_mapleVersion);
            using var writer = new MapleLib.WzLib.Util.WzBinaryWriter(stream, outIv);
            img.SaveImage(writer);
        }

        // Returns the WzImageProperty at path, or null if absent. Path elements are case-sensitive.
        // If the first segment ends with ".img" and the lookup misses, retry with that segment
        // dropped — covers the case where the diff hunk includes the file-root imgdir.
        public WzImageProperty? GetByPath(WzImage img, IReadOnlyList<string> path)
        {
            if (path.Count == 0) return null;
            var hit = img.GetFromPath(string.Join("/", path));
            if (hit != null) return hit;
            if (path.Count >= 2 && path[0].EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = new List<string>(path.Count - 1);
                for (int i = 1; i < path.Count; i++) stripped.Add(path[i]);
                return img.GetFromPath(string.Join("/", stripped));
            }
            return null;
        }

        public IPropertyContainer? GetParent(WzImage img, IReadOnlyList<string> path)
        {
            if (path.Count == 0) return null;
            // Try as-is.
            var direct = TryGetParent(img, path);
            if (direct != null) return direct;
            // Fallback: strip leading "*.img" file-root segment.
            if (path.Count >= 2 && path[0].EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = new List<string>(path.Count - 1);
                for (int i = 1; i < path.Count; i++) stripped.Add(path[i]);
                return TryGetParent(img, stripped);
            }
            return null;
        }

        private static IPropertyContainer? TryGetParent(WzImage img, IReadOnlyList<string> path)
        {
            if (path.Count == 1) return img;
            var parentPath = new List<string>(path.Count - 1);
            for (int i = 0; i < path.Count - 1; i++) parentPath.Add(path[i]);
            var parentNode = img.GetFromPath(string.Join("/", parentPath));
            if (parentNode is IPropertyContainer pc) return pc;
            return null;
        }

        // -------- mutate helpers --------

        public void ApplyModify(WzImage img, Change c)
        {
            var node = GetByPath(img, c.Path)
                ?? throw new InvalidOperationException($"node not found: {c.PathString}");

            switch (c.ValueType)
            {
                case ValueType.String:
                    if (node is WzStringProperty sp) { sp.Value = c.Value ?? ""; return; }
                    break;
                case ValueType.Int:
                    if (node is WzIntProperty ip) { ip.Value = ParseInt(c.Value); return; }
                    break;
                case ValueType.Short:
                    if (node is WzShortProperty shp) { shp.Value = (short)ParseInt(c.Value); return; }
                    break;
                case ValueType.Long:
                    if (node is WzLongProperty lp) { lp.Value = ParseLong(c.Value); return; }
                    break;
                case ValueType.Float:
                    if (node is WzFloatProperty fp) { fp.Value = ParseFloat(c.Value); return; }
                    break;
                case ValueType.Double:
                    if (node is WzDoubleProperty dp) { dp.Value = ParseDouble(c.Value); return; }
                    break;
                case ValueType.Vector:
                    if (node is WzVectorProperty vp)
                    {
                        vp.X.Value = c.VectorX;
                        vp.Y.Value = c.VectorY;
                        return;
                    }
                    break;
                case ValueType.Null:
                    // nothing to update
                    return;
                case ValueType.Sub:
                    // Modifying a container via diff is unusual; ignore.
                    return;
            }
            throw new InvalidOperationException(
                $"type mismatch at {c.PathString}: diff says {c.ValueType}, node is {node.GetType().Name}");
        }

        public void ApplyAdd(WzImage img, Change c)
        {
            if (c.SubTree == null)
                throw new InvalidOperationException($"ADD requires SubTree at {c.PathString}");

            var parent = GetParent(img, c.Path)
                ?? throw new InvalidOperationException($"parent not found for {c.PathString}");

            string newName = c.Path[c.Path.Count - 1];
            // Refuse to overwrite existing — surface as failure so caller can log it.
            if (parent[newName] != null)
                throw new InvalidOperationException($"already exists: {c.PathString}");

            WzImageProperty newProp = BuildProperty(c.SubTree, overrideName: newName);
            parent.AddProperty(newProp);
        }

        public void ApplyDelete(WzImage img, Change c)
        {
            var parent = GetParent(img, c.Path)
                ?? throw new InvalidOperationException($"parent not found for {c.PathString}");
            string targetName = c.Path[c.Path.Count - 1];
            var existing = parent[targetName];
            if (existing == null)
            {
                // already gone — treat as no-op (idempotent delete)
                return;
            }
            parent.RemoveProperty(existing);
        }

        // -------- builders --------

        private static WzImageProperty BuildProperty(SubTree node, string? overrideName = null)
        {
            string name = overrideName ?? node.Name;
            switch (node.Type)
            {
                case ValueType.Sub:
                    {
                        var sub = new WzSubProperty(name);
                        foreach (var ch in node.Children)
                            sub.AddProperty(BuildProperty(ch));
                        return sub;
                    }
                case ValueType.String:
                    return new WzStringProperty(name, node.Value ?? "");
                case ValueType.Int:
                    return new WzIntProperty(name, ParseInt(node.Value));
                case ValueType.Short:
                    return new WzShortProperty(name, (short)ParseInt(node.Value));
                case ValueType.Long:
                    return new WzLongProperty(name, ParseLong(node.Value));
                case ValueType.Float:
                    return new WzFloatProperty(name, ParseFloat(node.Value));
                case ValueType.Double:
                    return new WzDoubleProperty(name, ParseDouble(node.Value));
                case ValueType.Vector:
                    return new WzVectorProperty(name, node.VectorX, node.VectorY);
                case ValueType.Null:
                    return new WzNullProperty(name);
                default:
                    throw new InvalidOperationException($"unsupported type for ADD: {node.Type}");
            }
        }

        // -------- value parsers (invariant culture, lenient) --------

        private static int ParseInt(string? s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

        private static long ParseLong(string? s) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0L;

        private static float ParseFloat(string? s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

        private static double ParseDouble(string? s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0d;
    }
}
