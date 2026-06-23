# MapleLib.XmlImgPatcher

CLI 工具：把服务端 `*.img.xml` 的 git unified diff 应用到客户端 `*.img` 二进制文件，**保留所有未被 diff 触及的二进制资源**（PNG / Sound / UOL / Vector 等）。

典型用法是中文化场景——服务端 `wz/*.xml` 是英文，客户端 `*.img` 是中文，patch 完出来就是英文客户端；或者反过来用 `wz-zh-CN/*.xml` 把英文客户端汉化。

## 子命令

```
xml-img-patcher patch          <input.img>  <diff>       <output.img>   [选项]
xml-img-patcher dump-xml       <input.img>  <output.xml>                [选项]
xml-img-patcher batch          <img目录>    <diff目录>   <输出目录>      [选项]
xml-img-patcher batch-dump-xml <img目录>    <xml输出目录>                [选项]
xml-img-patcher verify         <patched.img> <diff>      [full-xml或目录][选项]
xml-img-patcher export         --from=<hash或datetime>                  [选项]
xml-img-patcher dump-changes   <diff>       [full-xml或目录]             [选项]
```

| 子命令 | 作用 |
|---|---|
| `patch` | 单文件应用 diff，输出新 .img。保留 PNG/音效/UOL 等所有 diff 没碰过的资源 |
| `dump-xml` | 把 .img 转成服务端格式的 .xml，方便对比或肉眼查看 |
| `batch` | 批量 patch。`a/b/Foo.img.xml.diff` 自动配对 `<img目录>/a/b/Foo.img`（自动剥 `.wz` 段）。递归扫整个 diff 目录 |
| `batch-dump-xml` | 批量 dump-xml。递归把目录下所有 `.img` 转成 `.xml` |
| `verify` | 直接加载 patched .img，逐个把 diff 里 `+` 变更跟节点的运行时值比对。最权威 |
| `export` | 从 git 仓库导出服务端补丁 xml + diff。`--from` 支持 commit hash 或 datetime |
| `dump-changes` | 调试用：打印 DiffParser 解析 diff 后得到的所有 Change |

## 选项

| 选项 | 适用 | 含义 |
|---|---|---|
| `-h`, `--help` | 全部 | 显示帮助 |
| `-V`, `--version` | 全部 | 打印版本号并退出 |
| `-v`, `--verbose` | 全部 | 失败时打印完整堆栈 |
| `--iv=<KEY>` | 全部 | WZ 加密 IV，大小写不敏感。可选 `gms` / `ems` / `bms` / `cms` / `classic` / `latest`，默认 `gms`。`cms`/`latest` 分别等价于 `bms`/`classic` |
| `--version=<KEY>` | 全部 | **已弃用**，等价于 `--iv=<KEY>`，将来会移除 |
| `--dry-run` | `patch`, `batch` | 加载 + 模拟应用，不写文件 |
| `--strict` | `patch`, `batch` | 任意一条变更失败立即中止（默认尽力跑完后汇总） |
| `--full-xml=<文件>` | `patch` | 服务端 patch 后的完整 XML。给短 hunk 提供上下文（深嵌套小改动靠这个才能定位到节点路径）。强烈建议常加 |
| `--full-xml-dir=<目录>` | `patch`, `batch` | 跟 `--full-xml` 同样作用，按目录结构自动配对 |
| `--from=<hash或datetime>` | `export` | 起点（必填） |
| `--repo=<dir>` | `export` | git 仓库根（默认当前目录） |
| `--out-xml=<dir>` | `export` | xml 输出根（默认 ~/Desktop/upgrade_yyyyMMdd） |
| `--out-diff=<dir>` | `export` | diff 输出根（默认 ~/Desktop/diff_yyyyMMdd） |
| `--prefix=<pref>` | `export` | 扫描目录前缀（可多个，默认 gms-server/wz,wz-zh-CN） |
| `--no-diff` | `export` | 只复制 xml，不生成 diff |
| `--context=<N>` | `export` | git diff 上下文行数 -U（默认 30） |

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 全部成功 |
| 1 | 部分变更失败但 .img 已写出（非 strict 模式下的"尽力跑完"） |
| 2 | 参数错误或文件/目录不存在 |
| 3 | diff 解析失败 |
| 4 | img 解析失败 |
| 5 | img 写入失败 |

## 输出格式（可被 AI / 脚本解析）

```
[parse] 12 changes from diff
[ok]  MODIFY  Mob/9999999/name = "已杀怪物数"
[ok]  ADD     0403/04031786 (subtree, 2 nodes)
[err] MODIFY  Foo/Bar — node not found
3 applied, 1 failed. Output: D:\out.img (1,335,712 bytes)
```

`batch` 末尾会再打印一份 `BATCH SUMMARY`，列出失败/跳过的文件清单。

## 构建

```
dotnet build MapleLib.XmlImgPatcher/MapleLib.XmlImgPatcher.csproj -c Release
```

发布为 self-contained 单文件 exe（csproj 已默认 PublishSingleFile + win-x64）：

```
dotnet publish MapleLib.XmlImgPatcher/MapleLib.XmlImgPatcher.csproj -c Release
```

## 例子

```bat
:: 单文件 patch + 提供完整 XML 上下文（diff 短时必备）
xml-img-patcher patch ^
  --full-xml="C:\upgrade_20260619\wz\String.wz\Mob.img.xml" ^
  "E:\BeiDou-Client\EN\String\Mob.img" ^
  "C:\diff_20260619\wz\String.wz\Mob.img.xml.diff" ^
  "C:\out\Mob.img"

:: 批量：把整个 wz/ 目录的 diff 都打到 EN/ 客户端 img 上
xml-img-patcher batch ^
  --full-xml-dir="C:\upgrade_20260619\wz" ^
  "E:\BeiDou-Client\EN" ^
  "C:\diff_20260619\wz" ^
  "C:\out\EN"

:: 批量：先 dry-run 看错误
xml-img-patcher batch --dry-run ^
  "E:\BeiDou-Client\EN" "C:\diff_20260619\wz" "C:\out\EN"

:: 校验：patched img 的实际节点值是否和 diff 一致
xml-img-patcher verify ^
  "C:\out\EN\String\Mob.img" ^
  "C:\diff_20260619\wz\String.wz\Mob.img.xml.diff" ^
  "C:\upgrade_20260619\wz"

:: 批量导出 XML（递归整个目录）
xml-img-patcher batch-dump-xml ^
  "E:\BeiDou-Client\Data" "C:\out_xml\Data"
```

## 验证与回归测试

工具的正确性用真实数据回归验证过：21 个生产 diff（11 英文 + 10 中文）打在真实客户端 .img 上，三层校验全过。

### 测试思路

diff 是要执行的指令、完整 XML 是查路径的字典、.img 是被改的目标。所以验证也按这三层由严到松：

1. **patch 自检**：CLI 输出 `0 failed` + 退出码 0——确认 21 个 batch 全跑通、没抛异常
2. **verify 子命令逐字段比对**：每个 patched .img 直接被加载，把 diff 里**每一条 `+` 变更**按解出的路径取到运行时节点，跟期望值逐字段比（字符串全等、数值相等、vector x/y 相等、类型匹配）。这一层绕开 dump-xml 的序列化噪音，最权威
3. **dump-xml 规范化对比 upgrade**：patched .img 导出成 XML，过滤掉 canvas/sound 等 patcher 不管的资源、按节点路径排序后，跟服务端 upgrade XML 比"该有的节点是否都在、值是否一致"。剩余差异都是客户端原本就有但服务端 XML 没有的字段（diff 没动 → patcher 也不动 → 是正确行为），不是漏改

### 结果

| 关卡 | 范围 | 结果 |
|---|---|---|
| batch patch | 21 diff → 21 patched img | 21/21 OK，0 fail，0 skip |
| verify 逐字段 | 21 patched img × 共 2016 个 `+` 字段 | 2016/2016 match，0 miss |
| dump-xml 规范化对比 | 21 patched vs upgrade | diff 触及的节点全一致；剩余差异均为客户端原有字段 |

抽样的真值对照（节选）：

| Case | 节点 | 期望值 | patched 实际 |
|---|---|---|---|
| zh Mob | `9999999/name` | 已杀怪物数 | ✓ |
| en Mob | `9999999/name` | Hunted Monsters | ✓ |
| zh Skill | `0001005/desc` | …300秒…（不再是 2小时） | ✓ |
| en 0403 | `04031786`（整棵子树新增） | quest=1 | ✓ |
| en Skill/000 | `0001005/level/1/cooltime`（深路径新增） | 300 | ✓ |

### 测试中发现并修复的 2 个 bug

真实数据跑出 4 个 batch 失败（EN/ZH 各 QuestInfo.img + Say.img），定位到两类 idempotent 缺陷，已修：

1. **`ApplyAdd` leaf-on-leaf 抛 `already exists`**：客户端经常已经有 server 还没补的字段（如 `demandSummary`），diff 把它们当 ADD 是正常的，应当按 diff 覆盖而非拒绝
2. **`ApplyDelete` 父节点已删抛 `parent not found`**：git diff 删一棵子树会逐叶发 DELETE，删完父节点后子节点 DELETE 找不到父节点，应当 no-op

详见 commit `db33fba`。修复后 21/21 全过。

### 复现（本机回归）

测试数据放仓库根的 `test-runs/`（已 `.gitignore`，不入库）。结构：

```
test-runs/
  input-en/  input-zh/      从客户端拷贝的 .img 输入（不污染原始 img）
  patched-en/  patched-zh/   batch 输出
  dumped-en/   dumped-zh/    batch-dump-xml 输出
  reports/                    各步骤日志 + 三层校验报告
  normalize2.py              dump-xml 规范化（过滤 canvas/sound + 按路径排序），用于第 3 层集合对比
```

复现步骤：

```bat
set EXE=MapleLib.XmlImgPatcher\bin\Release\net10.0-windows\win-x64\xml-img-patcher.exe

:: 1) batch 两条线（EN / ZH）
%EXE% batch --full-xml-dir="C:\upgrade_20260619\wz" ^
  test-runs\input-en  C:\diff_20260619\wz  test-runs\patched-en   > test-runs\reports\01-batch-en.log 2>&1
%EXE% batch --full-xml-dir="C:\upgrade_20260619\wz-zh-CN" ^
  test-runs\input-zh  C:\diff_20260619\wz-zh-CN  test-runs\patched-zh > test-runs\reports\02-batch-zh.log 2>&1

:: 2) verify 逐个 patched img（21 个，逐字段比对 —— 权威层）
::    对每个 diff: %EXE% verify <patched.img> <diff> <对应 full-xml或目录>
%EXE% verify test-runs\patched-en\String\Mob.img ^
  C:\diff_20260619\wz\String.wz\Mob.img.xml.diff C:\upgrade_20260619\wz

:: 3) dump-xml 后规范化对比 upgrade（第 3 层，集合差）
%EXE% batch-dump-xml test-runs\patched-en test-runs\dumped-en
py test-runs\normalize2.py <dumped.xml> <canon.xml>
:: 再跟 upgrade 规范化后的 XML 做集合差（comm -13/-23），确认 diff 触及的节点都在且一致
```

通过标准：21/21 batch 退出码 0、21/21 verify `0 miss`、dump-xml 规范化后 diff 触及的路径全一致。

## 内部模块

| 文件 | 作用 |
|---|---|
| `Program.cs` | CLI 入口、参数解析、子命令分发 |
| `Parser/XmlLineParser.cs` | 解析单行 XML 元素（`<imgdir>` / `<int>` / `<vector>` 等） |
| `Parser/DiffParser.cs` | 解析 unified diff，按 hunk 维护 imgdir 栈，输出 `List<Change>`；可读 full-xml 给短 hunk 种栈 |
| `Model/{Change,SubTree,ChangeOp,ValueType}.cs` | 数据模型 |
| `Patcher/MapleLibAdapter.cs` | 封装 MapleLib 的 load / find / set / add / remove / save |
| `Patcher/ImgPatcher.cs` | 协调器：load → 按 Delete-then-Modify-then-Add 相位顺序逐条 apply → save |

## 语义说明

`patch` 把 ADD 和 DELETE 都视作幂等：

- **ADD 命中已存在节点**：按 diff 的值覆盖（不论容器还是叶子）。客户端经常已经有 server 还没补的字段，diff 把它们当 ADD 是正常情况
- **DELETE 命中不存在节点 / 父节点不存在**：no-op。git diff 删一棵子树时会逐叶发 DELETE，删完父节点后子节点的 DELETE 自然没意义

`ImgPatcher` 内部把所有变更按 `Delete → Modify → Add` 三相位重排再执行，避免 git diff 在同一对兄弟里同时出现 `+ <imgdir X>` 和 `- <imgdir X>` 时 ADD 后被 DELETE 误清。

## 已知限制

- 仅处理 `<imgdir>` / `<string>` / `<int>` / `<short>` / `<long>` / `<float>` / `<double>` / `<vector>` / `<null>` 这九种 diff 中常见的标签。`<canvas>` / `<sound>` / `<uol>` 等开标签若出现在 diff 中作为未知行被跳过——这些资源原本就不会出现在服务端瘦 XML 里
- 不解析 diff 文件头里的路径，仅看 hunk 内容。多文件 diff 应当拆开传入
- `--strict` 失败时不会回滚已应用的修改；但 `--dry-run` 不会写文件，可用于先校验
- 短 hunk（深嵌套小改动）必须配 `--full-xml` / `--full-xml-dir` 才能正确推路径——hunk 上下文不含外层 `<imgdir>` 时光看 diff 推不出
- `--full-xml` / `--full-xml-dir` 路径不存在时会输出 `[warn]` 但不中止（仍按无 full-xml 的方式跑）
- `dump-xml` 输出风格（缩进 2 空格、`<x/>` 自闭合、`<null>` 标签写法）跟服务端 XML 不完全一致，文本级 diff 会有假阳性。需要严格比较时用 `verify` 子命令

## 姊妹仓库

| 实现 | 仓库 | 产物 |
|---|---|---|
| C# | <https://github.com/SleepNap/MapleLib-cli> | `dist/xml-img-patcher.exe`（.NET AOT/publish 单文件） |
| Java | <https://github.com/SleepNap/orange-wz-cli> | `dist/xml-img-patcher.exe`（GraalVM native，standalone） |

两边功能、子命令（`patch / dump-xml / batch / batch-dump-xml / verify / export`）、选项、退出码、输出格式**完全一致**，脚本可互换。两边 `dump-xml --linux` 输出逐字节一致。
