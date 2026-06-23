# xml-img-patcher

把服务端 XML 的 git unified diff 应用到客户端 `.img` 文件，**保留所有未触及的 PNG / Sound / Canvas / UOL / Vector 等二进制资源**。

适用场景：服务端用瘦 XML（只含业务节点）维护文本/数值变更，客户端的 `.img` 里有完整图标/音效/UI 资源；要把服务端的改动同步回客户端，又不能丢资源。

## 为什么需要这个工具

直接的"反向重生成 .img"流程会把客户端那些只存在于 .img、不存在于服务端 XML 的资源（PNG 图标、音效、UOL 引用、Vector 几何…）全部丢掉——之前出过 `0403.img: 1.3 MB → 70 KB`、`Skill/000.img: 3.1 MB → 4.9 KB` 这种事故。

本工具走另一条路：**直接打开 .img、按 diff 改节点、原样写回**，不重建文件。Diff 没碰到的节点一字节不动。

## 用法

### 子命令

```
xml-img-patcher patch          <input.img> <diff> <output.img> [选项]
xml-img-patcher dump-xml       <input.img> <output.xml>        [选项]
xml-img-patcher batch          <img目录> <diff目录> <输出目录> [选项]
xml-img-patcher batch-dump-xml <img目录> <xml输出目录>         [选项]
xml-img-patcher verify         <patched.img> <diff> [full-xml或目录] [选项]
xml-img-patcher export         --from=<hash或datetime>        [选项]
xml-img-patcher dump-changes   <diff>       [full-xml或目录]  [选项]
```

| 子命令 | 作用 |
|---|---|
| `patch` | 对一个 .img 应用一个 .diff，输出新 .img。保留 PNG/Sound/UOL 等所有 diff 没碰过的二进制资源 |
| `dump-xml` | 把 .img 转成服务端格式的 .xml，方便肉眼看或对比 |
| `batch` | 批量版的 patch。按文件名自动配对：diff 目录下 `a/b/Foo.img.xml.diff` → 找 img 目录里的 `a/b/Foo.img` → 写到输出目录 `a/b/Foo.img`。diff 目录可多层嵌套，工具会递归扫所有 `*.diff`。没找到对应 img 的 diff 会跳过并在最后 BATCH SUMMARY 汇总 |
| `batch-dump-xml` | 批量版的 dump-xml。递归把目录下所有 .img 都转成 .xml |
| `verify` | 校验：直接加载 patch 后的 .img，把 diff 里每条 + 变更（Add/Modify）查节点比对值；DELETE 查节点是否已消失。绕过 dump-xml 序列化，测的就是 img 的实际内容 |
| `export` | 从 git 仓库导出指定起点之后的 wz xml 与 diff。`--from` 同时支持 commit hash 和 datetime |
| `dump-changes` | 调试用：打印 DiffParser 解析 diff 后得到的所有 Change（op / path / value / 源行号），不写文件 |

### patch 选项

| 选项 | 说明 |
|---|---|
| `-v, --verbose` | 失败时打印完整堆栈 |
| `--dry-run` | 解析 diff、加载 img、模拟 patch，**不写文件** |
| `--strict` | 任何一条 change 失败立即中止；默认是尽力做完，最后汇总 |
| `--iv <GMS\|EMS\|BMS\|CLASSIC>` | WZ 加密 IV，默认 `GMS`（大小写不敏感） |
| `--full-xml <file>` | 完整服务端 XML（diff `+++` 那一侧的最终文件）。当 hunk 上下文不带外层 imgdir 时，用它从 hunk 头的行号反查路径栈，避免歧义/找不到节点。**强烈推荐配** |
| `--full-xml-dir <dir>` | 完整服务端 XML 根目录，会按 diff 路径自动配对。批量处理时用这个，比 `--full-xml` 省事 |

### batch 选项

| 选项 | 说明 |
|---|---|
| `--full-xml-dir <dir>` | 完整服务端 XML 根目录（按 diff 路径自动配对） |
| `--dry-run` / `--strict` / `--iv` / `-v` | 同 patch |

### dump-xml / batch-dump-xml 选项

| 选项 | 说明 |
|---|---|
| `--iv <GMS\|EMS\|BMS\|CLASSIC>` | 同上 |
| `--linux` | 用 LF 行尾（默认 CRLF） |

默认会**跳过 PNG / Sound 等二进制资源**（只输出节点骨架），便于纯文本对比。

### verify 选项

| 选项 | 说明 |
|---|---|
| `<full-xml 或目录>` | 第 3 个位置参数。完整服务端 XML 文件，或与之同布局的目录（工具会按 diff 文件名配对查找）。用来恢复 hunk 路径栈 |
| `--iv <GMS\|EMS\|BMS\|CLASSIC>` | 同上 |
| `-v` | 打印每条 ok / miss |

### export 选项

| 选项 | 说明 |
|---|---|
| `--from <hash或datetime>` | 起点（必填）。commit hash 或 datetime 两种形态 |
| `--repo <dir>` | git 仓库根目录（默认当前目录） |
| `--out-xml <dir>` | xml 输出根（默认 ~/Desktop/upgrade_yyyyMMdd） |
| `--out-diff <dir>` | diff 输出根（默认 ~/Desktop/diff_yyyyMMdd） |
| `--prefix <pref>` | 扫描目录前缀（可多个，默认 gms-server/wz、gms-server/wz-zh-CN） |
| `--no-diff` | 只复制 xml，不生成 diff |
| `--context <N>` | git diff 上下文行数 -U（默认 30） |

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 全部成功 |
| 1 | 部分 change 失败但已写出（非严格模式） |
| 2 | 参数错误或文件不存在 |
| 3 | diff 解析失败 |
| 4 | img 解析失败 |
| 5 | img 写入失败 |

## 输出格式

可被 AI / shell 脚本解析，关键字 `MODIFY` / `ADD` / `DELETE` / `[ok]` / `[err]` 永远是英文：

```
[parse] 12 changes from diff
[ok]  MODIFY  Mob.img/9999999/name = "已杀怪物数"
[ok]  ADD     0403.img/04031786 (subtree, 2 nodes)
[err] MODIFY  Foo/Bar — node not found
3 applied, 1 failed. Output: D:\out.img (1,335,712 bytes)
```

`batch` 末尾额外有 `BATCH SUMMARY`，列出 ok/fail/skip 计数和失败/跳过的文件清单。

## 典型用法

### 单文件 patch

```bash
xml-img-patcher patch \
  --full-xml=C:/upgrade_20260622/wz-zh-CN/Quest.wz/QuestInfo.img.xml \
  C:/client/Data/Quest/QuestInfo.img \
  C:/diff_20260622/wz-zh-CN/Quest.wz/QuestInfo.img.xml.diff \
  C:/out/Quest/QuestInfo.img
```

### 先 dry-run 看会不会失败，再实跑

```bash
xml-img-patcher patch --dry-run --full-xml="..." input.img diff output.img   # 不写文件
# 确认 0 failed 后去掉 --dry-run 实跑
```

### 批量 patch 整个 diff 目录

```bash
xml-img-patcher batch \
  --full-xml-dir=C:/upgrade_20260622/wz-zh-CN \
  C:/client/Data \
  C:/diff_20260622/wz-zh-CN \
  C:/out/Data
```

末尾会打印 `BATCH SUMMARY`，汇总 ok / fail / skip 文件数和每个失败/跳过的原因。

### 校验 patched img 是否正确

```bash
xml-img-patcher verify \
  C:/out/Quest/QuestInfo.img \
  C:/diff_20260622/wz-zh-CN/Quest.wz/QuestInfo.img.xml.diff \
  C:/upgrade_20260622/wz-zh-CN
```

输出 `verify: N expected, N match, 0 miss` 即通过。

### 把 .img 导出成 XML 肉眼看

```bash
xml-img-patcher dump-xml "E:/Client/EN/String/Mob.img" "C:/out/Mob.xml" --linux
```

### 从服务端 git 仓库导出 xml + diff

```bash
# 按 commit hash
xml-img-patcher export --from=27529d68 --repo="E:/LocalGit/GitHub/BeiDou-Server"

# 按时间点（找该时刻前最近一次 commit 作为起点）
xml-img-patcher export --from="2026-06-22 14:00" --repo="E:/LocalGit/GitHub/BeiDou-Server"
```

### 一站式工作流：从服务端拉 → 打到客户端 → 验

```bash
# Step 1: 从服务端 git 仓库导出补丁数据
xml-img-patcher export --from=27529d68 \
  --repo="E:/LocalGit/GitHub/BeiDou-Server" \
  --out-xml=C:/upgrade --out-diff=C:/diff

# Step 2a: wz 层（英文层）应用到客户端 EN 目录
xml-img-patcher batch --full-xml-dir=C:/upgrade/wz \
  E:/Client/EN C:/diff/wz C:/out/EN

# Step 2b: wz-zh-CN 层应用到客户端 Data 目录
xml-img-patcher batch --full-xml-dir=C:/upgrade/wz-zh-CN \
  E:/Client/Data C:/diff/wz-zh-CN C:/out/Data

# Step 3: 校验
xml-img-patcher verify C:/out/Data/Quest/Say.img \
  C:/diff/wz-zh-CN/Quest.wz/Say.img.xml.diff \
  C:/upgrade/wz-zh-CN
```

**关键映射规则**（patch/batch 共用）：
- 服务端 `wz/` 层 → 客户端 `EN/`（英文文本）目录（若不存在则回退到 `Data/`）
- 服务端 `wz-zh-CN/` 层 → 客户端 `Data/`（中文汉化）目录
- diff 路径 `String.wz/Mob.img.xml.diff` 自动剥 `.wz` 段 → img 路径 `String/Mob.img`

## 构建

要求 .NET 10.0+：

```bash
dotnet build MapleLib.XmlImgPatcher/MapleLib.XmlImgPatcher.csproj -c Release
```

发布为 self-contained 单文件 exe（csproj 已默认 PublishSingleFile + win-x64）：

```bash
dotnet publish MapleLib.XmlImgPatcher/MapleLib.XmlImgPatcher.csproj -c Release
# 产物在 bin/Release/net10.0-windows/win-x64/publish/xml-img-patcher.exe
```

项目根 `publish.bat` 是封装好的一键构建，产出复制到 `dist/xml-img-patcher.exe`。

## 姊妹仓库

| 实现 | 仓库 | 产物 |
|---|---|---|
| C# | <https://github.com/SleepNap/MapleLib-cli> | `dist/xml-img-patcher.exe`（.NET AOT/publish 单文件） |
| Java | <https://github.com/SleepNap/orange-wz-cli> | `dist/xml-img-patcher.exe`（GraalVM native，standalone） |

两边功能、子命令、选项、退出码、输出格式**完全一致**，脚本可互换。两边 `dump-xml --linux` 输出逐字节一致。