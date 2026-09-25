# PaperSharp 路线图

> 对标：**takumi**（<https://github.com/kane50613/takumi>，Rust，JSX/HTML → OG 图 + 分页 PDF，多运行时）。
> 我们的定位：**.NET 库，HTML/CSS → 文档（发票/报表/合同）+ 图卡**，不做多运行时与前端构建期能力。
>
> M1–M4 全部完成、M5 完成前半、M7（复杂文字）与并发安全化已完成；M8（伪元素 `::before/::after` + 计数器、`background-clip: text` 渐变文字、`backdrop-filter` 毛玻璃）与 M9（`@keyframes`/`animation` 确定性取帧）已交付（验收样例 `assets/m1–m5/m7–m9.html`，全清单见 README「能力速览」）。本文只记录**剩余差距与下一步**。

## 差距清单（takumi 有 / 我们没有的，全量）

| 领域 | 缺口 | 成本 | 备注 |
|---|---|---|---|
| 性能/架构 | ~~AngleSharp 层叠超线性：2000 元素文档 buildDoc 21–74s~~ **已消掉（09-24）**：物化样式表集合 → table 2000 行 74.4s→5.29s；同日第二刀（按元素缓存 + 双轮并一轮）再拿 Playground buildDoc **1.76×** | ~~高~~ **已交付** | 层叠性能理由用尽；M6 只剩依赖收敛，见 M6 与 PERF_REPORT §5.5/§5.6 |
| 分页/CSS | `@media`、`transform` | 低–中 | 伪元素 `::before/::after` + 计数器、`background-clip: text`、`backdrop-filter` 已于 M8 交付；`overflow` 裁剪已交付（含 `clip`/`overlay`/两值简写的原文兜底）；`transform` 仍未做（依赖它的动画停在基线态） |
| 布局/BFC | `overflow` 建立 BFC（float 包含、**父子** margin 坍缩隔离）、`display:flow-root` | 中 | 裁剪已交付、格式化上下文未做：`FloatContext.Insets/ClearTo` 无条件沿 `Parent` 链走到根（没有 BFC 停止边界），`LayoutBuilder` 的父子坍缩判定只看有无 border/padding。**兄弟**间坍缩与 overflow 无关，别照规范直觉改（实测已钉在 `MarginCollapsingTests` 与 `OverflowClipTests`） |
| 定位 | `fixed`/`sticky` 视口语义 | 中 | 静态文档场景价值有限 |
| 布局 | flex `align-content`（现简化为 flex-start） | 低 | |
| 文本 | `text-wrap: pretty`、`lang` 汉字分形（zh/ja/ko） | 低–中 | locale 传 SKFont 即可分形 |
| 视觉 | `corner-shape` bevel/scoop/notch、SVG 滤镜图 `filter: url()`、`offset-path` 路径文字 | 中 | Skia 有原语，缺 CSS 侧解析 |
| 图片 | 网络图片 URL | 低 | 当前刻意不抓（离线 + 确定性） |
| PDF | Tagged PDF / PDF/A / PDF/UA、附件（Factur-X XML） | 高 | Skia 后端不出结构树/XMP；`PdfEnhancer` 增量尾部已可挂载 → M5 剩余 |
| 文字系统 | 嵌套双向（RTL 中整句引语）的完整 UAX#9；国旗 emoji（regional indicator 成对合成）；shaped run 的 letter-spacing | 中 | M7 已交付连写/bidi run/字素/字形回退，这些是其已知边界 |
| 字体 | `@font-face` 的 `format()` 校验、`local()` | 低 | |
| 输入 | 网络字体外的 JSX / Tailwind / 动画 GIF·APNG / WASM 多运行时 | — | JSX/Tailwind/WASM 绑定已有；动画 GIF·APNG **明确不做**（与静态文档定位冲突）——`@keyframes`/`animation` 改以「确定性取帧」交付（见 M9） |

## 里程碑

### M6 · 层叠性能：超线性已消，自研替换降级为可选 —— 🟡 进行中

**2026-09-24 状态变更（先读这条，本节下面的六步计划按此重排）**：作者 CSS 路径的 O(n^2.5) **已定位并消掉，且没有自研级联**。根因是 AngleSharp 的样式表集合是**未物化的 LINQ**，而它层叠一个元素时会对该元素的**每个祖先各枚举一次集合** → 每元素 O(深度 × 文档大小)。改动 ~40 行（`Css/EagerStyleCollection.cs`：整篇文档枚举一次、`DocContext.Styles` 复用），特异度排序 / UA·作者·内联顺序 / `!important` / 继承 / `var()` **一行语义都没替换**，仍由 AngleSharp 判定。

| | 改前 | 改后 |
|---|---:|---:|
| 引擎真实入口 table 2000 行 buildDoc | 74.4s | **5.29s** |
| 净层叠同进程对拍（table / flexlist 2000） | 25.9s / 5.41s | 1.04s / 0.28s（**25.0× / 19.2×**） |
| Playground 22 样例端到端 | 2,471ms | **2,091ms（−15%）** |
| （同日第二刀，§5.6 按元素缓存 + 双轮并一轮，接上行） | 2,893ms | **2,001ms（累计口径见 PERF_REPORT §5.6：buildDoc 1.76×）** |

**门槛因此回到「元素数轴」**：≤501 盒的真实页面只有一两成收益，上千元素的长表格/长页面才是主战场（PERF_REPORT §5.5 有机制反编译、探针 A/B/C/D/F/G 全表、四道正确性门）。

**~~剩余的两刀~~ ✅ 两刀都已落地（09-24 同日第二刀，PERF_REPORT §5.6）**：

- **(a) ✅ 按元素缓存 `ComputeCascadedStyle`**：递推式 `R(e)=ComputeCascadedStyle(e, parent: R(最近祖先))`（折叠可结合，等价于 AngleSharp 的逐祖先 Update），每元素/每伪元素一份缓存。探针新增 H 行：比 F 行低 **7.6–13.5×**，table 档已到 G 行下界附近。
- **(b) ✅ 让每个元素只做一轮层叠**：raw 声明串直接吃 computed 轮缓存的**折算前**条目，双轮并一轮。⚠️ 与**已否决**的「无缓存时按元素启发式跳过第二轮」不是一回事（那条失败在 computed 对未声明属性返回空串、兜底必问一次；这条是问过的必然命中），见 PERF_REPORT §6.4 的更正。
- 合起来引擎真实入口 `pgstage 9`：buildDoc **1,701 → 964ms（1.76×）**、e2e **2,893 → 2,001ms（1.45×）**。门：22 真实样例 × {宿主,::before,::after} × {computed,raw} × 94 属性 = **1,274,452 次比对 0 不一致**。
- **(c) ✅ 第 3 刀：每元素属性记忆化视图 `PropCache`**（09-24 夜，PERF_REPORT §5.8）：`ReadStyle` 的 `Get`/`Spec` 各包一个惰性 Dictionary（引擎每元素 ~130 次属性读里重复的那部分变命中）。**同树 worktree A/B** 实测 buildDoc **−8~−11%**、e2e **−11%**（m1/pricing 系 −17~−19%）；门：两臂 22 样例 `boxes` JSON 22/22 字节全等。层叠侧性能杠杆到此连续兑现三刀，剩余候选转向自研值解析 span 化与预处理/DomPath 门控（§5.7 的自研 47%）。
- **(d) ✅ 第 4 刀：自研值解析 span 化**（09-25 凌晨，PERF_REPORT §5.9）：`CssValueParser` 十个函数改 `ReadOnlySpan<char>` 切片 + Ordinal 比较，公开签名不变。门：worktree 两臂（对照臂只回退本刀的 9 个方法体、保留对端混在同文件的 `ExpDigits` 修复）22 样例 boxes 22/22 字节全等。实测**远小于 §5.7 账面**：CSS 重页 buildDoc −7%、全语料 ~−1%——那 161ms 里七成是 AngleSharp 自己的串分配，PropCache 之后自研解析只吃未缓存的首读。教训：拆块百分比要按缓存命中率折减。剩余候选：display 分型跳读（回归面大）、正则预处理/DomPath 门控。

**六步计划（原方案，现仅为「彻底移除 AngleSharp.Css 依赖」服务；除②⑥已交付外不再阻塞性能）**：

1. ⏸ **已降级** CSS tokenizer/parser —— 不再有性能理由
2. ✅ 特异度 + key-selector 分桶匹配 —— **仅展平快照路径**已交付（`Css/FlatCascade.cs`：类名→声明索引，规则形状不符即整份弃权退回 AngleSharp）
3. ⏸ **已降级** 属性元数据表：继承性 / initial / 简写展开
4. ⏸ **已降级** 值解析直接接入现有 `CssValueParser` → `ComputedStyle`
5. ⏳ 换掉 `StyleResolver` 里 `Get/Spec/Val/Raw` 的内部实现（对外零改动）——快照路径已在缝内替换；作者 CSS 路径经 §5.5 已拿到 19–25×，剩余动机只有依赖收敛
6. ✅ **双跑对账** —— 工具 `tools/SnapshotProfile ... diff`：逐元素逐字段 + 全量像素 + 三条灵敏度对照 + 逐类回填老值重渲染（结果见 PERF_REPORT §5.4）

**已交付的量化结果**：① 真实站点快照（964 盒）WASM `precomputed` 渲染 **23.3s → 3.0s**，两路 PNG 字节完全相同（PERF_REPORT §5.4）；② 作者 CSS 路径超线性消掉，table 2000 行 74.4s → 5.29s（**§5.5**，本轮）；③ 同路按元素缓存 + 双轮并一轮，Playground 全语料 buildDoc **1.76×**、e2e **1.45×**（2,893 → 2,001ms，**§5.6**，同日）。

附带收益：一次性消掉 README「坑位备忘」里 6 类 AngleSharp bug（NRE、% 折算、calc 预解析、简写序列化、UA 表、text-overflow 兜底）——快照路径已顺带消掉 UA 表幽灵声明与渐变 NRE 丢样式两类，另带出 `align-items`/`justify-content` 的 `normal` 归一与伪元素 `box-sizing` 两个真实缺陷修复。

### M5 剩余 · PDF 合规 —— 🟡 需求驱动

已完成：书签 / 链接注释 / metadata（自写 `PdfEnhancer` 增量尾部，未换 PDF 后端）。待补：Tagged PDF 结构树 → PDF/UA → PDF/A、附件（Factur-X）。增量尾部机制已就位，加对象即可，但结构树/XMP 工程量大——等真实企业/政务需求触发。

### M7 · 复杂文字系统 —— ✅ 已完成（2026-09-22）

HarfBuzz shaping（阿拉伯连写）+ 轻量 bidi（grapheme 级强方向 run，RTL 基底反转 run 序）+ 字素安全分词 + 字形级字体回退。
验收样例 `assets/m7.html`（阿拉伯段落 / RTL 混排拉丁数字 / LTR 内嵌 RTL / 越南组合符 / 韩中混排 / emoji / RTL inline-block / RTL 表格）。
依赖：`SkiaSharp.HarfBuzz`（2.88 有绑定，此前"无绑定"判断有误）+ `HarfBuzzSharp.NativeAssets.Linux`。
已知边界：嵌套双向（单层 run 反转）、国旗 emoji、shaped run 内 letter-spacing、SVG 输出不做 shaping（由查看器重排）。

### M8 · 伪元素 + 渐变文字 + 毛玻璃 —— ✅ 已完成（2026-09-23）

`::before`/`::after` 生成内容（字符串 / `attr()` / 引号 / `url()` 图 / `counter()`·`counters()` 自动编号，含中文数字），行内与块级各自成盒、多级列表嵌套编号；`background-clip: text` 把背景铺进字形（渐变文字）；`backdrop-filter` 读取盒背后已绘像素做模糊（毛玻璃）。
验收样例 `assets/m8.html`（伪元素 + 计数器全谱）；`background-clip: text` / `backdrop-filter` 走像素级断言（`PseudoElementTests` 等）。
已知边界：`backdrop-filter` 仅位图路径生效（PDF/SVG 矢量路径跳过）；AngleSharp 对伪元素 UA 注入 `display:block`、吃掉 `counters()` 逗号、`counter-*`/`background-clip` computed 返回占位值——均按既有「原始 CSS 兜底」模式绕过（见 README「AngleSharp 坑位备忘」）。

### M9 · 动画确定性取帧 —— ✅ 已完成（2026-09-23）

解析 `@keyframes`（from/to、百分比、多选择器帧合并）+ `animation` 简写（duration/delay/timing 含 cubic-bezier·steps/iteration/direction/fill-mode）；渲染 API 加 `animationTime`（秒）参数，按该时刻求值插值覆盖样式，输出仍是**静态单帧、逐字节确定**。插值覆盖引擎已渲染的属性：opacity / color / background-color / font-size / letter-spacing / width / height / top·right·bottom·left / border-radius。Wasm `Api.Render`、JS `render(opts.animationTime)`、Tailwind `animate-pulse` 均已接线。
验收样例 `assets/m9.html`（淡入 / pulse / 颜色 / 宽度圆角 / transform 边界声明）；`AnimationTests` 用盒中心像素通道做连续量断言（单调插值、fill 末态、alternate 对称、同刻逐字节确定、transform 与无动画逐字节一致）。
已知边界：**`transform` 未实现** → `spin`/`ping`/`bounce` 等依赖变换的动画关键帧被解析但不生效（方块静止，不做假变换）；缓动按全局进度求值（多关键帧 + 非线性的严格 per-interval 语义是简化，两端点与常见单段动画正确）。

### 并发安全化（线程隔离）—— ✅ 已完成（2026-09-22）

`RenderScope`（AsyncLocal）承载每请求私有的 @font-face 注册表与 SKTypeface 缓存；`ImageLoader` 缓存键编入 BaseDir；`StyleResolver` 文档级态（title/author/vars/unsupported）收进 `DocContext` 随根样式下发；`GoogleFonts` 每请求独立 HttpRequestMessage。
回归哨兵：xunit 禁并行已移除，全套测试本身即并发压测；另有 `ConcurrencyTests`（16 线程字节一致 / 交错文档无污染 / 不同 BaseDir 图片不串台 / 并行 PDF 全有效）。

### 明确不做

- 动画 GIF/WebP/APNG、视频帧 —— takumi 主打 OG 动画，我们是文档渲染器；`@keyframes`/`animation` 改以「确定性取帧」交付（M9），输出恒为静态单帧
- Cloudflare Workers/WASM 多运行时 —— 我们本就是 .NET；Native AOT 另议

（JSX 输入与 Tailwind 原子类经 `js/papersharp` 层已支持，构建期预处理成 HTML+CSS 后喂给引擎。）

## 一页速览

| 里程碑 | 主题 | 状态 | 触发架构变更？ |
|---|---|---|---|
| M1 | 表格+分页（跨页续表/rowspan/break/中文页码） | ✅ | 否 |
| M2 | flex-wrap/float/Grid 子集/定位补全 | ✅ | 否 |
| M3 | vertical-align/balance/text-fit/conic/blend/mask/squircle/svg | ✅ | 否 |
| M4 | JPEG/WebP/ICO/SVG、Google Fonts、var/calc | ✅ | 否 |
| M5 | 书签/链接/metadata ✅ · Tagged/PDF-A/附件 ❌ | 🟡 | 否（增量尾部已解） |
| **M6** | **层叠性能：超线性已消（物化样式表集合）· 自研替换降级为可选** | **🟡 进行中** | **暂否**（依赖收敛才触发） |
| M7 | Arabic/bidi/emoji（HarfBuzz shaping + 字素 + 字形回退） | ✅ | 是（引 SkiaSharp.HarfBuzz） |
| M8 | 伪元素 `::before/::after` + 计数器 / `background-clip: text` / `backdrop-filter` | ✅ | 否 |
| M9 | `@keyframes`/`animation` 确定性取帧（非 transform 属性） | ✅ | 否 |
| — | 并发安全化（RenderScope 每请求隔离） | ✅ | 否 |
