# MapleLib.XmlImgPatcher

CLI 工具：把服务端 `*.img.xml` 的 git unified diff 应用到客户端 `*.img` 二进制文件，**保留所有未被 diff 触及的二进制资源**（PNG / Sound / UOL / Vector 等）。

参考 `xml-img-patcher-plan.md`。

## 用法

```
xml-img-patcher <input.img> <diff.xml.diff> <output.img> [选项]
```

选项：

| 选项 | 含义 |
|---|---|
| `-v`, `--verbose` | 失败时打印堆栈 |
| `--dry-run` | 加载 + 应用到内存，但不写出文件 |
| `--strict` | 任意一条变更失败立即中止（默认尽力跑完后汇总） |
| `--version=GMS` | WZ 加密版本，可选 `GMS` / `EMS` / `BMS` / `CLASSIC`（默认 GMS） |
| `-h`, `--help` | 显示帮助 |

退出码：0 全部成功 / 1 部分失败 / 2 参数错 / 3 diff 解析错 / 4 img 解析错 / 5 img 写入错。

## 输出格式（可被 AI / 脚本解析）

```
[parse] 12 changes from diff
[ok]  MODIFY  Mob/9999999/name = "已杀怪物数"
[ok]  ADD     0403/04031786 (subtree, 2 nodes)
[err] MODIFY  Foo/Bar — node not found
3 applied, 1 failed. Output: D:\out.img (1,335,712 bytes)
```

## 构建

```
dotnet build MapleLib.XmlImgPatcher/MapleLib.XmlImgPatcher.csproj -c Release
```

或发布为 self-contained 单文件 exe：

```
dotnet publish MapleLib.XmlImgPatcher/MapleLib.XmlImgPatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 模块

| 文件 | 作用 |
|---|---|
| `Program.cs` | CLI 入口、参数解析 |
| `Parser/XmlLineParser.cs` | 解析单行 XML 元素（`<imgdir>` / `<int>` / `<vector>` 等） |
| `Parser/DiffParser.cs` | 解析 unified diff，按 hunk 维护 imgdir 栈，输出 `List<Change>` |
| `Model/{Change,SubTree,ChangeOp,ValueType}.cs` | 数据模型 |
| `Patcher/MapleLibAdapter.cs` | 封装 MapleLib 的 load / find / set / add / remove / save |
| `Patcher/ImgPatcher.cs` | 协调器：load → 逐条 apply → save，统一日志 |

## 已知限制

- 仅处理 `<imgdir>` / `<string>` / `<int>` / `<short>` / `<long>` / `<float>` / `<double>` / `<vector>` / `<null>` 这九种 diff 中常见的标签。`<canvas>` / `<sound>` / `<uol>` 等开标签若出现在 diff 中，目前作为未知行被跳过（这些资源原本就不会出现在服务端瘦 XML 里）。
- 不解析 diff 文件头里的路径，仅看 hunk 内容。多文件 diff 应当拆开传入。
- `--strict` 失败时不会回滚已应用的修改；但 `--dry-run` 不会写文件，可用于先校验。
