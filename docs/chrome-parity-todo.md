# Chrome 对齐 · 待修复清单

> 目标：PaperSharp 的渲染结果与 Chrome 一致。本文件是**唯一**的待修复台账——每条都带 Chrome 真值、引擎现状、取证方式与验收口径；修完直接划掉（`~~~~` + 提交号），不另开分支文档。
>
> 分级口径：**爆炸半径 × 可逆性**。P0 = 真实页面上肉眼可见且会算错外观/几何；P1 = 特定写法下丢内容或丢装饰；P2 = 观测盲区与亚像素残差。

## 本轮基线（2026-09-28，`bash tools/sweep.sh` @ 1284px）

24 个样例可测：**布局几何基本对齐**——合计漂移盒 31 / 2300+，最坏 2.1px，全部集中在 m7/inline/table/challenge。`login-page` **0 漂移**，但页面上有肉眼可见的绘制错误（见 P0-1）——说明现有对拍通道只量盒矩形，绘制层完全无门（见 P1-4）。

---

## P0-1 · 伪元素声明漏到宿主元素（`border-radius` 等 30+ 属性）—— ✅ 已修（波次1）

**症状**：`assets/login-page.html` 顶部横幅（`.hero`，CSS 里**没写** `border-radius`）被画成四角半径 64px 的大圆角胶囊；Chrome 里 `.hero` 是直角矩形，只有贴着卡片的那两个上角被 `.card{border-radius:24px;overflow:hidden}` 裁成 24px。

**实测**（截图与引擎 2x 光栅同一量法，逐行扫蓝区左边界求弧长）：

| | Chrome | PaperSharp |
|---|---|---|
| 左上角半径 | 24 CSS px（≈48 设备 px 弧） | ≈57 CSS px（弧 115 设备 px，被卡片 24px 裁剪切掉角尖后的可见值） |
| 左下/右下角 | 直角 | 同样 64px 圆角 |
| 盒几何 | — | 引擎 0 漂移（`boxdiff` 看不见此错） |

**根因**：`Css/StyleResolver.cs` 的「原始 CSS 兜底表」`ScanUnsupported`（约 231–255 行）按**选择器末段简单 token** 建键，剥键用的正则是 `[:\[].*$`——从第一个冒号起全剥，于是 `.hero::after` 与 `.hero` 归并成同一个键 `.hero`。`ReadStyle` 里 `border-radius` 这条走 `Lookup("border-radius", element, "", ctx)`（约 1612 行，刻意传空 `current` 强制查表），就把伪元素的 `64px` 读到了宿主身上。同一条规则里的 `box-shadow: inset 0 0 0 1px rgba(255,255,255,.45)` 也一并漏了过去（引擎给 `.hero` 画了一圈 Chrome 没有的白色内描边）。

**波及面**：`UnsupportedProps`（约 96–107 行）里全部 30+ 属性都可能漏，已用最小样例逐个证实会漏到宿主的有：`border-radius`、`box-shadow`、`background`/`background-image`（渐变）、`clip-path`、`mix-blend-mode`、`background-clip`。同表还挂着 `overflow`、`flex`、`vertical-align`、`align-*`/`justify-*`/`place-*`、`filter`、`corner-shape`、`counter-*`、`animation`、`font-variant*`、`text-*`——同一机理，未逐一取证但同源。

**触发条件**（决定这不是长尾）：伪元素规则的末复合选择器是**裸** `.class` / `#id` / `tag`。写成 `.a.b::after`、`.a:hover::after` 的不会漏（键变成 `.a.b` 命不中）——即漏不漏取决于作者有没有再叠一层伪类，纯装饰件最常见的 `.hero::after` 恰好必漏。

**修复方向**：兜底表建键时把伪元素标记单独存一列（`::before`/`::after`/`::placeholder`/`::marker` 与宿主互不同名），宿主层叠只查无伪元素的那份；伪元素层叠只查自己那份。顺带把「同一条规则里前一个属性值覆盖后一个」的粗粒度（`Regex.Match` 取首个命中）一并收紧。

**验收**：① `login-page` 的 `.hero` 计算样式 `border-radius=0`、`Shadows` 为空，蓝区左上角弧实测 48±2 设备 px、左下角直角；② 新增受跟踪探针 `assets/_probe-pseudoleak.html`（`.a::after{border-radius:40px;box-shadow:...;background:linear-gradient(...)}` + 裸 `.a` 宿主），配 xunit 断言宿主四属性全为初值；③ `tools/sweep.sh` 与既有金标零回归。

---

## P0-2 · 自定义属性从伪元素漏到**全页其它元素** —— ✅ 已修（波次1）

**症状 / 实测**：`.a::after{--pad:30px}` 与 `.b{padding:var(--pad,0px)}` 同页时，引擎里 `.b` 的宽度从 400 变成 340（吃到 30px 内边距）；Chrome 里 `--pad` 只存在于 `.a` 的伪元素上，`.b` 取不到 → 回退 `0px`，宽度 400。

**根因**：`ScanVars`（约 300–321 行）用的 `SelectorScopeKey`（约 324–333 行）与 P0-1 是**同一条 `[:\[]` 剥离正则**，键退化成 `.a`；而 `ExpandVars` 的查找是「沿祖先链按 class/id/tag 命中」，于是伪元素上声明的变量被当成宿主乃至后代可见。

**为什么单独一条**：P0-1 只污染宿主自身的外观；这条会污染**任意引用该变量的兄弟/后代**的盒几何——是布局级错误，且设计系统里 `::after` 上挂 `--size`/--`color` 的写法不少见。

**修复方向**：与 P0-1 同一个键方案一起改（两处共用 `SelectorScopeKey`，一次改对）。注意 CSS 真实语义是**伪元素继承宿主的变量**（宿主 `.a{--pad:5px}` 时 `::after` 可见），反向不可见——改完要按这条立断言。

**验收**：上述最小样例 `.b` 宽 400；`.a{--pad:5px}` + `.a::after{margin-left:var(--pad)}` 的伪元素仍拿到 5px。

---

## P1-3 · 绝对定位的装饰伪元素整块不出现 —— ✅ 已修（波次2，根因与初判不同）

**症状**：`login-page` 的 `.hero::before`（260×260、`rgba(255,255,255,.35)`、`rotate(24deg)`）与 `.hero::after`（320×320 白色渐变 + `rotate(-18deg)`）在 Chrome 里是横幅右侧两块可见的浅色装饰；引擎整页光栅里**一块都没有**（盒树里 `header` 只有 `h1`、`p` 两个孩子）。

**根因（实测修正，与初判不同）**：不是「`content:""` 被当成无内容」。AngleSharp 的 computed 分得开：未写 `content` → 空串，写了 `content:""` → 两字符的 `""""`，`PseudoContent.Parse` 据此已建出 `runs=[Literal:""]`。真凶是 **伪元素的 display 归一发生在块级化之后**：`ReadStyle` 末尾按规范把 `position:absolute/fixed` 的 display 块级化（真元素靠它才成盒，见 fluent-docs 滑杆拇指），可 `BuildPseudoGen` 在那之后又按「作者有没有显式写 display」把伪元素打回 `inline`（AngleSharp 对伪元素恒注入 `display:block`，不可信，故必须问作者）。空内容的行内伪元素在 `Materialize` 里 `InlinePieces.Count == 0 → return`，整块装饰就这么没了。修法：抽出 `Blockified(display)` 单一口径，伪元素归一之后再走一次。

**修后实测**：`login-page` 的 `header` 盒树多出 `<> 260×260 R=44` 与 `<> 320×320 R=64` 两个块盒（半径各归各的，正是 P0-1 修完后的样子），2x 光栅上横幅右侧两块装饰出现，差异包围盒 x1992–2449 / y120–515、15742 像素；对照组 `pricing-mid`/`m8`/`table`/`report` 逐字节不变。

**依赖项**：`transform: rotate()` 引擎未实现（ROADMAP 已记），故本条补齐后这两块装饰的**角度**仍与 Chrome 有差；本条只验收「装饰件出现且尺寸/落位正确」，旋转跟随 `transform`。

**验收**：`content:"";position:absolute;width:50px;height:50px;background:#f00` 的 `.a::after` 在引擎盒树里成盒并画出 50×50 红块（`PseudoScopeTests.Absolute_empty_content_pseudo_paints_its_box` 逐像素钉住），且非绝对定位的空内容行内伪元素仍不成盒（`Inline_empty_content_pseudo_still_does_not_box` 反向钉住，防扩面）。

---

## P1-9 · 多类名伪元素选择器被伪元素闸门整条挡掉（波次2 中发现，未动）

**症状**：`login-page` 的选中页签蓝条 `.tab.active::after{content:"";position:absolute;left:50%;bottom:0;width:104px;height:4px;border-radius:2px;background:#1a5cf0}` 在 Chrome 里画得出来，引擎里**整条伪元素规则压根没进 `BuildPseudoGen`**（盒树里 `.tab` 无伪元素子盒），蓝条缺失。

**根因**：`ScanPseudoDisplay` 建 `ctx.PseudoScope` 时把末段冒号后剥掉 → 存的是 **`.tab.active`**（多类名整串）；而 `PseudoMayMatch` 的候选是按元素的 class 列表逐个拼的 **`.tab`** / **`.active`** → 两边永不同名 → 闸门按「这个元素不可能有伪元素」把它跳过了。同一个 token 口径也用于 `LookupPseudoMap`（伪元素显式 display / `::placeholder` 颜色 / `::marker`），所以带叠加类名的伪元素规则整体处于「看不见」状态。

**为什么没跟着波次2 一起改**：闸门是**性能**闸门（每元素多跑一轮层叠实测 ~5ms），放宽它要走「多类名逐个登记」的超集路线——对闸门是安全的（宁可多放行），但 `PseudoDisplay` 那张表不能同样放宽（会把 A 规则的 display 发给 B 元素，正是 P0-1 那类串味）。要改就得把「闸门候选」与「取值键」拆成两套匹配，属独立一波。

**验收（修时）**：上述蓝条在引擎里出现且落位/尺寸与 Chrome 一致；`PseudoScope` 命中数不劣化（跑 `dotnet run -- pgstyle` 对账层叠次数）。

---

## P1-4 · 对拍通道只量盒矩形，绘制层没有任何门

**症状**：本轮 P0-1 的错误在 `tools/sweep.sh` 上表现为 `login-page 引擎 33 盒 / 浏览器 33 矩形 · 漂移 0`——几何全绿、页面明显画错。圆角、阴影、渐变、blend、伪元素装饰、边框描边这些「盒矩形不变但像素变了」的类别，现在**全靠肉眼**。

**现状盘点**：`tools/boxdiff.mjs`（几何对拍）+ `dotnet run -- pixdiff <name> <browser2x.png>`（像素重影，需人工先出浏览器截图）+ `tests/PaperSharp.Tests/goldens`（自比金标，不是 Chrome 真值）。缺的是「一条命令跑全样例的 Chrome 像素基线」。

**修复方向**：把 `boxdiff.mjs --dsf 2 --shot` 的截图与 `pixdiff` 串成 `tools/pixelsweep.sh`，逐样例出「差异像素占比 + 差异包围盒」，超阈值即红；差异包围盒喂给现有 `tools/crop.ps1` 自动出三联图（引擎 / Chrome / 重影）。金标只当回归门，Chrome 截图才是真值源。

**验收**：新脚本能在不改代码的前提下把 P0-1 报成红（`login-page` 差异占比 > 阈值，包围盒落在横幅左/下角），且修完转绿。

---

## P1-7 · 逗号选择器组只索引末段，前面各段整条丢失（波次1 刻意未动）

原始 CSS 兜底表（`ScanUnsupported`）与自定义属性索引（`ScanVars` 之外的 `Spec`/`Lookup` 那条路）对 `.a, .b { border-radius: 9px }` 这种分组选择器**只取整串的末段**建键，于是 `.a` 拿不到值。波次1 试过顺手改成逐段建键，实测会把 26 个样例里 9 个的像素基线掀了（改动面与伪元素无关，属另一条独立轴），为守住「一波一因」已回退。现状由 `PseudoScopeTests.Grouped_selector_pseudo_part_does_not_leak_to_other_part` 钉住，修它时要连同金标一起重出。

## P1-8 · 宿主的自定义属性喂不到伪元素的 margin/padding 上

`.a{--pad:5px} .a::after{margin-left:var(--pad)}`：Chrome 里伪元素继承宿主的变量 → 5px；引擎里 `margin`/`padding` 走的是 `AuthorBoxSpacing.ResolvePseudo` 那条作者源索引，它不展开 `var()`（也不认伪元素作用域键）→ 0。波次1 把 `border-radius` 这类走 `Lookup` 的属性接通了（`PseudoScopeTests.Host_custom_property_still_visible_to_pseudo` 用 border-radius 断言），margin/padding 这一对角仍待接。

## P2-5 · 两个样例在浏览器侧压根量不到（观测盲区）

`fluent-docs`、`framer-motion` 跑 `boxdiff` 报 `Error: {"code":-32000,"message":"Execution context was destroyed."}`（Edge 无头，CDP 页面被导航/脚本搞挂）→ 这两页从未进入对拍统计，等于没有真值。要先定位是页面里的脚本/重定向还是 `boxdiff` 的等待策略，必要时给这两个样例走 `--virtual-time-budget` 或禁脚本。

## P2-6 · 亚像素盒漂移残差（几何侧，非本轮新发现但仍未清）

| 样例 | 漂移盒 | 最坏 | 备注 |
|---|---|---|---|
| m7 | 14 / 41 | 2.1px | 全样例里最大，均值=最坏说明是同一类系统性偏（疑似某类行盒/字素度量），优先挖 |
| inline | 3 / 39 | 0.8px | 行内盒 |
| challenge | 10 / 257 | 0.6px | 亚像素 |
| table | 4 / 66 | 0.5px | 表格边框合并处 |

阈值 `--tol 0.5` 之下仍有 0.5–0.6px 的贴边项，别把它们当噪声划掉——m7 那 2.1px 是同一根因的放大版。

---

## 取证脚本与复现约定

- 本轮所有数值来自：逐行扫描截图蓝区（`B−R ≥ 15 且 B > 200`）左右边界求弧长，2x 设备 px ÷ 实测比例 1.986 折 CSS px；引擎侧同法量 `dotnet run -- html login-page` 的 2x 光栅。
- 临时探针工程落在 `.scratch/radius/probe/`（仓库外，不随分发）——**按根 `AGENTS.md` 约定，P0-1/P0-2/P1-3 修之前先把最小复现固化成受跟踪的 `assets/_probe-*.html` + xunit 断言**，否则下次还得重摸。
- 复跑几何基线：`bash tools/sweep.sh`（26 行，2 行失败即 P2-5）。
