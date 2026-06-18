using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.XmlImgPatcher.Parser;
using MapleLib.XmlImgPatcher.Patcher;

namespace MapleLib.XmlImgPatcher
{
    /// <summary>
    /// CLI 入口。子命令：
    ///   patch           单文件应用 diff
    ///   dump-xml        单文件 img → xml
    ///   batch           批量应用 diff（按目录配对）
    ///   batch-dump-xml  批量 img → xml
    ///
    /// 退出码：
    ///   0 全部成功 / 1 部分失败但已写出 / 2 参数错 / 3 diff 解析错 /
    ///   4 img 解析错 / 5 img 写入错
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Force UTF-8 console output so Chinese text in -h and log messages displays correctly
            // on Windows (where cmd/powershell defaults to the system OEM code page).
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.InputEncoding = System.Text.Encoding.UTF8;
            }
            catch { /* harmless on non-tty */ }

            var positional = new List<string>();
            bool verbose = false, dryRun = false, strict = false;
            WzMapleVersion version = WzMapleVersion.GMS;
            string? fullXml = null;
            string? fullXmlDir = null;

            foreach (string a in args)
            {
                if (a == "-h" || a == "--help") { PrintHelp(Console.Out); return 0; }
                else if (a == "-v" || a == "--verbose") verbose = true;
                else if (a == "--dry-run") dryRun = true;
                else if (a == "--strict") strict = true;
                else if (a.StartsWith("--full-xml=", StringComparison.Ordinal))
                    fullXml = a.Substring("--full-xml=".Length);
                else if (a.StartsWith("--full-xml-dir=", StringComparison.Ordinal))
                    fullXmlDir = a.Substring("--full-xml-dir=".Length);
                else if (a.StartsWith("--version=", StringComparison.Ordinal))
                {
                    string v = a.Substring("--version=".Length);
                    if (!Enum.TryParse(v, ignoreCase: true, out version))
                    {
                        Console.Error.WriteLine($"unknown --version: {v}");
                        return 2;
                    }
                }
                else if (a.StartsWith("-"))
                {
                    Console.Error.WriteLine($"unknown option: {a}");
                    return 2;
                }
                else positional.Add(a);
            }

            // Decide subcommand. Default: `patch` (3 positionals, backwards-compat).
            string mode = "patch";
            if (positional.Count > 0 &&
                (positional[0] == "patch"
                 || positional[0] == "dump-xml"
                 || positional[0] == "batch"
                 || positional[0] == "batch-dump-xml"))
            {
                mode = positional[0];
                positional.RemoveAt(0);
            }

            return mode switch
            {
                "dump-xml" => RunDumpXml(positional, version, verbose),
                "batch" => RunBatch(positional, version, verbose, dryRun, strict, fullXmlDir),
                "batch-dump-xml" => RunBatchDumpXml(positional, version, verbose),
                _ => RunPatch(positional, version, verbose, dryRun, strict, fullXml),
            };
        }

        // ---------- patch ----------
        private static int RunPatch(List<string> positional, WzMapleVersion version, bool verbose, bool dryRun, bool strict, string? fullXml)
        {
            if (positional.Count != 3)
            {
                PrintHelp(Console.Error);
                return 2;
            }

            string inputImg = positional[0];
            string diffPath = positional[1];
            string outputImg = positional[2];

            if (!File.Exists(inputImg)) { Console.Error.WriteLine($"input not found: {inputImg}"); return 2; }
            if (!File.Exists(diffPath)) { Console.Error.WriteLine($"diff not found: {diffPath}"); return 2; }

            return PatchOne(inputImg, diffPath, outputImg, fullXml, version, verbose, dryRun, strict);
        }

        private static int PatchOne(string inputImg, string diffPath, string outputImg,
            string? fullXml, WzMapleVersion version, bool verbose, bool dryRun, bool strict)
        {
            List<Model.Change> changes;
            try
            {
                var parser = new DiffParser(fullXml);
                changes = parser.ParseFile(diffPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"diff parse failed: {ex.Message}");
                if (verbose) Console.Error.WriteLine(ex);
                return 3;
            }

            try
            {
                var adapter = new MapleLibAdapter(version);
                var patcher = new ImgPatcher(adapter, verbose, strict, dryRun, Console.Out, Console.Error);
                var result = patcher.Patch(inputImg, changes, outputImg);
                return result.Failed == 0 ? 0 : 1;
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"img parse failed: {ex.Message}");
                if (verbose) Console.Error.WriteLine(ex);
                return 4;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"img write failed: {ex.Message}");
                if (verbose) Console.Error.WriteLine(ex);
                return 5;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"unhandled error: {ex.Message}");
                if (verbose) Console.Error.WriteLine(ex);
                return 1;
            }
        }

        // ---------- dump-xml ----------
        private static int RunDumpXml(List<string> positional, WzMapleVersion version, bool verbose)
        {
            if (positional.Count != 2)
            {
                PrintHelp(Console.Error);
                return 2;
            }
            string inputImg = positional[0];
            string outputXml = positional[1];
            if (!File.Exists(inputImg)) { Console.Error.WriteLine($"input not found: {inputImg}"); return 2; }
            return DumpOne(inputImg, outputXml, version, verbose);
        }

        private static int DumpOne(string inputImg, string outputXml, WzMapleVersion version, bool verbose)
        {
            try
            {
                var adapter = new MapleLibAdapter(version);
                WzImage img = adapter.LoadImg(inputImg);
                img.ParseEverything = true;
                if (!img.Parsed) img.ParseImage(true);

                string? dir = Path.GetDirectoryName(Path.GetFullPath(outputXml));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var ser = new WzClassicXmlSerializer(indentation: 2, lineBreakType: LineBreak.Unix, exportbase64: false);
                ser.SerializeImage(img, outputXml);
                Console.Out.WriteLine($"[ok] dump-xml {inputImg} -> {outputXml}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"dump-xml failed for {inputImg}: {ex.Message}");
                if (verbose) Console.Error.WriteLine(ex);
                return 1;
            }
        }

        // ---------- batch ----------
        private static int RunBatch(List<string> positional, WzMapleVersion version, bool verbose, bool dryRun, bool strict, string? fullXmlDir)
        {
            if (positional.Count != 3)
            {
                PrintHelp(Console.Error);
                return 2;
            }
            string imgDir = positional[0];
            string diffDir = positional[1];
            string outDir = positional[2];

            if (!Directory.Exists(imgDir)) { Console.Error.WriteLine($"img dir not found: {imgDir}"); return 2; }
            if (!Directory.Exists(diffDir)) { Console.Error.WriteLine($"diff dir not found: {diffDir}"); return 2; }
            if (fullXmlDir != null && !Directory.Exists(fullXmlDir))
            {
                Console.Error.WriteLine($"full-xml dir not found: {fullXmlDir}");
                return 2;
            }

            // Find all *.diff under diff dir.
            var diffFiles = Directory.GetFiles(diffDir, "*.diff", SearchOption.AllDirectories);
            Array.Sort(diffFiles, StringComparer.OrdinalIgnoreCase);

            int ok = 0, fail = 0, skip = 0;
            var failList = new List<string>();
            foreach (string diffPath in diffFiles)
            {
                // diff_rel = relative path of the diff under diffDir, e.g. "String.wz/Mob.img.xml.diff"
                string diffRel = Path.GetRelativePath(diffDir, diffPath);
                // Strip trailing ".xml.diff" → "String.wz/Mob.img"
                string? imgRel = StripDiffSuffix(diffRel);
                if (imgRel == null)
                {
                    Console.Error.WriteLine($"[skip] not a *.img.xml.diff: {diffRel}");
                    skip++;
                    continue;
                }
                // The diff side typically lives under server-style directories like "Quest.wz/...";
                // the client side uses "Quest/...". Try both, preferring the as-is path.
                string inputImg = ResolveClientImgPath(imgDir, imgRel);
                string outputImg = Path.Combine(outDir, StripWzSuffixFromPath(imgRel));

                if (!File.Exists(inputImg))
                {
                    Console.Error.WriteLine($"[skip] no matching client img: {imgRel}");
                    skip++;
                    failList.Add($"{imgRel} (no input img)");
                    continue;
                }

                string? fullXmlForThis = null;
                if (fullXmlDir != null)
                {
                    // diff "String.wz/Mob.img.xml.diff" → full xml "String.wz/Mob.img.xml"
                    string xmlRel = diffRel.Substring(0, diffRel.Length - ".diff".Length);
                    string candidate = Path.Combine(fullXmlDir, xmlRel);
                    if (File.Exists(candidate)) fullXmlForThis = candidate;
                }

                Console.Out.WriteLine("================================================================");
                Console.Out.WriteLine($"[batch] {diffRel}");
                Console.Out.WriteLine($"        input : {inputImg}");
                Console.Out.WriteLine($"        output: {outputImg}");
                if (fullXmlForThis != null)
                    Console.Out.WriteLine($"        seed  : {fullXmlForThis}");

                int rc = PatchOne(inputImg, diffPath, outputImg, fullXmlForThis, version, verbose, dryRun, strict);
                if (rc == 0) ok++;
                else { fail++; failList.Add($"{imgRel} (rc={rc})"); }
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine("================ BATCH SUMMARY ================");
            Console.Out.WriteLine($"ok:   {ok}");
            Console.Out.WriteLine($"fail: {fail}");
            Console.Out.WriteLine($"skip: {skip}");
            foreach (var f in failList) Console.Out.WriteLine($"  - {f}");
            return fail == 0 ? 0 : 1;
        }

        private static string? StripDiffSuffix(string rel)
        {
            if (rel.EndsWith(".img.xml.diff", StringComparison.OrdinalIgnoreCase))
                return rel.Substring(0, rel.Length - ".xml.diff".Length); // keep ".img"
            if (rel.EndsWith(".xml.diff", StringComparison.OrdinalIgnoreCase))
                return rel.Substring(0, rel.Length - ".xml.diff".Length);
            return null;
        }

        // Server diffs are organized as "Quest.wz/Check.img.xml.diff" but the client stores
        // "Quest/Check.img" (no `.wz` suffix on the directory). Strip `.wz` from each path
        // segment so configurations like that map cleanly.
        private static string StripWzSuffixFromPath(string rel)
        {
            string[] parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].EndsWith(".wz", StringComparison.OrdinalIgnoreCase))
                    parts[i] = parts[i].Substring(0, parts[i].Length - 3);
            }
            return string.Join(Path.DirectorySeparatorChar, parts);
        }

        // Look for the client img file. Tries the as-is rel path first, then falls back to
        // the `.wz`-stripped variant for typical server-vs-client layout mismatches.
        private static string ResolveClientImgPath(string imgDir, string imgRel)
        {
            string asIs = Path.Combine(imgDir, imgRel);
            if (File.Exists(asIs)) return asIs;
            string stripped = Path.Combine(imgDir, StripWzSuffixFromPath(imgRel));
            return stripped;
        }

        // ---------- batch-dump-xml ----------
        private static int RunBatchDumpXml(List<string> positional, WzMapleVersion version, bool verbose)
        {
            if (positional.Count != 2)
            {
                PrintHelp(Console.Error);
                return 2;
            }
            string imgDir = positional[0];
            string outDir = positional[1];
            if (!Directory.Exists(imgDir)) { Console.Error.WriteLine($"img dir not found: {imgDir}"); return 2; }

            var imgFiles = Directory.GetFiles(imgDir, "*.img", SearchOption.AllDirectories);
            Array.Sort(imgFiles, StringComparer.OrdinalIgnoreCase);

            int ok = 0, fail = 0;
            foreach (string img in imgFiles)
            {
                string rel = Path.GetRelativePath(imgDir, img);
                string outXml = Path.Combine(outDir, rel + ".xml");
                int rc = DumpOne(img, outXml, version, verbose);
                if (rc == 0) ok++; else fail++;
            }
            Console.Out.WriteLine();
            Console.Out.WriteLine($"batch-dump-xml: {ok} ok, {fail} fail");
            return fail == 0 ? 0 : 1;
        }

        // ---------- help ----------
        private static void PrintHelp(TextWriter w)
        {
            w.WriteLine("xml-img-patcher  -  把服务端 *.img.xml 的 git diff 应用到客户端 *.img");
            w.WriteLine();
            w.WriteLine("用法：");
            w.WriteLine("  xml-img-patcher patch          <input.img> <diff> <output.img> [选项]");
            w.WriteLine("  xml-img-patcher dump-xml       <input.img> <output.xml>        [选项]");
            w.WriteLine("  xml-img-patcher batch          <img目录> <diff目录> <输出目录> [选项]");
            w.WriteLine("  xml-img-patcher batch-dump-xml <img目录> <xml输出目录>         [选项]");
            w.WriteLine();
            w.WriteLine("子命令说明：");
            w.WriteLine("  patch           对一个 .img 文件应用一个 .diff，输出新 .img。");
            w.WriteLine("                  会保留 PNG/音效/UOL 等所有 diff 没碰过的二进制资源。");
            w.WriteLine("  dump-xml        把 .img 转成服务端格式的 .xml，方便肉眼看或对比。");
            w.WriteLine("  batch           批量版的 patch。按文件名自动配对：");
            w.WriteLine("                    diff 目录下 a/b/Foo.img.xml.diff");
            w.WriteLine("                    → 找 img 目录里的 a/b/Foo.img");
            w.WriteLine("                    → 写到输出目录的 a/b/Foo.img");
            w.WriteLine("                  diff 目录可以多层嵌套，工具会递归扫所有 *.diff。");
            w.WriteLine("                  没找到对应 img 的 diff 会跳过并在最后汇总。");
            w.WriteLine("  batch-dump-xml  批量版的 dump-xml。递归把目录下所有 .img 都转成 .xml。");
            w.WriteLine();
            w.WriteLine("通用选项：");
            w.WriteLine("  -h, --help             显示这个帮助。");
            w.WriteLine("  -v, --verbose          失败时打印完整堆栈，方便排查。");
            w.WriteLine("      --version=<KEY>    WZ 加密 IV：GMS / EMS / BMS / CLASSIC，默认 GMS。");
            w.WriteLine();
            w.WriteLine("patch / batch 专用选项：");
            w.WriteLine("      --dry-run          只解析+模拟应用，不写文件。用来先看看哪些会失败、");
            w.WriteLine("                         不污染目标文件，确认 OK 之后再去掉这个选项实跑。");
            w.WriteLine("      --strict           任意一条变更失败立即中止。默认是跑完所有再汇总。");
            w.WriteLine("      --full-xml=<文件>  服务端 patch 后的完整 XML，用来给短 hunk 提供");
            w.WriteLine("                         上下文（深嵌套小改动靠这个才能定位到节点路径）。");
            w.WriteLine("                         （仅 patch 子命令用）");
            w.WriteLine("      --full-xml-dir=<目录>  跟 --full-xml 同样作用，但是按 batch 的目录");
            w.WriteLine("                         结构去配对。建议批量跑时都加上。（仅 batch 用）");
            w.WriteLine();
            w.WriteLine("退出码：");
            w.WriteLine("  0  全部成功");
            w.WriteLine("  1  部分变更失败，但 .img 已经写出（非 strict 模式）");
            w.WriteLine("  2  参数错误或文件/目录不存在");
            w.WriteLine("  3  diff 解析失败");
            w.WriteLine("  4  img 解析失败");
            w.WriteLine("  5  img 写入失败");
            w.WriteLine();
            w.WriteLine("例子：");
            w.WriteLine("  # 单文件 patch");
            w.WriteLine("  xml-img-patcher patch ^");
            w.WriteLine("    \"E:\\BeiDou-Client\\EN\\String\\Mob.img\" ^");
            w.WriteLine("    \"C:\\diff_20260618\\wz\\String.wz\\Mob.img.xml.diff\" ^");
            w.WriteLine("    \"C:\\out\\Mob.img\"");
            w.WriteLine();
            w.WriteLine("  # 单文件 patch + 提供完整 XML 上下文（推荐，diff 短时必备）");
            w.WriteLine("  xml-img-patcher patch ^");
            w.WriteLine("    --full-xml=\"C:\\upgrade_20260618\\wz\\String.wz\\Mob.img.xml\" ^");
            w.WriteLine("    \"E:\\BeiDou-Client\\EN\\String\\Mob.img\" ^");
            w.WriteLine("    \"C:\\diff_20260618\\wz\\String.wz\\Mob.img.xml.diff\" ^");
            w.WriteLine("    \"C:\\out\\Mob.img\"");
            w.WriteLine();
            w.WriteLine("  # 批量：把整个 wz/ 目录下所有 diff 都打到 EN/ 目录的客户端 img 上");
            w.WriteLine("  xml-img-patcher batch ^");
            w.WriteLine("    --full-xml-dir=\"C:\\upgrade_20260618\\wz\" ^");
            w.WriteLine("    \"E:\\BeiDou-Client\\EN\" ^");
            w.WriteLine("    \"C:\\diff_20260618\\wz\" ^");
            w.WriteLine("    \"C:\\out\\EN\"");
            w.WriteLine();
            w.WriteLine("  # 批量：先试跑看错误");
            w.WriteLine("  xml-img-patcher batch --dry-run ^");
            w.WriteLine("    \"E:\\BeiDou-Client\\EN\" \"C:\\diff_20260618\\wz\" \"C:\\out\\EN\"");
            w.WriteLine();
            w.WriteLine("  # 批量导出 XML（递归整个目录）");
            w.WriteLine("  xml-img-patcher batch-dump-xml ^");
            w.WriteLine("    \"E:\\BeiDou-Client\\Data\" \"C:\\out_xml\\Data\"");
        }
    }
}
