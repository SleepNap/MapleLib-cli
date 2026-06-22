# xml-img-patcher 待办（交接给下一个 AI）

> 当前状态（commit `3170676`）：C# 版 CLI 31/31 verify 通过（含 DELETE 校验 + SubTree 递归展开）。但存在一个已知未修复的 DiffParser bug（见下文第 0 节）。

## 0. 已知未修复 bug：DiffParser plus block 内 sibling 容器丢失（Say.img 4928/1）

**症状**：`wz-zh-CN/Quest.wz/Say.img.xml.diff` 的 hunk 19 里，`4928/1` 容器开标签在 plus block 内，但 close `</imgdir>` 落在 context 行（line 2519）而非 plus 行。C# DiffParser 处理时**丢失了 `1` 这一层**，导致 `1` 的子节点（stop/yes/ask/strings）全部浮到 `4928/` 根级，而不是 `4928/1/` 下。

**Java 版对照**：Java 版正确处理了这种情况，4928/1 子树完整。C# verify 假通过——用同一份有 bug 的 DiffParser 解析 diff 得到短路径 Change，然后在 C# patcher 自己写错的孤儿节点上"找到"了。

**复现**：
```bash
# 看 C# 的 4928 结构，缺 1 容器
dist/xml-img-patcher.exe dump-xml work/out_csharp/Data/Quest/Say.img /tmp/cs.xml
awk '/imgdir name="4928"/,/imgdir name="4929"/' /tmp/cs.xml
# 对比 Java 版，有 1 容器
```

**diff 形态**（关键部分）：
```
+ <imgdir name="1">           ← 4928/1 开，plus 行
+   <string 0..7>             ← plus 行
+   <int ask/>                ← plus 行
+   <imgdir name="yes">       ← plus 行
+     <string 0/>             ← plus 行
  </imgdir>                    ← context 行（关 yes），不在 plus entries
- <imgdir name="2">...         ← minus 行
+ <imgdir name="stop">        ← plus 行，应该是 4928/1/stop
+   <imgdir name="item">...
+   ...
+ </imgdir>                    ← plus 行，关 stop
+ </imgdir>                    ← plus 行，关 1
```

**根因**：`HandlePlusEntries`（`DiffParser.cs:215`）处理 plus entries 时：
1. 碰到 `<imgdir name="1">` push `1`，调 `BuildSubTreeFrom` 消耗子节点
2. `BuildSubTreeFrom` 碰到 `<imgdir name="yes">` 递归 push yes，消耗 yes 的子节点
3. yes 的 `</imgdir>` close 在 context 行（不在 entries），`BuildSubTreeFrom` 碰不到，**yes 没 pop**
4. `BuildSubTreeFrom` 继续消耗，把后续 `<imgdir name="stop">` 当成 yes 的子节点
5. 最终 `1` 容器的 SubTree 被错误构建，`1` 这层路径丢失

**已尝试的修法（失败）**：
- `HandlePlusEntries` 碰到 `</imgdir>` 时按 `stack.Count > initialDepth` 判断 pop 并 continue（不 return）→ 修好 4928/1 但 4937/1/stop/item 退化（plus block 内 sibling 容器顺序错乱，出现两个 `Add 4937/1/stop`）
- 直接 `continue` 不 return → 同样退化

**待尝试方向**：
- `BuildSubTreeFrom` 需要能识别"这个 `</imgdir>` close 不在 entries 里，对应的 open 也不在，应该停止当前递归层级"。可能需要传一个"期望的 close 深度"参数，或者改成不依赖 entries 边界、而是按 indent 推断层级。
- 或者：`HandlePlusEntries` 不用 `BuildSubTreeFrom` 递归，改成自己用 stack 状态机遍历 entries，碰到 `</imgdir>` 时按 indent 判断关的是哪层。
- 参考 Java 版 `orange-wz-cli` 的 `DiffParser` 实现（它处理对了）。

**影响**：仅影响"plus block 内有容器开标签、但对应 close 在 context 行"这种 diff 形态。生产数据中 Say.img hunk 19 触发，其他 30 个 diff 不受影响。

**临时缓解**：无。verify 因用同一份 DiffParser 假通过，需要手工对照 Java 版 dump 才能发现。

---

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
