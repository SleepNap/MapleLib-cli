using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.WzProperties;
using MapleLib.XmlImgPatcher.Parser;
using MapleLib.XmlImgPatcher.Patcher;
using MapleLib.XmlImgPatcher.Sync;

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
            bool verbose = false, dryRun = false, strict = false, linuxLineBreak = false;
            int indent = 4; // dump-xml 缩进空格数（与 Java 版默认 4 对齐）
            WzMapleVersion version = WzMapleVersion.GMS;
            string? fullXml = null;
            string? fullXmlDir = null;
            // export 子命令参数（与 Java 版 ExportCommand 对齐）
            string? exportFrom = null;
            string? exportRepo = null;
            string? exportOutXml = null;
            string? exportOutDiff = null;
            var exportPrefixes = new List<string>();
            bool exportNoDiff = false;
            int exportContext = 30;
            // sync 子命令参数（与 Java 版 SyncCommand 对齐）
            string? syncServer = null;
            string? syncRef = null;
            string? syncClient = null;
            string? syncOut = null;
            bool syncInPlace = false;
            bool syncStrict = false;
            string syncMode = "review";
            string? syncReviewOut = null;

            foreach (string a in args)
            {
                if (a == "-h" || a == "--help") { PrintHelp(Console.Out); return 0; }
                else if (a == "-V" || a == "--version") { Console.Out.WriteLine("xml-img-patcher 0.1.0"); return 0; }
                else if (a == "-v" || a == "--verbose") verbose = true;
                else if (a == "--dry-run") dryRun = true;
                else if (a == "--strict") strict = true;
                else if (a == "--linux") linuxLineBreak = true;
                else if (a.StartsWith("--indent=", StringComparison.Ordinal))
                {
                    if (!int.TryParse(a.Substring("--indent=".Length), out indent) || indent <= 0)
                    {
                        Console.Error.WriteLine($"invalid --indent value, must be positive integer");
                        return 2;
                    }
                }
                else if (a == "--no-diff") exportNoDiff = true;
                else if (a.StartsWith("--full-xml=", StringComparison.Ordinal))
                {
                    fullXml = a.Substring("--full-xml=".Length);
                    if (!string.IsNullOrEmpty(fullXml) && !File.Exists(fullXml))
                        Console.Error.WriteLine($"[warn] --full-xml 文件不存在: {fullXml}，将不使用路径回退");
                }
                else if (a.StartsWith("--full-xml-dir=", StringComparison.Ordinal))
                {
                    fullXmlDir = a.Substring("--full-xml-dir=".Length);
                    if (!string.IsNullOrEmpty(fullXmlDir) && !Directory.Exists(fullXmlDir))
                        Console.Error.WriteLine($"[warn] --full-xml-dir 目录不存在: {fullXmlDir}，将不使用路径回退");
                }
                else if (a.StartsWith("--from=", StringComparison.Ordinal))
                    exportFrom = a.Substring("--from=".Length);
                else if (a.StartsWith("--repo=", StringComparison.Ordinal))
                    exportRepo = a.Substring("--repo=".Length);
                else if (a.StartsWith("--out-xml=", StringComparison.Ordinal))
                    exportOutXml = a.Substring("--out-xml=".Length);
                else if (a.StartsWith("--out-diff=", StringComparison.Ordinal))
                    exportOutDiff = a.Substring("--out-diff=".Length);
                else if (a.StartsWith("--prefix=", StringComparison.Ordinal))
                    exportPrefixes.Add(a.Substring("--prefix=".Length));
                else if (a.StartsWith("--server=", StringComparison.Ordinal))
                    syncServer = a.Substring("--server=".Length);
                else if (a.StartsWith("--ref=", StringComparison.Ordinal))
                    syncRef = a.Substring("--ref=".Length);
                else if (a.StartsWith("--client=", StringComparison.Ordinal))
                    syncClient = a.Substring("--client=".Length);
                else if (a.StartsWith("--out=", StringComparison.Ordinal))
                    syncOut = a.Substring("--out=".Length);
                else if (a == "--in-place") syncInPlace = true;
                else if (a.StartsWith("--mode=", StringComparison.Ordinal))
                    syncMode = a.Substring("--mode=".Length);
                else if (a.StartsWith("--review-out=", StringComparison.Ordinal))
                    syncReviewOut = a.Substring("--review-out=".Length);
                else if (a.StartsWith("--context=", StringComparison.Ordinal))
                {
                    if (!int.TryParse(a.Substring("--context=".Length), out exportContext))
                    {
                        Console.Error.WriteLine($"invalid --context value, must be integer");
                        return 2;
                    }
                }
                else if (a.StartsWith("--iv=", StringComparison.Ordinal))
                {
                    string v = a.Substring("--iv=".Length);
                    if (!TryParseIv(v, out version))
                    {
                        Console.Error.WriteLine($"unknown --iv: {v}（可用值: gms / ems / bms / cms / classic）");
                        return 2;
                    }
                }
                else if (a.StartsWith("--version=", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("[warn] --version=<KEY> 已弃用，请改用 --iv=<KEY>");
                    string v = a.Substring("--version=".Length);
                    if (!TryParseIv(v, out version))
                    {
                        Console.Error.WriteLine($"unknown --version: {v}（可用值: GMS / EMS / BMS / CLASSIC）");
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
            var knownCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "patch", "dump-xml", "batch", "batch-dump-xml", "verify", "dump-changes", "export", "sync" };
            if (positional.Count > 0 && knownCommands.Contains(positional[0]))
            {
                mode = positional[0];
                positional.RemoveAt(0);
            }
            else if (positional.Count > 0 && !knownCommands.Contains(positional[0]) && positional.Count != 3)
            {
                // positional[0] could be a subcommand the user typed wrong, or a file path.
                // If it's 3 positionals they probably typed "patch" implicitly (backwards-compat).
                // If it's 1 positional it's probably a misspelled subcommand.
                Console.Error.WriteLine($"unknown command: {positional[0]}");
                PrintHelp(Console.Error);
                return 2;
            }

            return mode switch
            {
                "dump-xml" => RunDumpXml(positional, version, verbose, linuxLineBreak, indent),
                "batch" => RunBatch(positional, version, verbose, dryRun, strict, fullXmlDir),
                "batch-dump-xml" => RunBatchDumpXml(positional, version, verbose, linuxLineBreak, indent),
                "verify" => RunVerify(positional, version, verbose, fullXmlDir),
                "dump-changes" => RunDumpChanges(positional, fullXml),
                "export" => RunExport(exportFrom, exportRepo, exportOutXml, exportOutDiff, exportPrefixes, exportNoDiff, exportContext, verbose),
                "sync" => RunSync(positional, syncServer, exportRepo, syncRef, exportFrom, syncClient, syncOut,
                    exportPrefixes, version, syncInPlace, syncStrict, syncMode, syncReviewOut, dryRun, verbose),
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

            // Empty / non-diff detection. A legitimate diff always has `@@` hunk headers;
            // a file with zero hunks is either an empty diff (no changes) or not a diff at
            // all. Distinguish: if the file contains any diff marker (`diff --git`, `--- `,
            // `+++ `, `@@`) treat it as a legitimately empty diff and warn; if none, treat
            // it as "not a diff file" and exit 3 per README.
            if (changes.Count == 0)
            {
                bool looksLikeDiff = File.ReadLines(diffPath).Any(l =>
                    l.StartsWith("diff --git", StringComparison.Ordinal)
                    || l.StartsWith("@@", StringComparison.Ordinal)
                    || l.StartsWith("--- ", StringComparison.Ordinal)
                    || l.StartsWith("+++ ", StringComparison.Ordinal));
                if (!looksLikeDiff)
                {
                    Console.Error.WriteLine($"diff parse failed: {diffPath} 不是 unified diff 文件（未找到 diff/hunk 头）");
                    return 3;
                }
                Console.Error.WriteLine($"[warn] {diffPath} 解析到 0 条变更（可能是空 diff），仍继续写出 img");
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
        private static int RunDumpXml(List<string> positional, WzMapleVersion version, bool verbose, bool linuxLineBreak, int indent)
        {
            if (positional.Count != 2)
            {
                PrintHelp(Console.Error);
                return 2;
            }
            string inputImg = positional[0];
            string outputXml = positional[1];
            if (!File.Exists(inputImg)) { Console.Error.WriteLine($"input not found: {inputImg}"); return 2; }
            return DumpOne(inputImg, outputXml, version, verbose, linuxLineBreak, indent);
        }

        private static int DumpOne(string inputImg, string outputXml, WzMapleVersion version, bool verbose, bool linuxLineBreak, int indent)
        {
            try
            {
                var adapter = new MapleLibAdapter(version);
                WzImage img = adapter.LoadImg(inputImg);
                img.ParseEverything = true;
                if (!img.Parsed) img.ParseImage(true);

                string? dir = Path.GetDirectoryName(Path.GetFullPath(outputXml));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var lineBreak = linuxLineBreak ? LineBreak.Unix : LineBreak.Windows;
                var ser = new WzClassicXmlSerializer(indentation: indent, lineBreakType: lineBreak, exportbase64: false);
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

        // ---------- dump-changes ----------
        // dump-changes <diff> [--full-xml=<path>]
        // Debug helper: print every Change the DiffParser produces, one per line.
        private static int RunDumpChanges(List<string> positional, string? fullXml)
        {
            if (positional.Count < 1)
            {
                Console.Error.WriteLine("usage: dump-changes <diff> [--full-xml=<path>]");
                return 2;
            }
            string diffPath = positional[0];
            if (!File.Exists(diffPath)) { Console.Error.WriteLine($"diff not found: {diffPath}"); return 2; }
            try
            {
                var parser = new DiffParser(fullXml);
                var changes = parser.ParseFile(diffPath);
                foreach (var c in changes)
                {
                    string val = c.Value == null ? "<null>" : c.Value.Replace("\n", "\\n");
                    if (val.Length > 80) val = val.Substring(0, 80) + "…";
                    Console.Out.WriteLine($"{c.Op,-6} {c.PathString} :: {c.ValueType} = {val}  (line {c.SourceLine})");
                }
                Console.Out.WriteLine($"total: {changes.Count} changes");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"dump-changes failed: {ex.Message}");
                return 3;
            }
        }

        // ---------- export ----------
        // 从 git 仓库导出补丁数据：把指定起点之后变更的 wz xml 抽出来，并生成对应的 diff。
        // 与 Java 版 ExportCommand 对齐：--from 支持 commit hash / git ref，也支持 datetime
        // （yyyy-MM-dd / yyyy-MM-ddTHH:mm:ss 等），datetime 模式会找该时间点之前最近的一个
        // commit 作为起点，从而把该时间点之后的所有变更全部包含进来。
        private static int RunExport(string? from, string? repoOpt, string? outXmlOpt, string? outDiffOpt,
            List<string> prefixes, bool noDiff, int context, bool verbose)
        {
            if (string.IsNullOrEmpty(from))
            {
                Console.Error.WriteLine("usage: export --from=<commit-hash|datetime> [--repo=<dir>] [--out-xml=<dir>] [--out-diff=<dir>] [--prefix=<p>]... [--no-diff] [--context=<N>]");
                return 2;
            }

            string startDir = repoOpt != null
                ? Path.GetFullPath(repoOpt)
                : Path.GetFullPath(Directory.GetCurrentDirectory());
            string repoRoot = FindRepoRoot(startDir);
            if (!Directory.Exists(Path.Combine(repoRoot, ".git")))
            {
                Console.Error.WriteLine($"[err] 不是 git 仓库（找不到 .git 目录）: {repoRoot}");
                return 2;
            }

            string fromCommit;
            try
            {
                fromCommit = ResolveFromCommit(from, repoRoot);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[err] 解析 --from 失败: {ex.Message}");
                return 2;
            }

            List<string> effPrefixes = (prefixes == null || prefixes.Count == 0)
                ? new List<string> { "gms-server/wz", "gms-server/wz-zh-CN" }
                : prefixes;
            string effOutXml = outXmlOpt ?? DefaultExportDir("upgrade_");
            string effOutDiff = outDiffOpt ?? DefaultExportDir("diff_");

            Console.Out.WriteLine("==========================================");
            Console.Out.WriteLine("  补丁导出");
            Console.Out.WriteLine($"  起点:     {from}{(fromCommit == from ? "" : "  →  " + fromCommit)}");
            Console.Out.WriteLine($"  仓库:     {repoRoot}");
            Console.Out.WriteLine($"  前缀:     [{string.Join(", ", effPrefixes)}]");
            Console.Out.WriteLine($"  xml out:  {effOutXml}");
            Console.Out.WriteLine($"  diff out: {(noDiff ? "(skipped)" : effOutDiff)}");
            Console.Out.WriteLine("==========================================");

            DeleteDirIfExists(effOutXml);
            if (!noDiff) DeleteDirIfExists(effOutDiff);

            int totalAdded = 0, totalDeleted = 0, totalFailed = 0;
            foreach (string prefix in effPrefixes)
            {
                string shortName = LastSegment(prefix);
                string targetDir = Path.Combine(effOutXml, shortName);
                string diffDir = Path.Combine(effOutDiff, shortName);
                Console.Out.WriteLine();
                Console.Out.WriteLine($">>> {prefix}/");

                List<string> changed = GitListFiles(fromCommit, prefix, "ACMR", repoRoot);
                if (changed.Count == 0)
                {
                    Console.Out.WriteLine("  (无新增/修改)");
                }
                else
                {
                    foreach (string file in changed)
                    {
                        string src = Path.Combine(repoRoot, file.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(src))
                        {
                            Console.Out.WriteLine($"  ! 文件不存在(跳过): {file}");
                            continue;
                        }
                        string rel = file.Substring(prefix.Length + 1);
                        string dst = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                            File.Copy(src, dst, overwrite: true);
                            if (verbose) Console.Out.WriteLine($"  + {file}");
                            totalAdded++;
                        }
                        catch (Exception ex)
                        {
                            totalFailed++;
                            Console.Error.WriteLine($"  ! 复制失败: {file} — {ex.Message}");
                            continue;
                        }
                        if (!noDiff)
                        {
                            string diffDst = Path.Combine(diffDir, (rel + ".diff").Replace('/', Path.DirectorySeparatorChar));
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(diffDst)!);
                                WriteFileDiff(fromCommit, file, repoRoot, diffDst, context);
                            }
                            catch (Exception ex)
                            {
                                totalFailed++;
                                Console.Error.WriteLine($"  ! diff 失败: {file} — {ex.Message}");
                            }
                        }
                    }
                }

                List<string> deleted = GitListFiles(fromCommit, prefix, "D", repoRoot);
                if (deleted.Count > 0)
                {
                    Console.Out.WriteLine("  [删除文件]");
                    foreach (string f in deleted) Console.Out.WriteLine($"    [DEL] {f}");
                    totalDeleted += deleted.Count;
                }
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine("==========================================");
            Console.Out.WriteLine("  导出完成");
            Console.Out.WriteLine($"  新增/修改: {totalAdded}");
            Console.Out.WriteLine($"  删除:      {totalDeleted}");
            Console.Out.WriteLine($"  失败:      {totalFailed}");
            Console.Out.WriteLine($"  xml out:   {effOutXml}");
            if (!noDiff) Console.Out.WriteLine($"  diff out:  {effOutDiff}");
            Console.Out.WriteLine("==========================================");
            return totalFailed > 0 ? 1 : 0;
        }

        // 把 --from 解析成 commit hash：优先尝试 git rev-parse（hash/ref/HEAD~N），
        // 失败则按 datetime 处理（在该时间点之前找最近一个 commit）。
        private static string ResolveFromCommit(string input, string repoRoot)
        {
            string? resolved = TryGitRevParse(input, repoRoot);
            if (resolved != null) return resolved;
            return ResolveByDatetime(input, repoRoot);
        }

        private static string? TryGitRevParse(string refName, string repoRoot)
        {
            try
            {
                var (rc, stdout, _) = RunGit(repoRoot, "rev-parse", "--verify", refName + "^{commit}");
                string trimmed = stdout.Trim();
                if (rc == 0 && System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[0-9a-f]{40}$"))
                    return trimmed;
            }
            catch { /* fall through */ }
            return null;
        }

        private static string ResolveByDatetime(string input, string repoRoot)
        {
            DateTime? dt = TryParseDatetime(input);
            if (dt == null)
                throw new ArgumentException($"既不是 git ref 也不是合法 datetime: {input}");

            // git log --until=<ISO> -1 取该时间点之前最近的一个 commit
            string iso = dt.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
            var (rc, stdout, _) = RunGit(repoRoot, "log", "--until=" + iso, "--pretty=format:%H", "-1");
            string trimmed = stdout.Trim();
            if (rc != 0 || !System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[0-9a-f]{40}$"))
                throw new InvalidOperationException($"在 {iso} 之前找不到任何 commit");
            return trimmed;
        }

        private static DateTime? TryParseDatetime(string s)
        {
            string[] patterns =
            {
                "yyyy-MM-ddTHH:mm:ss",
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd HH:mm",
                "yyyy-MM-dd",
                "yyyy/MM/dd HH:mm:ss",
                "yyyy/MM/dd HH:mm",
                "yyyy/MM/dd",
                "yyyyMMdd",
            };
            foreach (string p in patterns)
            {
                if (DateTime.TryParseExact(s, p, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out DateTime dt))
                    return dt;
            }
            return null;
        }

        // git log --diff-filter=<ACMR|D> --name-only fromCommit..HEAD -- <prefix>
        private static List<string> GitListFiles(string fromCommit, string prefix, string diffFilter, string repoRoot)
        {
            var files = new List<string>();
            var (rc, stdout, _) = RunGit(repoRoot,
                "-c", "core.quotePath=false", "log",
                fromCommit + "..HEAD",
                "--diff-filter=" + diffFilter,
                "--name-only",
                "--pretty=format:",
                "--",
                prefix);
            if (rc != 0) return files;
            using var reader = new StringReader(stdout);
            string? line;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length >= 2 && line.StartsWith("\"") && line.EndsWith("\""))
                    line = line.Substring(1, line.Length - 2);
                if (line.Length > 0 && seen.Add(line)) files.Add(line);
            }
            return files;
        }

        // git diff --binary -U<ctx> fromCommit..HEAD -- <file>  > outFile
        private static void WriteFileDiff(string fromCommit, string file, string repoRoot, string outFile, int context)
        {
            int ctx = Math.Max(0, context);
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("core.quotePath=false");
            psi.ArgumentList.Add("diff");
            psi.ArgumentList.Add("--binary");
            psi.ArgumentList.Add("-U" + ctx);
            psi.ArgumentList.Add(fromCommit + "..HEAD");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(file);

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start git diff");
            // git diff 输出可能含二进制字节（--binary 模式），用字节级 stream 写
            using (var fs = File.Create(outFile))
            {
                proc.StandardOutput.BaseStream.CopyTo(fs);
            }
            proc.WaitForExit();
        }

        // 跑 git，捕获 stdout/stderr。
        private static (int rc, string stdout, string stderr) RunGit(string workingDir, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start git");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, stdout, stderr);
        }

        private static string FindRepoRoot(string start)
        {
            string? cur = Path.GetFullPath(start);
            while (cur != null)
            {
                if (Directory.Exists(Path.Combine(cur, ".git"))) return cur;
                cur = Path.GetDirectoryName(cur);
            }
            return start;
        }

        private static string LastSegment(string prefix)
        {
            int idx = prefix.LastIndexOf('/');
            return idx < 0 ? prefix : prefix.Substring(idx + 1);
        }

        private static string DefaultExportDir(string tag)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string date = DateTime.Now.ToString("yyyyMMdd");
            return Path.Combine(home, "Desktop", tag + date);
        }

        private static void DeleteDirIfExists(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { Console.Error.WriteLine($"  ! 无法删除目录: {dir} — {ex.Message}"); }
        }

        // ---------- verify ----------
        // verify <patched.img> <diff> [--full-xml=<path>]
        // Loads the patched img directly and checks every "+" change (Add/Modify) against the
        // node's runtime value. Bypasses dump-xml, so it tests the actual img contents — not
        // the XML-serialisation quirks (e.g. how '\n' is written).
        private static int RunVerify(List<string> positional, WzMapleVersion version, bool verbose, string? fullXmlDir)
        {
            if (positional.Count < 2 || positional.Count > 3)
            {
                Console.Error.WriteLine("usage: verify <patched.img> <diff> [--full-xml=<path> | <full-xml-dir>]");
                return 2;
            }
            string imgPath = positional[0];
            string diffPath = positional[1];
            if (!File.Exists(imgPath)) { Console.Error.WriteLine($"img not found: {imgPath}"); return 2; }
            if (!File.Exists(diffPath)) { Console.Error.WriteLine($"diff not found: {diffPath}"); return 2; }

            // If a 3rd positional is a directory, derive the full-xml by mirroring the diff's
            // relative path under that directory (so subdir structure is preserved).
            string? fullXml = fullXmlDir;
            if (positional.Count == 3)
            {
                string third = positional[2];
                if (Directory.Exists(third))
                {
                    // Mirror the diff's path layout, stripping ".diff".
                    string diffName = Path.GetFileName(diffPath);
                    string xmlName = diffName.Substring(0, diffName.Length - ".diff".Length);
                    // Walk up from the diff file to find a matching subdir under `third`.
                    string? hit = FindMirroredXml(third, diffPath, xmlName);
                    fullXml = hit;
                }
                else if (File.Exists(third))
                {
                    fullXml = third;
                }
            }

            List<Model.Change> changes;
            try
            {
                var parser = new DiffParser(fullXml);
                changes = parser.ParseFile(diffPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"diff parse failed: {ex.Message}");
                return 3;
            }

            // Empty / non-diff detection (same logic as patch).
            if (changes.Count == 0)
            {
                bool looksLikeDiff = File.ReadLines(diffPath).Any(l =>
                    l.StartsWith("diff --git", StringComparison.Ordinal)
                    || l.StartsWith("@@", StringComparison.Ordinal)
                    || l.StartsWith("--- ", StringComparison.Ordinal)
                    || l.StartsWith("+++ ", StringComparison.Ordinal));
                if (!looksLikeDiff)
                {
                    Console.Error.WriteLine($"diff parse failed: {diffPath} 不是 unified diff 文件（未找到 diff/hunk 头）");
                    return 3;
                }
                Console.Error.WriteLine($"[warn] {diffPath} 解析到 0 条变更（可能是空 diff）");
            }

            // Add/Modify changes must be present with the expected value.
            // Delete changes must be absent (patcher must have removed them).
            // Two cases where a "Delete" entry must be ignored:
            //   (1) Same path is also Added in the same diff (rename / re-insert at same name).
            //       The Add wins — node should be present.
            //   (2) An ancestor path is being Added as a Sub container. The diff often "removes the
            //       old container and all its leaves" then "adds a new container with new leaves".
            //       The leaf-level Deletes inside that container are redundant: rebuilding the
            //       container under the same name reseeds its contents. Don't validate them.
            // Flatten ADD SubTree entries into individual leaf-level expectations so
            // verify can check every node, not just the container's presence.
            var expected = new List<Model.Change>();
            foreach (var c in changes.Where(c => c.Op == Model.ChangeOp.Add || c.Op == Model.ChangeOp.Modify))
            {
                if (c.Op == Model.ChangeOp.Add && c.ValueType == Model.ValueType.Sub && c.SubTree != null)
                {
                    // c.Path already ends with SubTree.Name, so pass the parent of c.Path
                    // (drop last segment) as the prefix when recursing.
                    var parentPath = c.Path.Count > 0
                        ? new List<string>(c.Path.Take(c.Path.Count - 1))
                        : new List<string>();
                    FlattenSubTree(parentPath, c.SubTree, c.SourceLine, expected);
                }
                else
                    expected.Add(c);
            }
            var addPaths = new HashSet<string>(expected.Select(c => c.PathString));
            var addContainerPaths = new HashSet<string>(
                expected.Where(c => c.ValueType == Model.ValueType.Sub).Select(c => c.PathString));

            bool HasAddAncestor(string path)
            {
                int slash = path.LastIndexOf('/');
                while (slash > 0)
                {
                    string parent = path.Substring(0, slash);
                    if (addContainerPaths.Contains(parent)) return true;
                    slash = parent.LastIndexOf('/');
                }
                return false;
            }

            // Case (3): an ADD descendant exists under a DELETEd container. The diff textually
            // deletes a container (old tree) and re-adds it with new children (new tree), so the
            // DELETE path itself no longer holds post-patch — the re-add rebuilds it. Only a
            // DELETE with NO added descendant is a true removal. Descendants may be either
            // containers or leaves (e.g. a bare "+ <string>" whose parent imgdirs are context
            // lines) — check both, not just the container set.
            bool HasAddDescendant(string path)
            {
                string prefix = path + "/";
                foreach (var p in addPaths)
                    if (p.StartsWith(prefix, StringComparison.Ordinal)) return true;
                return false;
            }

            var deletes = changes
                .Where(c => c.Op == Model.ChangeOp.Delete
                            && !addPaths.Contains(c.PathString)
                            && !HasAddAncestor(c.PathString)
                            && !HasAddDescendant(c.PathString))
                .ToList();

            try
            {
                var adapter = new MapleLibAdapter(version);
                WzImage img = adapter.LoadImg(imgPath);

                int match = 0, miss = 0;
                foreach (var c in expected)
                {
                    var node = adapter.GetByPath(img, c.Path);
                    if (node == null)
                    {
                        miss++;
                        Console.Error.WriteLine($"[miss] {c.PathString} — node not found");
                        continue;
                    }
                    bool ok = Matches(node, c);
                    if (ok) { match++; if (verbose) Console.Out.WriteLine($"[ok ] {c.PathString}"); }
                    else
                    {
                        miss++;
                        Console.Error.WriteLine($"[miss] {c.PathString} — want={Short(c.Value)} got={Short(NodeValue(node))}");
                    }
                }

                // Verify DELETE: each deleted path must be absent from the patched img.
                int delOk = 0, delMiss = 0;
                foreach (var c in deletes)
                {
                    var node = adapter.GetByPath(img, c.Path);
                    if (node == null)
                    {
                        delOk++;
                        if (verbose) Console.Out.WriteLine($"[ok ] DELETE {c.PathString}");
                    }
                    else
                    {
                        delMiss++;
                        Console.Error.WriteLine($"[miss] DELETE {c.PathString} — still present, value={Short(NodeValue(node))}");
                    }
                }

                Console.Out.WriteLine(
                    $"verify: {expected.Count} expected, {match} match, {miss} miss; " +
                    $"deletes: {deletes.Count} expected absent, {delOk} ok, {delMiss} still present");
                return (miss == 0 && delMiss == 0) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"verify failed: {ex.Message}");
                if (verbose) Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void FlattenSubTree(List<string> parentPath, Model.SubTree node, int sourceLine, List<Model.Change> outList)
        {
            var path = new List<string>(parentPath) { node.Name };
            if (node.Type == Model.ValueType.Sub)
            {
                // Always emit the container presence check, then recurse children.
                // SubTree node itself is the container with children.
                outList.Add(new Model.Change(path, Model.ChangeOp.Add, Model.ValueType.Sub, null, sourceLine, node, node.VectorX, node.VectorY));
                foreach (var child in node.Children)
                    FlattenSubTree(path, child, sourceLine, outList);
            }
            else
            {
                outList.Add(new Model.Change(path, Model.ChangeOp.Add, node.Type, node.Value, sourceLine, null, node.VectorX, node.VectorY));
            }
        }

        private static bool Matches(WzImageProperty node, Model.Change c)
        {
            switch (c.ValueType)
            {
                case Model.ValueType.String: return node is WzStringProperty s && (s.Value ?? "") == (c.Value ?? "");
                case Model.ValueType.Int: return node is WzIntProperty i && i.Value == ParseInt(c.Value);
                case Model.ValueType.Short: return node is WzShortProperty sh && sh.Value == (short)ParseInt(c.Value);
                case Model.ValueType.Long: return node is WzLongProperty l && l.Value == ParseLong(c.Value);
                case Model.ValueType.Float: return node is WzFloatProperty f && Math.Abs(f.Value - ParseFloat(c.Value)) < 1e-6f;
                case Model.ValueType.Double: return node is WzDoubleProperty d && Math.Abs(d.Value - ParseDouble(c.Value)) < 1e-12;
                case Model.ValueType.Vector: return node is WzVectorProperty v && v.X.Value == c.VectorX && v.Y.Value == c.VectorY;
                case Model.ValueType.Null: return node is WzNullProperty;
                case Model.ValueType.Sub: return node is WzSubProperty; // presence check
                default: return false;
            }
        }

        private static string NodeValue(WzImageProperty p) => p switch
        {
            WzStringProperty s => s.Value ?? "",
            WzIntProperty i => i.Value.ToString(),
            WzShortProperty sh => sh.Value.ToString(),
            WzLongProperty l => l.Value.ToString(),
            WzFloatProperty f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            WzDoubleProperty d => d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            WzVectorProperty v => $"({v.X.Value},{v.Y.Value})",
            WzNullProperty => "<null>",
            WzSubProperty => "<imgdir>",
            _ => p.WzValue?.ToString() ?? "",
        };

        private static string Short(string? s) => s == null ? "<null>" : (s.Length > 50 ? s.Substring(0, 50) + "…" : s).Replace("\n", "\\n");

        private static int ParseInt(string? s) => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;
        private static long ParseLong(string? s) => long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : 0L;
        private static float ParseFloat(string? s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
        private static double ParseDouble(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0d;

        // Given a full-xml directory and a diff path, find the matching .xml by filename.
        // Recurses the directory so subdirectory structure mismatches (e.g. server "Map.wz/Map/Map2/"
        // vs a flatter layout) don't matter — only the leaf filename needs to match.
        private static string? FindMirroredXml(string dir, string diffPath, string xmlName)
        {
            // xmlName is e.g. "209000000.img.xml". The full-xml file may sit anywhere under `dir`
            // with that exact name.
            var hits = Directory.GetFiles(dir, xmlName, SearchOption.AllDirectories);
            if (hits.Length == 0) return null;
            if (hits.Length == 1) return hits[0];
            // Prefer the one whose relative path best matches the diff's relative path tail.
            string diffTail = diffPath.Replace('/', '\\');
            string best = hits[0];
            int bestScore = -1;
            foreach (var h in hits)
            {
                string rel = Path.GetRelativePath(dir, h);
                int score = CommonSuffixSegments(rel, diffTail);
                if (score > bestScore) { bestScore = score; best = h; }
            }
            return best;
        }

        private static int CommonSuffixSegments(string a, string b)
        {
            var aa = a.Replace('/', '\\').Split('\\');
            var bb = b.Replace('/', '\\').Split('\\');
            int i = aa.Length - 1, j = bb.Length - 1, n = 0;
            while (i >= 0 && j >= 0 && string.Equals(aa[i], bb[j], StringComparison.OrdinalIgnoreCase))
            { i--; j--; n++; }
            return n;
        }

        // ---------- batch-dump-xml ----------
        private static int RunBatchDumpXml(List<string> positional, WzMapleVersion version, bool verbose, bool linuxLineBreak, int indent)
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
                int rc = DumpOne(img, outXml, version, verbose, linuxLineBreak, indent);
                if (rc == 0) ok++; else fail++;
            }
            Console.Out.WriteLine();
            Console.Out.WriteLine($"batch-dump-xml: {ok} ok, {fail} fail");
            return fail == 0 ? 0 : 1;
        }

        // ---------- sync ----------
        // sync 把"服务端仓库同步到客户端 .img"收进内核，一行命令完成，替代 wz-sync.py。
        // 直接节点级三方对比（old/new/client）产出 Change 列表喂给 ImgPatcher，不生成文本 diff。
        //
        // 三种"new"来源：
        //   --server=<目录>          服务端 XML 目录（完全不需要 git，两方全量）
        //   --repo + --ref=<ref>     git 某 ref（两方全量，git show <ref>:<path>）
        //   --repo + --from=<ref|dt> git 增量（三方，git diff <from>..HEAD）
        // 通用：
        //   --client=<客户端根> --out=<输出根> [--prefix=...] [--iv] [--in-place] [--dry-run]
        //   [--strict] [--mode=review|trust]
        private static int RunSync(
            List<string> positional,
            string? serverDir,
            string? repo,
            string? refArg,
            string? fromArg,
            string? clientRoot,
            string? outDir,
            List<string> prefixes,
            WzMapleVersion version,
            bool inPlace,
            bool strict,
            string mode,
            string? reviewOut,
            bool dryRun,
            bool verbose)
        {
            if (positional.Count > 0)
            {
                Console.Error.WriteLine($"sync 不接受位置参数: {string.Join(" ", positional)}");
                return 2;
            }
            if (string.IsNullOrEmpty(clientRoot) || !Directory.Exists(clientRoot))
            {
                Console.Error.WriteLine($"--client 目录不存在: {clientRoot}");
                return 2;
            }
            if (!inPlace && string.IsNullOrEmpty(outDir))
            {
                Console.Error.WriteLine("需要 --out=<目录> 或 --in-place");
                return 2;
            }
            if (serverDir != null && repo != null)
            {
                Console.Error.WriteLine("--server 和 --repo 只能二选一");
                return 2;
            }
            if (repo != null && string.IsNullOrEmpty(refArg) && string.IsNullOrEmpty(fromArg))
            {
                Console.Error.WriteLine("--repo 模式下需要 --ref 或 --from");
                return 2;
            }
            if (!string.IsNullOrEmpty(refArg) && !string.IsNullOrEmpty(fromArg))
            {
                Console.Error.WriteLine("--ref 和 --from 只能二选一（ref=全量，from=增量）");
                return 2;
            }
            bool trustMode = mode.Equals("trust", StringComparison.OrdinalIgnoreCase);
            if (!trustMode && !mode.Equals("review", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"未知 --mode: {mode}（可用值: review / trust）");
                return 2;
            }
            if (serverDir != null && !Directory.Exists(serverDir))
            {
                Console.Error.WriteLine($"--server 目录不存在: {serverDir}");
                return 2;
            }

            // Determine effective prefixes (layer → client mapping).
            var effPrefixes = prefixes.Count > 0
                ? prefixes
                : new List<string> { "gms-server/wz", "gms-server/wz-zh-CN" };

            // Locate git repo root if in git mode.
            string? repoRoot = null;
            string? fromCommit = null;
            if (repo != null)
            {
                repoRoot = FindRepoRoot(Path.GetFullPath(repo));
                if (!Directory.Exists(Path.Combine(repoRoot, ".git")))
                {
                    Console.Error.WriteLine($"不是 git 仓库: {repoRoot}");
                    return 2;
                }
                if (!string.IsNullOrEmpty(fromArg))
                {
                    try { fromCommit = ResolveFromCommit(fromArg, repoRoot); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"解析 --from 失败: {ex.Message}");
                        return 2;
                    }
                }
            }

            string effOut = inPlace ? clientRoot : Path.GetFullPath(outDir!);

            Console.Out.WriteLine("==========================================");
            Console.Out.WriteLine("  服务端 → 客户端 同步");
            Console.Out.WriteLine($"  source:  {(serverDir != null ? "--server=" + serverDir
                        : (refArg != null ? $"--repo={repo} --ref={refArg}" : $"--repo={repo} --from={fromArg}"))}");
            Console.Out.WriteLine($"  client:  {clientRoot}");
            Console.Out.WriteLine($"  out:     {(inPlace ? "(in-place)" : effOut)}");
            Console.Out.WriteLine($"  mode:    {(trustMode ? "trust" : "review")}{(strict ? ", strict" : "")}");
            Console.Out.WriteLine("==========================================");

            // Gather the file list (mode-specific).
            var files = new List<(string status, string serverPath)>();
            if (serverDir != null)
            {
                foreach (string f in Directory.GetFiles(serverDir, "*.xml", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(serverDir, f);
                    files.Add(("M", rel.Replace('\\', '/')));
                }
            }
            else if (!string.IsNullOrEmpty(refArg))
            {
                foreach (string p in effPrefixes)
                    foreach (string f in GitListFilesAtRef(repoRoot!, refArg, p))
                        files.Add(("M", f));
            }
            else
            {
                foreach (string p in effPrefixes)
                    foreach (string f in GitListFiles(fromCommit!, p, "ACMR", repoRoot!))
                        files.Add(("M", f));
            }

            // Dedup by client rel path; zh layer wins over en (wz-zh-CN → Data, wz → EN/Data).
            var byRel = new Dictionary<string, (int prio, string status, string path)>();
            foreach (var (status, path) in files)
            {
                if (MapServerRelToClient(path) is not (var rest, var layer)) continue;
                if (rest == null || layer == null) continue;
                string clientRel = ClientRelFor(layer, rest, clientRoot);
                if (clientRel == null) continue;
                int prio = layer == "wz-zh-CN" ? 0 : 1;
                if (!byRel.TryGetValue(clientRel, out var cur) || prio < cur.prio)
                    byRel[clientRel] = (prio, status, path);
            }
            var ordered = byRel.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();

            Console.Out.WriteLine($"共 {ordered.Count} 个文件待处理");
            int okCount = 0, failCount = 0, noChange = 0, reviewItems = 0;
            var failList = new List<string>();
            var reviewList = new List<string>();

            var adapter = new MapleLibAdapter(version);
            int i = 0;
            foreach (var (clientRel, (_, status, serverPath)) in ordered)
            {
                i++;
                Console.Out.WriteLine($"[{i}/{ordered.Count}] {Path.GetFileName(serverPath)} ({status}) -> {clientRel}");
                try
                {
                    var r = ProcessSyncFile(
                        serverPath, serverDir, repoRoot, refArg, fromCommit,
                        clientRoot, effOut, version, inPlace, strict, trustMode,
                        dryRun, verbose, adapter, clientRel);
                    if (r.NoChange) noChange++;
                    else if (r.Ok) { okCount++; reviewItems += r.ReviewCount; reviewList.AddRange(r.ReviewItems); }
                    else { failCount++; failList.Add($"{serverPath} ({r.Error})"); }
                }
                catch (Exception ex)
                {
                    failCount++;
                    failList.Add($"{serverPath} ({ex.Message})");
                    if (verbose) Console.Error.WriteLine(ex);
                }
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine("================ SYNC SUMMARY ================");
            Console.Out.WriteLine($"ok:       {okCount}");
            Console.Out.WriteLine($"nochange: {noChange}");
            Console.Out.WriteLine($"fail:     {failCount}");
            Console.Out.WriteLine($"review:   {reviewItems}");
            if (reviewList.Count > 0)
            {
                Console.Out.WriteLine("---- 人工复核清单 ----");
                foreach (var s in reviewList) Console.Out.WriteLine("  " + s);
            }
            foreach (var f in failList) Console.Out.WriteLine($"  [fail] {f}");

            // --review-out=<file>: 把人工复核清单写到文件（每行一条），与 Java 版对齐。
            if (!string.IsNullOrEmpty(reviewOut) && reviewList.Count > 0)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(Path.GetFullPath(reviewOut));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllLines(reviewOut, reviewList);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[err] 写入 review 清单失败: {reviewOut} — {ex.Message}");
                }
            }
            return failCount == 0 ? 0 : 1;
        }

        // Per-file sync: load old/new/client, three-way merge → changes, patch, verify inline.
        private static SyncFileResult ProcessSyncFile(
            string serverPath,          // rel under serverDir (mode A) OR full "gms-server/..." path (git modes)
            string? serverDir,          // mode A root (null in git modes)
            string? repoRoot,           // git modes root (null in mode A)
            string? refArg,             // git new ref (for --ref full-match)
            string? fromCommit,         // git old ref (for --from three-way); null → two-way
            string clientRoot,
            string outRoot,
            WzMapleVersion version,
            bool inPlace,
            bool strict,
            bool trustMode,
            bool dryRun,
            bool verbose,
            MapleLibAdapter adapter,
            string clientRel)
        {
            // 1. Fetch old / new text.
            string? oldText = null;
            string? newText = null;
            if (serverDir != null)
            {
                newText = File.ReadAllText(Path.Combine(serverDir, serverPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            else
            {
                string newRef = !string.IsNullOrEmpty(refArg) ? refArg! : "HEAD";
                newText = GitShow(repoRoot!, newRef, serverPath);
                if (fromCommit != null)
                    oldText = GitShow(repoRoot!, fromCommit, serverPath);
            }
            if (newText == null)
            {
                return new SyncFileResult(false, false, 0, 0, new List<string>(), "git 无此文件");
            }

            // 2. Map to client img path.
            if (MapServerRelToClient(serverPath) is not (var rest, var layer)) return new SyncFileResult(false, false, 0, 0, new List<string>(), "路径解析失败");
            string clientImg = Path.Combine(clientRoot, clientRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(clientImg))
            {
                return new SyncFileResult(false, false, 0, 0, new List<string>(), "客户端 img 不存在");
            }

            // 3. Parse trees.
            Node oldNode = oldText == null ? null! : XmlNodeParser.Parse(oldText);
            Node newNode = XmlNodeParser.Parse(newText);
            WzImage img = adapter.LoadImg(clientImg);
            Node clientNode = ImgNodeReader.Read(img);
            img.Dispose(); // free the image after reading the Node tree

            // 4. Three-way merge → changes.
            var merge = ThreeWayMerge.Merge(oldNode, newNode, clientNode, trustMode, strict);
            var changes = merge.Changes;
            var reviewItems = new List<string>();
            // Change-backed review entries (third-default / type-conflict).
            if (merge.ReviewCount > 0)
            {
                foreach (var c in changes)
                {
                    if (c.Action is ChangeAction.ModifyThirdDefault or ChangeAction.TypeConflict)
                        reviewItems.Add(FormatReviewItem(c, clientNode));
                }
            }
            // Non-Change review entries (missing-unmodified etc.).
            reviewItems.AddRange(merge.ReviewItems);

            if (changes.Count == 0)
            {
                // No change → copy img to out (unless in-place no-op).
                if (!dryRun && !inPlace)
                {
                    string outImg = Path.Combine(outRoot, clientRel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(outImg)!);
                    File.Copy(clientImg, outImg, overwrite: true);
                }
                return new SyncFileResult(true, true, 0, 0, reviewItems, null);
            }

            // 5. Patch.
            string outputImg = Path.Combine(outRoot, clientRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outputImg)!);
            var patcher = new ImgPatcher(adapter, verbose, strict, dryRun, Console.Out, Console.Error);
            var result = patcher.Patch(clientImg, changes, outputImg);
            if (result.Failed > 0)
            {
                return new SyncFileResult(false, false, result.Applied, merge.ReviewCount, reviewItems, $"{result.Failed} 条失败");
            }

            // 6. Verify inline (unless dry-run — nothing written).
            int miss = 0;
            if (!dryRun)
            {
                WzImage outImg = adapter.LoadImg(outputImg);
                miss = VerifyChangeList(adapter, outImg, changes, verbose, Console.Error);
                outImg.Dispose();
            }

            return new SyncFileResult(result.Failed == 0, false, result.Applied, merge.ReviewCount, reviewItems, miss > 0 ? $"{miss} miss" : null);
        }

        private sealed class SyncFileResult
        {
            public bool Ok;
            public bool NoChange;
            public int Applied;
            public int ReviewCount;
            public List<string> ReviewItems;
            public string? Error;
            public SyncFileResult(bool ok, bool noChange, int applied, int review, List<string> reviewItems, string? error)
            { Ok = ok; NoChange = noChange; Applied = applied; ReviewCount = review; ReviewItems = reviewItems; Error = error; }
        }

        // Map a server-side path (rel under --server, or full "gms-server/<layer>/...") to
        // (rest-path-without-layer, layer). Returns null if unparseable.
        private static (string? rest, string? layer)? MapServerRelToClient(string serverPath)
        {
            string p = serverPath.Replace('\\', '/');
            if (p.StartsWith("gms-server/", StringComparison.Ordinal))
                p = p.Substring("gms-server/".Length);
            int slash = p.IndexOf('/');
            if (slash < 0) return null;
            string layer = p.Substring(0, slash);
            string rest = p.Substring(slash + 1);
            if (rest.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                rest = rest.Substring(0, rest.Length - ".xml".Length);
            // strip ".wz" from each directory segment (Quest.wz → Quest)
            var segs = rest.Split('/');
            for (int j = 0; j < segs.Length; j++)
                if (segs[j].EndsWith(".wz", StringComparison.OrdinalIgnoreCase))
                    segs[j] = segs[j].Substring(0, segs[j].Length - 3);
            return (string.Join('/', segs), layer);
        }

        // layer → client root-relative path: wz-zh-CN → Data/, wz → EN/ (fallback Data/ if EN missing).
        private static string? ClientRelFor(string layer, string rest, string clientRoot)
        {
            if (layer == "wz-zh-CN") return "Data/" + rest;
            if (layer == "wz")
            {
                string en = "EN/" + rest;
                string enAbs = Path.Combine(clientRoot, en.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(enAbs) ? en : "Data/" + rest;
            }
            return null;
        }

        private static string FormatReviewItem(Model.Change c, Node? clientNode)
        {
            string cv = ClientLeafValue(clientNode, c) ?? "<null>";
            string nv = c.Value ?? "<null>";
            return $"{c.Action} {c.PathString}  (client={Short(cv)}, new={Short(nv)})";
        }

        // Resolve a leaf value from the client Node tree by path + sibling ordinal (used for
        // review lines). Honors Change.SiblingIndices so a duplicated name (e.g. the two "2277"
        // quest blocks) resolves to the correct instance instead of always the first.
        private static string? ClientLeafValue(Node? root, Model.Change c)
        {
            if (root == null || c.Path.Count == 0) return null;
            Node cur = root;
            for (int i = 0; i < c.Path.Count; i++)
            {
                string seg = c.Path[i];
                int ordinal = c.SiblingIndexAt(i);
                Node? next = null;
                int seen = 0;
                foreach (var ch in cur.Children)
                {
                    if (ch.Name == seg)
                    {
                        if (seen == ordinal) { next = ch; break; }
                        seen++;
                    }
                }
                if (next == null) return null;
                cur = next;
            }
            return cur.Value;
        }

        // git show <ref>:<path> → file content, or null if missing at that ref.
        private static string? GitShow(string repoRoot, string refName, string path)
        {
            var (rc, stdout, _) = RunGit(repoRoot, "-c", "core.quotePath=false", "show", $"{refName}:{path}");
            return rc == 0 ? stdout : null;
        }

        // git ls-tree -r --name-only <ref> -- <prefix> → file list.
        private static List<string> GitListFilesAtRef(string repoRoot, string refName, string prefix)
        {
            var files = new List<string>();
            var (rc, stdout, _) = RunGit(repoRoot, "ls-tree", "-r", "--name-only", refName, "--", prefix);
            if (rc != 0) return files;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using var reader = new StringReader(stdout);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length > 0 && seen.Add(line)) files.Add(line);
            }
            return files;
        }

        // Verify a Change list against a patched img. Flattens ADD SubTrees into leaf
        // expectations. Simpler than RunVerify (which must tolerate messy text-diff shapes);
        // sync changes come from a clean three-way merge so no false-rename filtering needed.
        private static int VerifyChangeList(MapleLibAdapter adapter, WzImage img, List<Model.Change> changes, bool verbose, TextWriter err)
        {
            var expected = new List<Model.Change>();
            foreach (var c in changes)
            {
                if (c.Op == Model.ChangeOp.Add && c.ValueType == Model.ValueType.Sub && c.SubTree != null)
                {
                    var parent = c.Path.Count > 0
                        ? new List<string>(c.Path.Take(c.Path.Count - 1))
                        : new List<string>();
                    FlattenSubTree(parent, c.SubTree, 0, expected);
                }
                else
                {
                    expected.Add(c);
                }
            }
            int match = 0, miss = 0;
            foreach (var c in expected)
            {
                if (c.Op == Model.ChangeOp.Delete)
                {
                    if (adapter.GetByPath(img, c.Path, c.SiblingIndices) == null) match++;
                    else { miss++; err.WriteLine($"[miss] DELETE {c.PathString} — still present"); }
                    continue;
                }
                var node = adapter.GetByPath(img, c.Path, c.SiblingIndices);
                if (node == null)
                {
                    miss++;
                    err.WriteLine($"[miss] {c.PathString} — node not found");
                    continue;
                }
                if (Matches(node, c)) { match++; if (verbose) Console.Out.WriteLine($"[ok ] {c.PathString}"); }
                else
                {
                    miss++;
                    err.WriteLine($"[miss] {c.PathString} — want={Short(c.Value)} got={Short(NodeValue(node))}");
                }
            }
            if (verbose) Console.Out.WriteLine($"verify-inline: {expected.Count} expected, {match} match, {miss} miss");
            return miss;
        }

        // ---------- help ----------
        private static bool TryParseIv(string s, out WzMapleVersion version)
        {
            // Accept GMS / EMS / BMS / CLASSIC (MapleLib's canonical names), plus
            // a couple of common aliases. Case-insensitive.
            switch ((s ?? "").Trim().ToUpperInvariant())
            {
                case "GMS":     version = WzMapleVersion.GMS; return true;
                case "EMS":     version = WzMapleVersion.EMS; return true;
                case "BMS":
                case "CMS":     // CMS is conventionally encoded with the BMS IV
                    version = WzMapleVersion.BMS; return true;
                case "CLASSIC":
                case "LATEST":  // Java side calls it "latest"; map to CLASSIC for parity
                    version = WzMapleVersion.CLASSIC; return true;
                case "GENERATE": version = WzMapleVersion.GENERATE; return true;
                default:
                    version = WzMapleVersion.GMS;
                    return false;
            }
        }

        private static void PrintHelp(TextWriter w)
        {
            w.WriteLine("xml-img-patcher  -  把服务端 *.img.xml 的 git diff 应用到客户端 *.img");
            w.WriteLine();
            w.WriteLine("用法：");
            w.WriteLine("  xml-img-patcher patch          <input.img> <diff> <output.img> [选项]");
            w.WriteLine("  xml-img-patcher dump-xml       <input.img> <output.xml>        [选项]");
            w.WriteLine("  xml-img-patcher batch          <img目录> <diff目录> <输出目录> [选项]");
            w.WriteLine("  xml-img-patcher batch-dump-xml <img目录> <xml输出目录>         [选项]");
            w.WriteLine("  xml-img-patcher verify         <patched.img> <diff> [full-xml或目录] [选项]");
            w.WriteLine("  xml-img-patcher export         --from=<commit|datetime> [选项]");
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
            w.WriteLine("  verify          校验：直接加载 patch 后的 .img，把 diff 里每条 + 变更");
            w.WriteLine("                  和 img 节点的真实值逐条比对。绕过 dump-xml，所以测的是");
            w.WriteLine("                  img 内部内容本身，不受 XML 序列化影响。");
            w.WriteLine("                  第 3 个参数可给单个完整 XML 文件或目录（自动配对）。");
            w.WriteLine("  export          从 git 仓库导出补丁数据：把指定起点之后变更的 wz xml 抽");
            w.WriteLine("                  出来，并生成 git diff。--from 接受 commit hash 或 ref，");
            w.WriteLine("                  也接受 datetime（如 2026-06-22），datetime 会找该时间点");
            w.WriteLine("                  之前最近的一个 commit 作为起点。");
            w.WriteLine();
            w.WriteLine("通用选项：");
            w.WriteLine("  -h, --help             显示这个帮助。");
            w.WriteLine("  -V, --version          打印版本号并退出。");
            w.WriteLine("  -v, --verbose          失败时打印完整堆栈，方便排查。");
            w.WriteLine("      --iv=<KEY>         WZ 加密 IV，大小写不敏感。默认 gms。");
            w.WriteLine("                         可用：gms / ems / bms / cms / classic / latest");
            w.WriteLine("                         （--version=<KEY> 是已弃用的别名，将来会移除）");
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
            w.WriteLine("      --linux             dump-xml / batch-dump-xml 输出用 LF 行尾（默认 CRLF）");
            w.WriteLine("      --indent=<N>        dump-xml / batch-dump-xml 缩进空格数，默认 4（与 Java 版一致）");
            w.WriteLine();
            w.WriteLine("export 专用选项：");
            w.WriteLine("      --from=<起点>      必填。git commit hash / ref（如 27529d68 / HEAD~3），");
            w.WriteLine("                         或 datetime（如 2026-06-22 / 2026-06-22T15:30:00）。");
            w.WriteLine("                         datetime 会找该时间点之前最近的一个 commit 作起点。");
            w.WriteLine("      --repo=<目录>      git 仓库根目录（默认当前目录向上找 .git）");
            w.WriteLine("      --out-xml=<目录>   xml 输出根目录（默认 ~/Desktop/upgrade_yyyyMMdd）");
            w.WriteLine("      --out-diff=<目录>  diff 输出根目录（默认 ~/Desktop/diff_yyyyMMdd）");
            w.WriteLine("      --prefix=<前缀>    需要扫描的目录前缀（相对仓库根），可重复多次。");
            w.WriteLine("                         默认：--prefix=gms-server/wz --prefix=gms-server/wz-zh-CN");
            w.WriteLine("      --no-diff          只复制 xml，不生成 diff");
            w.WriteLine("      --context=<N>      git diff 上下文行数（-U），默认 30");
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
            w.WriteLine();
            w.WriteLine("  # 从 git commit 之后导出 wz xml + diff（默认输出到桌面）");
            w.WriteLine("  xml-img-patcher export --from=27529d68 --repo=\"E:\\LocalGit\\GitHub\\BeiDou-Server\"");
            w.WriteLine();
            w.WriteLine("  # 用日期作为起点（找该日之前最近的一个 commit）");
            w.WriteLine("  xml-img-patcher export --from=2026-06-22 --repo=\"E:\\LocalGit\\GitHub\\BeiDou-Server\"");
        }
    }
}
