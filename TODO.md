# xml-img-patcher 待办（交接给下一个 AI）

> 当前状态（commit `6404967`）：C# 版 CLI 已完成，20 个 diff 全部通过 `verify` 子命令严格校验，**1842/1842 节点值 100% 匹配**。以下是还没做的事。

## 1. Java 版（计划里明确要求，完全没做）

计划文档（本地 `xml-img-patcher-plan.md`，未上仓）第 1.3 节原话："java 和 c# 各来一个"。目前只有 C# 版。

- 库：`leevccc/orange-wz`（https://github.com/leevccc/orange-wz），用户指定
- 参考 `E:\LocalGit\Local\beidou-cli`（picocli + GraalVM native-image 模板）
- 要对等实现 C# 版的全部子命令：`patch` / `dump-xml` / `batch` / `batch-dump-xml` / `verify`
- DiffParser 逻辑直接移植（Java→C# 已验证过的算法），关键点：
  - 双栈（old/new）+ hunk 内 imgdir 栈追踪
  - `--full-xml` 种子化短 hunk 的嵌套栈
  - `&#xA;` 等数字字符引用必须解码成真换行
  - 执行相位固定 Delete→Modify→Add（git diff 在 sibling 层会交错 -/+）
  - ADD 遇已存在 container 做 upsert（清空旧子树填新的）
  - 容错：diff 行漏了开头 `<` 的残缺标签（实测服务端 diff 有这种）
- 验收标准：用同一组 20 个 diff + img 跑，Java 版输出和 C# 版节点级一致

## 2. 真机验证（从未做过）

计划 M2/M4 要求，但一直没真跑过游戏：

1. 把 `work_20260618/out/` 下的 patched .img 放回 `BeiDou-Client/Data/` 和 `EN/`
2. 启动 `BeiDou.exe`
3. 确认不闪退
4. 进游戏看：
   - 任务道具 04031786/04031787 图标正常显示（不是问号方块）—— 验证 PNG 保留
   - 怪物名字变成"已杀怪物数" —— 验证 MODIFY
   - 任务对话文本换行正常（不是显示 `&#xA;` 字面字符）—— 验证实体解码修复

如果第 4 点的换行还是乱码，说明 `&#xA;` 解码没覆盖所有路径，回头看 `XmlLineParser.DecodeXmlEntities`。

## 3. dump-xml 的双重转义（已知缺陷，非阻塞）

`verify` 子命令绕过了 dump-xml，所以 patch 正确性已证实。但 `dump-xml` 导出的 XML 有两个输出质量问题：

- 字符串值里的 `&` 被转义成 `&amp;`，导致 `&#xA;` 变成 `&amp;#xA;`（MapleLib 的 `XmlUtil.SanitizeText` 行为）
- 字符串值里的真换行 `\n` 写进 `value="..."` 属性时变成字面换行，XML 解析时被规范化成空格

后果：用 `dump-xml` 导出再和服务端 XML 做 byte-level diff 会有假差异。要修的话，改 `WzClassicXmlSerializer` 的写出逻辑（或在我们 `dump-xml` 流程里后处理），把 `\n` 写成 `&#xA;`、不要二次转义已存在的实体。

优先级低，因为 `verify` 已经能做正确性校验。

## 4. 单元测试（目前 0 个）

`DiffParser` / `XmlLineParser` 没有任何单元测试，唯一"测试"是 `verify` 子命令跑那 20 个真实 diff。

建议加 xUnit 测试项目，覆盖：
- `XmlLineParser`：各种标签解析、`&#xA;` 解码、残缺标签容错
- `DiffParser`：MODIFY 合并、ADD 子树、DELETE、interleaved hunk、full-xml 种子化
- `MapleLibAdapter`：upsert、already-exists、type-mismatch

参考用户全局规则 `~/.claude/rules/ecc/csharp/testing.md`（xUnit + FluentAssertions，覆盖率 80%+）。

## 5. 边缘情况（未在 20 个 diff 中触发，但理论上可能）

- **ADD 遇已存在且类型变化**（leaf→container 或反之）：当前抛 `already exists`，没做类型替换。可在 `MapleLibAdapter.ApplyAdd` 补 remove-old + add-new 分支。
- **canvas/sound/uol 出现在 diff 里**：服务端瘦 XML 不会有这些节点的修改，但如果某天 diff 真带了，`XmlLineParser` 把开标签当未知行跳过，静默忽略。需要的话补上 canvas/sound 的解析。
- **多文件 diff 混在一个 .diff 里**：当前假设一个 .diff 只对应一个 img。如果服务端某天把多个 img 的变更合并到一个 diff 文件，需要按 `diff --git` 头拆分。

## 6. 计划文档本身没上仓

`xml-img-patcher-plan.md`（在 `.gitignore` 里）有完整背景：BeiDou-Client `6cc7c5f` 那次 0403.img 塌缩事故、wz vs wz-zh-CN 的语义、客户端 Data/EN 对应关系、M1-M5 里程碑、验证用例 C1-C4。

下一个 AI 如果需要这些上下文，让用户从本地 `E:\LocalGit\GitHub\MapleLib-cli\xml-img-patcher-plan.md` 提供，或者这个 TODO 加上后应该够用。

---

## 快速复现当前已验证状态

```cmd
git clone https://github.com/SleepNap/MapleLib-cli
cd MapleLib-cli
publish.bat
:: 产物在 dist\xml-img-patcher.exe

:: 需要用户提供的数据（在用户桌面）：
::   C:\Users\CN\Desktop\diff_20260618\       (wz/ 和 wz-zh-CN/ 两套 diff)
::   C:\Users\CN\Desktop\upgrade_20260618\    (服务端完整 XML，full-xml 种子用)
::   E:\LocalGit\GitHub\BeiDou-Client\        (客户端 img)

:: 批量 patch（zh-CN → Data）
dist\xml-img-patcher.exe batch --full-xml-dir="C:\Users\CN\Desktop\upgrade_20260618\wz-zh-CN" "E:\LocalGit\GitHub\BeiDou-Client\Data" "C:\Users\CN\Desktop\diff_20260618\wz-zh-CN" "C:\out\Data"

:: 校验（每个文件 0 miss 才算对）
dist\xml-img-patcher.exe verify "C:\out\Data\Quest\Say.img" "C:\Users\CN\Desktop\diff_20260618\wz-zh-CN\Quest.wz\Say.img.xml.diff" "C:\Users\CN\Desktop\upgrade_20260618\wz-zh-CN"
```
