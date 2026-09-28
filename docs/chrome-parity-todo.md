# Chrome 对齐 · 待修复清单

> 目标：PaperSharp 的渲染结果与 Chrome 一致。本文件是**唯一**的待修复台账——每条都带 Chrome 真值、引擎现状、取证方式与验收口径；修完直接划掉（`~~~~` + 提交号），不另开分支文档。
>
> 分级口径：**爆炸半径 × 可逆性**。P0 = 真实页面上肉眼可见且会算错外观/几何；P1 = 特定写法下丢内容或丢装饰；P2 = 观测盲区与亚像素残差。

## 本轮基线（2026-09-28，`bash tools/sweep.sh` @ 1284px）

24 个样例可测：**布局几何基本对齐**——合计漂移盒 31 / 2300+，最坏 2.1px，全部集中在 m7/inline/table/challenge。`login-page` **0 漂移**，但页面上有肉眼可见的绘制错误（见 P0-1）——说明现有对拍通道只量盒矩形，绘制层完全无门（见 P1-4）。

**同日三波修复后的账**：P0-1 `cd54e4d`、P1-3 `faa1264`、P1-9 `4c8e7bd` 已落（三处都是 `login-page` 一页引出的独立根因）；三波累计只改动 `login-page` / `pricing-page` / `pricing-top` 三张 2x 光栅，其余 23 张逐字节不变。绘制层的门（P1-4）已补成 `tools/pixelsweep.sh`，此后每样例给「2x 强差 % / 1x 可见重影 % / 重影评分」一行硬指标，不再靠肉眼截图对比。

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

## P1-9 · 多类名 / 带 transform 的伪元素规则整条消失 —— ✅ 已修（波次3，实为两个独立根因）

**症状**：`login-page` 选中页签那条蓝条 `.tab.active::after{content:"";position:absolute;left:50%;bottom:0;transform:translateX(-50%);width:104px;height:4px;border-radius:2px;background:#1a5cf0}` 在 Chrome 里画得出来，引擎里整条规则当它不存在。

**根因 A（闸门 token 不同名）**：`ScanPseudoDisplay` 建 `ctx.PseudoScope` 存的是末段整串 **`.tab.active`**，而 `PseudoMayMatch` 的探测式是按元素 class 列表逐个拼的 **`.tab`** / **`.active`** → 永不同名 → 闸门按「这个元素不可能有伪元素」直接跳过（闸门确实生效：`StyleResolver.cs:922` `if (ctx.HasPseudoRules && PseudoMayMatch(...))`）。同一个 token 口径也用于 `LookupPseudoMap`（伪元素显式 display、`::placeholder` 颜色、`::marker`），所以带叠加类名的伪元素规则整体处于「看不见」状态。
修法：闸门候选摊成末段复合里的**每个**简单 token（tag / 每个 `.class` / `#id`）——闸门只做「宁可多放行」的超集筛选；**取值键仍按整串存**，不会因此把 A 规则的 display 发给 B 元素（那正是 P0-1 那类串味）。

**根因 B（AngleSharp computed 抛异常把伪元素判成「没写 content」）**：`BuildPseudoGen.Content()` 用 `ComputeDeclarations(el, "::after")` 取 content，而 AngleSharp 在解析该伪元素规则的 computed 时，值里带 `transform: translateX(-50%)` **必抛 NullReferenceException**（`transform:none`、未知属性、只写 content 都不抛，declared 那条路也不抛——实测钉住）。旧写法 `catch { return ""; }` 把异常咽成空串，于是「一条无关声明」判掉「整个伪元素」。
修法：computed 抛了退到 declared（同样做真选择器匹配，只是取作者原样值，而 content 本来就由 `PseudoContent.Parse` 自己解析）。

**修后实测**：蓝条画出来了（2x 光栅上 207×7 设备 px = 104×4 CSS px，落在页签下方）。

**残留（另立一条，见 P1-10）**：蓝条的**水平落位**仍与 Chrome 差半个条宽——`left:50%` 生效而 `transform: translateX(-50%)` 未实现，条子右偏 52px。

---

## P1-10 · `transform` 未实现，凡靠它定位/旋转的装饰都偏位

`translateX(-50%)`（居中）、`rotate(24deg)` / `rotate(-18deg)`（login-page 横幅那两块圆角装饰件的角度）在引擎里一律忽略：盒出现、尺寸对，但角度与位移与 Chrome 有差。ROADMAP 早已把 `transform` 列为未做项（依赖它的动画停在基线态），本条只是把它在**静态装饰**上的可见后果点名归档：P1-3 / P1-9 修完后，剩下的差异全部落在这一条上。

---

## P1-4 · 对拍通道只量盒矩形，绘制层没有任何门 —— ✅ 已补门（波次4：`tools/pixelsweep.sh`）

**症状**：`tools/sweep.sh` 上 `login-page 引擎 33 盒 / 浏览器 33 矩形 · 漂移 0`——几何全绿、页面明显画错。圆角、阴影、渐变、blend、伪元素装饰、边框描边这些「盒矩形不变但像素变了」的类别，此前**全靠肉眼**。

**现状盘点（修前）**：`tools/boxdiff.mjs`（几何对拍）+ `dotnet run -- pixdiff <name> <browser2x.png>`（像素重影，但要人工先出浏览器截图）+ `tests/PaperSharp.Tests/goldens`（自比金标，不是 Chrome 真值）。缺的是「一条命令跑全样例的 Chrome 像素基线」。

**已交付**：`bash tools/pixelsweep.sh [样例…]` 把 boxes → 无头浏览器 2x 截图 → pixdiff 串成一行硬指标（2x 强差 % / 1x 可见重影 % / 真位移带条数 / 判定），逐样例原始输出与差异三联图落在 `.scratch/pixelsweep/`。

**2026-09-28 首份全样例基线（1284px，三波修复之后）**：26 样例，2x 强差均值 **10.5%**。最差五张：`m7 24.4% · m9 23.7% · long 21.2% · login-page 16.2% · pricing-mid 15.4%`；最干净 `fontvs 0.3% · flex 1.5% · m2 1.4% · invoice 1.8%`。

**这份数字怎么读（关键）**：`位移带 X/Y` 是**对齐后仍可修的整段位移**，绝大多数样例是 `0/N`——也就是说全局对齐之后，残余差异的主要成分不是几何错位，而是**字形光栅化口径**（hinting / 抗锯齿 / 次像素），pixdiff 自己把它单列成「光栅差 / 半像素噪声」。这直接给「重影」类问题定了性：**继续调盒几何不会再降这个数**，要降得去对字形栅格化策略。例外是 `challenge 6/122`、`m2 6/34`、`pricing-page 4/130`、`m9 2/13`、`pct 2/22`——这几张有真位移带，属可修目标。

**最差三张的定性（2026-09-28 逐张看过差异图并量过）**：

- `long 21.2%`：把引擎与浏览器两层按 90px 高的纵向窗口逐窗搜最佳整数平移，**y=60→960 全程偏移 +0** —— 垂直方向没有累积漂移，26 行列表的行高/行距是对的。差异全在**行内水平方向**：越往行尾越明显，是逐字符推进宽度（advance/kerning）与栅格化的差，不是布局。
- `m7 24.4%`：红色强差**集中在 RTL 段**（阿拉伯语/希伯来语整行、含 bidi 混排的表格右列、emoji/ZWJ 那一行），同页的拉丁与 CJK 行只剩轮廓级弱差。这张的大头是 **bidi 落位**，属 M7 已知边界（嵌套双向、shaped run 的 letter-spacing），不是栅格化。
- `m9 23.7%`：`位移带 2/13` 是全场真位移带占比最高的一张，先看那两条带再谈栅格化。

**已知把数字顶高的非缺陷项**：`login-page` 的二维码是 `<canvas>` + 页内 `<script>` 画的，引擎不执行 JS → 那块区域必然 100% 差（约占画面 14%）。要么给样例换静态 `<img>`，要么在指标里把 `canvas` 区域剔除，否则这张的百分比不可比。

**验收**：新门能在不改代码的前提下把已知绘制错报成红（首跑即把 login-page 报 `16.2% / 严重`，与三波修复前的肉眼结论一致），修完转绿。


---

## P0-3 · 宿主的盒尺寸声明漏给伪元素（AngleSharp 第二条漏值路）—— ✅ 已修（波次6）

**症状**（Playground 现场，`pricing-page`/`pricing-top`）：四张定价卡里各有一颗撑满整卡的「蛋形」轮廓，Chrome 里只是卡片右上角一小段圆环弧线。

**根因**：波次2 让绝对定位的装饰伪元素开始成盒之后才暴露出来的下一层——**AngleSharp 的伪元素视图会把宿主的盒尺寸声明原样漏进 computed 与 declared 两条路**。实测（`.plan{min-height:574px}` + `.plan::before{width:200px;height:200px;border:26px}`）：伪元素的 `min-height` 在两条读取路上都是 `574px`，而作者从没为 `::before` 写过它；`min-width`/`max-*` 不漏，`width`/`height`/`border-*` 取的是伪元素自己的值。`min-height`/`max-height`/`width`… 按 CSS 永不继承 → 圆环被撑成 148×522 的蛋。

**改法**：`ScanPseudoDisplay` 顺带登记「伪元素规则里作者亲自写过哪条尺寸属性」（`ctx.PseudoSizing`），`ReadStyle` 里六处尺寸取值改走 `OwnSize`。判漏用**两条同时成立**：作者没为该伪元素写过 **且** 取值与宿主逐字相同（漏值的签名）。只写「索引里没有」会把 token 索引没覆盖到的真声明（如 `ul.tags li::after`）误杀——这条边界由 `Pseudo_own_min_height_is_kept` 反向钉住。

**实测**：`pricing-top` 2x 强差 **12.3% → 7.4%**、`pricing-page` **14.4% → 12.0%**，其余 24 张样例逐字节不变；卡片自己的 `min-height:574px` 未被误伤（`Host_box_sizing_does_not_leak_into_pseudo` 双向断言）。

**同类风险（未修，待挖）**：这条「宿主→伪元素」漏值路既然在 `min-height` 上成立，其它非继承属性也可能有各自的漏法（margin/padding 早先已单独用作者源桶裁决过，见 `StyleResolver.cs` 该处注释）。`PseudoScopeTests` 目前只钉住尺寸与 margin/padding 两族，其余属性按样例撞见再补。

---

## P1-7 · 逗号选择器组只索引末段，前面各段整条丢失（波次1 刻意未动）

原始 CSS 兜底表（`ScanUnsupported`）与自定义属性索引（`ScanVars` 之外的 `Spec`/`Lookup` 那条路）对 `.a, .b { border-radius: 9px }` 这种分组选择器**只取整串的末段**建键，于是 `.a` 拿不到值。波次1 试过顺手改成逐段建键，实测会把 26 个样例里 9 个的像素基线掀了（改动面与伪元素无关，属另一条独立轴），为守住「一波一因」已回退。现状由 `PseudoScopeTests.Grouped_selector_pseudo_part_does_not_leak_to_other_part` 钉住，修它时要连同金标一起重出。

## P1-8 · 宿主的自定义属性喂不到伪元素的 margin/padding 上 —— ✅ 已修（波次5）

**症状**：`.a{--pad:5px} .a::after{margin-left:var(--pad,0px)}`：Chrome 里伪元素继承宿主的变量 → 5px；引擎里那条边吃初值 0。

**根因（实测修正）**：不是「作者源索引不认伪元素作用域键」。`AuthorBoxSpacing.Parse` 在**建索引时就把值里含 `var()` 的声明整条跳过**（`if (val.Length == 0 || val.Contains("var(")) continue;`），于是这条边在作者源桶里根本不存在；而伪元素的八条边只能由这张桶裁决（AngleSharp 的 computed/declared 两条路都会把宿主声明漏进伪元素结果，见该处注释）→ 结果恒为初值 0。

**改法**：桶里照收带 `var()` 的值，展开交给消费侧——宿主走 `AuthorEdge` → `ExpandVars(mv, element, ctx)`，伪元素走 `PseudoEdge` → `ExpandVars(pv, element, ctx, pseudo)`（波次1 已把 pseudo 上下文接进 FindVar：先查伪元素自己的作用域键，再按继承回落宿主链）。变量查不到又没写回退值时留下 `var(--x)` 原串，`ParseSize` 判非法 → 0，与 Chrome 的 invalid-at-computed-value-time 一致。

**影响面**：`InlineUniqueVars` 那条预内联已把「全局唯一定值」的变量折成字面值，所以只有**同名变量多作用域取值**的 margin/padding 才会经过这条路——首版全样例像素对拍见下。

## P2-7 · `challenge` 整页逐块下坠：Chrome 把长度折到 1/64 px（Blink LayoutUnit），引擎保留 float 精度

**症状**（`boxdiff challenge --tol 0.05` 实测）：229 个盒的 `dy` 沿文档序**单调累积**——`div[2]` −0.053 → `p[2]` −0.065 → `h2[2]` −0.074 → … → 页尾 −0.226；而 `dh`（盒高）几乎全为 0。也就是说每个块自己的尺寸是对的，**块间距**每跳一个兄弟多约 0.011–0.014px，攒到页尾差出 0.226px，在像素上表现为若干条 1px 边翻转（该样例 `位移带 6/122`）。

**机制已钉死**：该样例的间距全是物理单位（`margin:3.5mm 0 2mm` 等）。逐条对账 Chrome 的取值：

| 作者值 | 引擎（float） | Chrome | `floor(px×64)/64` |
|---|---|---|---|
| 2.5mm | 9.4488 | 9.4375 | 9.4375 ✓ |
| 3.5mm | 13.2284 | 13.2188 | 13.2188 ✓ |
| 2mm | 7.5591 | 7.5469 | 7.5469 ✓ |
| 1mm | 3.7795 | 3.7656 | 3.7656 ✓ |

四条全中 —— Chrome 把长度折到 Blink 的 `LayoutUnit` 网格（1 px = 1/64），引擎走的是 float 全精度。

**两种朴素全局折法都已实测否决**（在 `CssValueParser.ToPixels` 的 5 个出口统一加 `SnapLayoutUnit`，2x 强差 %）：

| 样例 | 基线 | floor（朝零截断） | round（四舍五入） |
|---|---|---|---|
| challenge | 12.5 | **12.2**（位移带 6/122 → **1/108**） | 12.8（位移带反升到 14） |
| m7 | 24.4 | 24.1 | 24.4 |
| m1 | 12.4 | 15.7 ✗ | 12.6 |
| framer-motion | 6.8 | 10.9 ✗ | 10.3 ✗ |
| fluent-docs | 14.5 | 22.7 ✗ | 22.6 ✗ |

结论：**不能在全局长度折算口子上统一 snap**。floor 能把 challenge 的累积漂移基本消掉（位移带 6→1），代价是 fluent-docs +8.2、framer-motion +4.1、m1 +3.3；round 保住 m1 却把 challenge 的位移带顶到 14。Chrome 的 LayoutUnit 是**在具体换算点**（margin/border-box 宽度等）折的，不是每个长度都折，且不同属性用的换算站点不同。

**下一步的正确形状**（留给修的人，别重复试全局版）：只在「块间距的生成处」折——即 margin 折叠结果与 `AuthorBoxSpacing` 折出的 px 值，而不是 `ToPixels` 通用出口；改完用 `node tools/boxdiff.mjs challenge --tol 0.05 --json` 看累积 `dy` 是否归零，再用 `tools/pixelsweep.sh` 逐张确认 fluent-docs/framer-motion/m1 不回退。

---

## P2-8 · `m9` / `m2` 的「位移带」是测量口径问题，不是布局 bug（已给门加两面旗）

- `m9 2/13`：浏览器截图含视口留白（`body{min-height:100vh}` 一类），文档高 972 CSS px vs 引擎 645.5 —— 比的是两份不同高度的文档；灰带上下边在 2x 下只差 1 设备 px，盒几何 `boxdiff` 全绿。
- `m2 6/34`：全局整数平移没收敛（`SAD 72%`，引擎 body 原点 (0,0) vs 浏览器 (84,1)），残余的整体 1px 被逐条边重复计成 6 条「位移带」——六条全是 `dy-1`，正是那个残差。

`tools/pixelsweep.sh` 现在对每张额外打两面旗：`高度不可比(引擎vs浏览器)` 与 `对齐未收敛(SADx%)`；带旗的位移带数**不当 bug 追**。全样例重跑后新暴露 6 张带旗的（table / invoice / report / pct / m7 / m1 等），此前它们的百分比都是不可比的。

---


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
