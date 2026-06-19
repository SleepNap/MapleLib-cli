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
```

| 子命令 | 作用 |
|---|---|
| `patch` | 单文件应用 diff，输出新 .img。保留 PNG/音效/UOL 等所有 diff 没碰过的资源 |
| `dump-xml` | 把 .img 转成服务端格式的 .xml，方便对比或肉眼查看 |
| `batch` | 批量 patch。`a/b/Foo.img.xml.diff` 自动配对 `<img目录>/a/b/Foo.img`（自动剥 `.wz` 段：`String.wz/Mob.img.xml.diff` ⇄ `String/Mob.img`）。递归扫整个 diff 目录 |
| `batch-dump-xml` | 批量 dump-xml。递归把目录下所有 `.img` 转成 `.xml` |
| `verify` | 直接加载 patched .img，逐个把 diff 里 `+` 变更跟节点的运行时值比对。绕开 dump-xml，测的是 .img 内部内容本身，最权威 |

## 选项

| 选项 | 适用 | 含义 |
|---|---|---|
| `-h`, `--help` | 全部 | 显示帮助 |
| `-v`, `--verbose` | 全部 | 失败时打印完整堆栈 |
| `--version=<KEY>` | 全部 | WZ 加密 IV，可选 `GMS` / `EMS` / `BMS` / `CLASSIC`，默认 `GMS` |
| `--dry-run` | `patch`, `batch` | 加载 + 模拟应用，不写文件 |
| `--strict` | `patch`, `batch` | 任意一条变更失败立即中止（默认尽力跑完后汇总） |
| `--full-xml=<文件>` | `patch` | 服务端 patch 后的完整 XML。给短 hunk 提供上下文（深嵌套小改动靠这个才能定位到节点路径）。强烈建议常加 |
| `--full-xml-dir=<目录>` | `batch` | 跟 `--full-xml` 同样作用，按 batch 的目录结构自动配对 |

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
