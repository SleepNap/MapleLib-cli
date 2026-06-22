---
name: wz-patch-csharp
description: 用 C# 版 xml-img-patcher.exe 把服务端 XML 的 git unified diff 应用到客户端 .img 文件，保留未触及的 PNG/Sound/Canvas/UOL/Vector 等二进制资源。当需要给 MapleStory 客户端 .img 打补丁、批量打补丁、把 .img 导出成 XML、或校验 patched .img 是否正确时使用本 skill。exe 路径、子命令、退出码、坑点都在下面。
---

# xml-img-patcher（C# 版）使用指南

## 工具位置

self-contained 单文件 exe，无需装 .NET 运行时：

```
MapleLib.XmlImgPatcher/bin/Release/net10.0-windows/win-x64/xml-img-patcher.exe
```

仓库根 `dist/xml-img-patcher.exe` 是旧副本，**跑最新代码要先 build**：

```bash
dotnet build MapleLib.XmlImgPatcher/MapleLib.XmlImgPatcher.csproj -c Release
```

build 产物在 `MapleLib.XmlImgPatcher/bin/Release/net10.0-windows/win-x64/xml-img-patcher.exe`。下文用 `$EXE` 代指这个路径。

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
```

| 子命令 | 干啥 |
|---|---|
| `patch` | 单文件打补丁 |
| `dump-xml` | 把 .img 转成服务端格式 .xml（默认跳过 PNG/Sound，只出节点骨架） |
| `batch` | 批量 patch，自动按路径配对 diff↔img |
| `batch-dump-xml` | 批量 dump-xml |
| `verify` | 加载 patched .img，把 diff 里每条 `+` 变更跟运行时节点值逐字段比对。**最权威的校验** |

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
| `--full-xml-dir=<目录>` | batch | 完整服务端 XML 根目录，按 batch 目录结构自动配对 |
| `--linux` | dump-xml, batch-dump-xml | 输出用 LF 行尾（默认 CRLF） |

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

## 决策流程

1. **要改一个 .img** → `patch`，带上 `--full-xml` 或 `--full-xml-dir`
2. **要改一整个目录** → `batch`，带 `--full-xml-dir`
3. **不确定能不能成** → 先 `--dry-run`
4. **改完要确认对不对** → `verify`（比肉眼看 dump-xml 可靠）
5. **只想看 .img 里有什么** → `dump-xml`
6. **img 解析失败（退出 4）** → 99% 是 `--iv` 给错了，默认 `gms` 对应 GMS v83 客户端

## 已知限制

- 只处理 `<imgdir>/<string>/<int>/<short>/<long>/<float>/<double>/<vector>/<null>` 九种标签；`<canvas>/<sound>/<uol>` 在 diff 里会被跳过（这些资源本来就不在服务端瘦 XML 里）
- 不解析 diff 文件头里的路径，多文件 diff 要拆开传
- `--strict` 失败不回滚已应用的修改；用 `--dry-run` 先校验
- 短 hunk（深嵌套小改动）必须配 `--full-xml` / `--full-xml-dir` 才能正确推路径

## 和 Java 版的关系

姊妹仓库 `orange-wz-cli`（Java 实现）功能、子命令、选项、退出码、输出格式**完全一致**，脚本可互换。

- C# 产物：`xml-img-patcher.exe`（self-contained，.NET AOT/publish 单文件）
- Java 产物：`xml-img-patcher.exe`（GraalVM native，standalone）

两边都用 exe 的话，调用方式完全一样，连 `$EXE` 都可以指向同一个命令名。两边 `dump-xml --linux` 输出逐字节一致。
