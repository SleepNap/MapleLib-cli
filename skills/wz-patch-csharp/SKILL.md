---
name: wz-patch-csharp
description: 用 C# 版 xml-img-patcher.exe 把服务端 XML 的 git unified diff 应用到客户端 .img 文件，保留未触及的 PNG/Sound/Canvas/UOL/Vector 等二进制资源。当需要从服务端 git 仓库导出 wz xml/diff、给 MapleStory 客户端 .img 打补丁、批量打补丁、把 .img 导出成 XML、或校验 patched .img 是否正确时使用本 skill。子命令、退出码、坑点都在下面。
---

# xml-img-patcher（C# 版）使用指南

下文用 `$EXE` 代指 `xml-img-patcher.exe`（已构建好的 standalone 二进制，路径见 README）。

## 三个输入各是什么

打补丁这件事有三个输入，别搞混：

| 输入 | 角色 | 必需 |
|---|---|---|
| **客户端 `.img`** | 被改的目标，唯一被读写的对象 | 必需 |
| **diff 文件**（git unified diff，`*.img.xml.diff`） | 唯一真理来源：改哪个节点、改成什么值 | 必需 |
| **完整服务端 XML**（`--full-xml` 或 `--full-xml-dir`） | 路径字典：当 hunk 上下文太短推不出节点路径时，用 hunk 头行号反查完整路径 | **强烈推荐总是带上** |

diff 是从服务端瘦 XML 生成的，hunk 上下文经常不带外层 `<imgdir>`，不带 `--full-xml` 会导致深嵌套小改动定位失败（报 `node not found`）。**所以默认就带 `--full-xml-dir`。**

## 子命令

```bash
$EXE patch          <input.img>  <diff>       <output.img>   [选项]
$EXE dump-xml       <input.img>  <output.xml>                [选项]
$EXE batch          <img目录>    <diff目录>   <输出目录>      [选项]
$EXE batch-dump-xml <img目录>    <xml输出目录>                [选项]
$EXE verify         <patched.img> <diff>      [full-xml或目录][选项]
$EXE export         --from=<hash或datetime>                  [选项]
```

| 子命令 | 干啥 |
|---|---|
| `patch` | 单文件打补丁 |
| `dump-xml` | 把 .img 转成服务端格式 .xml（默认跳过 PNG/Sound，只出节点骨架） |
| `batch` | 批量 patch，自动按路径配对 diff↔img |
| `batch-dump-xml` | 批量 dump-xml |
| `verify` | 加载 patched .img，把 diff 里每条 `+` 变更跟运行时节点值逐字段比对。**最权威的校验** |
| `export` | 从 git 仓库导出服务端补丁 xml + diff，等价于 ExportPatch.java |

### export 子命令详解

```bash
$EXE export \
  --from=<commit-hash 或 datetime> \
  --repo=<git仓库根>               \
  --out-xml=<输出目录>              \  # 默认 ~/Desktop/upgrade_yyyyMMdd
  --out-diff=<输出目录>             \  # 默认 ~/Desktop/diff_yyyyMMdd
  [--prefix=gms-server/wz,gms-server/wz-zh-CN]  # 默认这俩
  [--no-diff]                       \  # 只复制 xml 不生成 diff
  [--context=30]                    \  # git diff 上下文行数
  [-v]
```

**`--from` 同时支持两种形态**：

1. **git commit hash** — `27529d68`、`HEAD~3`、`main`、`v1.0`…… 凡 `git rev-parse` 认识的 ref 都行
2. **datetime** — 自动找该时间点之前的最近一个 commit，然后导出它到 HEAD 的变更：
   - `2026-06-22`（当日 00:00 之前）
   - `2026-06-22 15:30`、`2026-06-22T15:30:00` 等

输出：两套镜像目录，`wz/` 和 `wz-zh-CN/`。xml 目录结构和服务端一致，diff 目录是每个 xml 加 `.diff`。

**`export` + `batch` 是完整工作流的标准组合**：先用 `export` 从服务端仓库拉补丁，再用 `batch` 打到客户端。

## 选项

| 选项 | 适用 | 含义 |
|---|---|---|
| `-h, --help` | 全部 | 帮助 |
| `-V, --version` | 全部 | 打印版本号 |
| `--iv=<KEY>` | 全部 | WZ 加密 IV，大小写不敏感。可用 `gms / ems / bms / cms / classic / latest`，默认 `gms` |
| `-v, --verbose` | 全部 | 失败时打印完整堆栈 |
| `--dry-run` | patch, batch | 只解析+模拟，不写文件 |
| `--strict` | patch, batch | 任一变更失败立即中止（默认尽力跑完后汇总） |
| `--full-xml=<文件>` | patch | 单个完整服务端 XML |
| `--full-xml-dir=<目录>` | patch, batch | 完整服务端 XML 根目录，按目录结构自动配对 |
| `--linux` | dump-xml, batch-dump-xml | 输出用 LF 行尾（默认 CRLF） |
| `--from=<hash或datetime>` | export | 起点（必填） |
| `--repo=<dir>` | export | git 仓库根（默认当前目录） |
| `--out-xml=<dir>` | export | xml 输出根（默认 ~/Desktop/upgrade_yyyyMMdd） |
| `--out-diff=<dir>` | export | diff 输出根（默认 ~/Desktop/diff_yyyyMMdd） |
| `--prefix=<pref>` | export | 扫描目录前缀（可多个，默认 gms-server/wz,wz-zh-CN） |
| `--no-diff` | export | 只复制 xml，不生成 diff |
| `--context=<N>` | export | git diff 上下文行数 -U（默认 30） |

## 退出码（脚本要靠它判断成败）

| 码 | 含义 |
|---|---|
| 0 | 全部成功 |
| 1 | 部分变更失败但 .img 已写出（非 strict 模式） |
| 2 | 参数错误 / 文件或目录不存在 / 未知命令 |
| 3 | diff 解析失败（文件不是 unified diff、为空、或无 hunk） |
| 4 | img 解析失败（通常是 `--iv` 给错了） |
| 5 | img 写入失败 |

**坑**：在 bash 里用 `$EXE ... | head` 会把 exe 的退出码吞掉，变成 `head` 的退出码。要拿真实退出码就别接管道，或用 `PIPESTATUS`。

## 输出格式（可被脚本解析）

```
[parse] 12 changes from diff
[ok]  MODIFY  Mob.img/9999999/name = "已杀怪物数"
[ok]  ADD     0403.img/04031786 (subtree, 2 nodes)
[err] MODIFY  Foo/Bar — node not found
3 applied, 1 failed. Output: D:\out.img (1,335,712 bytes)
```

`batch` 末尾额外有 `BATCH SUMMARY`，列出 ok/fail/skip 计数和失败/跳过的文件清单。

## 典型用法

### 单文件 patch（带完整 XML 上下文，推荐）

```bash
$EXE patch \
  --full-xml="C:\upgrade\wz\String.wz\Mob.img.xml" \
  "E:\Client\EN\String\Mob.img" \
  "C:\diff\wz\String.wz\Mob.img.xml.diff" \
  "C:\out\Mob.img"
```

### 先 dry-run 看会不会失败，再实跑

```bash
$EXE patch --dry-run --full-xml="..." input.img diff output.img   # 不写文件
# 确认 0 failed 后去掉 --dry-run 实跑
```

### 批量 patch 整个 diff 目录

```bash
$EXE batch \
  --full-xml-dir="C:\upgrade\wz" \
  "E:\Client\EN" \
  "C:\diff\wz" \
  "C:\out\EN"
```

diff 目录结构 `a/b/Foo.img.xml.diff` 会自动配对 `<img目录>/a/b/Foo.img`，自动剥 `.wz` 段（`String.wz/Mob.img.xml.diff` ⇄ `String/Mob.img`）。没找到对应 img 的 diff 会 skip 并在 BATCH SUMMARY 列出。

### 校验 patched img 是否正确

```bash
$EXE verify \
  "C:\out\EN\String\Mob.img" \
  "C:\diff\wz\String.wz\Mob.img.xml.diff" \
  "C:\upgrade\wz"
```

输出 `verify: N expected, N match, 0 miss` 即通过。

### 把 .img 导出成 XML 肉眼看

```bash
$EXE dump-xml "E:\Client\EN\String\Mob.img" "C:\out\Mob.xml" --linux
```

### 从服务端 git 仓库导出 xml + diff

按 commit hash：

```bash
$EXE export \
  --from=27529d68 \
  --repo="E:\LocalGit\GitHub\BeiDou-Server"
# → ~/Desktop/upgrade_20260623/{wz,wz-zh-CN}/   完整 xml
# → ~/Desktop/diff_20260623/{wz,wz-zh-CN}/      对应 .diff
```

按时间点（找该时刻前最近一次 commit 作为起点）：

```bash
$EXE export \
  --from="2026-06-22 14:00" \
  --repo="E:\LocalGit\GitHub\BeiDou-Server"
```

### 一站式工作流：从服务端拉 → 打到客户端 → 验

最常用的组合。两步走，中间产物可重复使用：

```bash
# Step 1: 从服务端 git 仓库导出补丁数据
$EXE export --from=27529d68 \
  --repo="E:\LocalGit\GitHub\BeiDou-Server" \
  --out-xml=C:\upgrade --out-diff=C:\diff

# Step 2a: wz 层（英文层）应用到客户端 EN 目录
$EXE batch --full-xml-dir=C:\upgrade\wz \
  "E:\Client\EN" "C:\diff\wz" "C:\out\EN"

# Step 2b: wz-zh-CN 层应用到客户端 Data 目录
$EXE batch --full-xml-dir=C:\upgrade\wz-zh-CN \
  "E:\Client\Data" "C:\diff\wz-zh-CN" "C:\out\Data"

# Step 3: 校验（可选但推荐）
$EXE verify "C:\out\Data\Quest\Say.img" \
  "C:\diff\wz-zh-CN\Quest.wz\Say.img.xml.diff" \
  "C:\upgrade\wz-zh-CN"
```

**关键映射规则**（patch/batch 共用）：
- 服务端 `wz/` 层 → 客户端 `EN/`（英文文本）目录（若不存在则回退到 `Data/`）
- 服务端 `wz-zh-CN/` 层 → 客户端 `Data/`（中文汉化）目录
- diff 路径 `String.wz/Mob.img.xml.diff` 自动剥 `.wz` 段 → img 路径 `String/Mob.img`

## 决策流程

1. **没有 diff，只有服务端 git 仓库** → `export --from=<hash或datetime>` 先生成 xml + diff
2. **要改一个 .img** → `patch`，带上 `--full-xml` 或 `--full-xml-dir`
3. **要改一整个目录** → `batch`，带 `--full-xml-dir`
4. **完整工作流（从服务端到客户端）** → `export` → `batch` → `verify`（见上方一站式示例）
5. **不确定能不能成** → 先 `--dry-run`
6. **改完要确认对不对** → `verify`（比肉眼看 dump-xml 可靠）
7. **只想看 .img 里有什么** → `dump-xml`
8. **img 解析失败（退出 4）** → 99% 是 `--iv` 给错了，默认 `gms` 对应 GMS v83 客户端

## 已知限制

- 只处理 `<imgdir>/<string>/<int>/<short>/<long>/<float>/<double>/<vector>/<null>` 九种标签；`<canvas>/<sound>/<uol>` 在 diff 里会被跳过（这些资源本来就不在服务端瘦 XML 里）
- 不解析 diff 文件头里的路径，多文件 diff 要拆开传
- `--strict` 失败不回滚已应用的修改；用 `--dry-run` 先校验
- 短 hunk（深嵌套小改动）必须配 `--full-xml` / `--full-xml-dir` 才能正确推路径
