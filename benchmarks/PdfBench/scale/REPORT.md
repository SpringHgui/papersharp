# HTML→PDF 同类工具 · 规模扫描报告

同一份语料（`corpus/*.html`，五条规模轴 A–E + 锚点 Z + 退化宽度探针 X，每档逐字节相同）喂给所有引擎，比的是**文档变大之后谁先垮**。warm=常驻进程（服务端形态），cold=每次新进程（CLI 形态），warmvary=常驻但每次换文档。

## 0. 速览

| 引擎 | 赛道 | A1 warm(ms) | A100 warm(ms) | A 指数 | B 指数 | C 指数 | D 指数 | E 指数 | RSS 峰值(MB) |
|---|---|---|---|---|---|---|---|---|---|
| papersharp | 自研 CSS 引擎 + Skia | 202 | 5354 | 0.69 | 0.85 | 2.73 | 0.76 | 0.12 | 490 |
| chrome-cli | Chromium（CLI 直出） | 1497 | 3510 | 0.17 | 0.48 | — | — | — | 2422 |
| playwright | Chromium（Playwright 包装） | 1618 | 2745 | 0.11 | 0.25 | — | — | — | 2173 |
| puppeteer | Chromium（Puppeteer 包装） | 1973 | 3490 | 0.12 | 0.37 | 0.47 | 0.08 | 0.00 | 2484 |
| weasyprint | WeasyPrint 独立排版引擎 | 241 | 16430 | 0.93 | 1.02 | 1.96 | 0.92 | 0.65 | 1579 |
| wkhtmltopdf | Qt WebKit | 325 | 2372 | 0.43 | 0.66 | 2.12 | 0.45 | 0.06 | 113 |

指数拟合口径：log-log 最小二乘，warm 优先（缺 warm 时用 warmvary、再用 cold；不同口径的行不会混进同一条拟合）。自变量：A/B/D/E 用名义档位，C/X 用元素数（见 README）。**固定成本 + 边际成本的线性拟合、两家曲线的交叉规模、吞吐与轮间离散度在 `out/summary.md`（`python summary.py` 重生成）。

## 轴 A · 富样式整页 × N

![A 延迟](charts/scale_A_wall.png)

**warm 口径**

| 规模 N | papersharp ms | playwright ms | puppeteer ms | weasyprint ms | papersharp ms / 页 | playwright ms / 页 | puppeteer ms / 页 | weasyprint ms / 页 | papersharp 实测页数 | playwright 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 202 | 1618 | 1973 | 241 | 202.4 | 1617.7 | 1973.0 | 241.1 | 1 | 1 | 1 | 2 |
| 3 | 403 | 1565 | 1961 | 477 | 134.2 | 521.7 | 653.8 | 158.8 | 3 | 3 | 3 | 5 |
| 10 | 717 | 1686 | 2144 | 1487 | 71.7 | 168.6 | 214.4 | 148.7 | 10 | 10 | 10 | 18 |
| 30 | 1707 | 2012 | 2538 | 4333 | 56.9 | 67.1 | 84.6 | 144.4 | 30 | 30 | 30 | 56 |
| 100 | 5354 | 2745 | 3490 | 16430 | 53.5 | 27.5 | 34.9 | 164.3 | 100 | 100 | 100 | 187 |

**warmvary 口径**

| 规模 N | papersharp ms | playwright ms | puppeteer ms | weasyprint ms | papersharp ms / 页 | playwright ms / 页 | puppeteer ms / 页 | weasyprint ms / 页 | papersharp 实测页数 | playwright 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 10 | 662 | 1539 | 1945 | 1410 | 66.2 | 153.9 | 194.5 | 141.0 | 10 | 10 | 10 | 18 |
| 100 | 4949 | 2481 | 3265 | 14968 | 49.5 | 24.8 | 32.7 | 149.7 | 100 | 100 | 100 | 187 |

**cold 口径**

| 规模 N | papersharp ms | chrome-cli ms | playwright ms | puppeteer ms | weasyprint ms | wkhtmltopdf ms | papersharp ms / 页 | chrome-cli ms / 页 | playwright ms / 页 | puppeteer ms / 页 | weasyprint ms / 页 | wkhtmltopdf ms / 页 | papersharp 实测页数 | chrome-cli 实测页数 | playwright 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 | wkhtmltopdf 实测页数 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 1134 | 1497 | 2407 | 2863 | 891 | 325 | 1133.6 | 1496.5 | 2406.5 | 2863.1 | 891.5 | 324.8 | 1 | 1 | 1 | 1 | 2 | 1 |
| 3 | 1329 | 1543 | 2360 | 2826 | 1120 | 360 | 442.8 | 514.3 | 786.8 | 942.0 | 373.5 | 120.1 | 3 | 3 | 3 | 3 | 5 | 3 |
| 10 | 2220 | 1661 | 2467 | 2921 | 2138 | 526 | 222.0 | 166.1 | 246.7 | 292.1 | 213.8 | 52.6 | 10 | 10 | 10 | 10 | 18 | 10 |
| 30 | 4724 | 2088 | 2717 | 3306 | 4874 | 964 | 157.5 | 69.6 | 90.6 | 110.2 | 162.5 | 32.1 | 30 | 30 | 30 | 30 | 56 | 30 |
| 100 | 8030 | 3510 | 3438 | 4409 | 16001 | 2372 | 80.3 | 35.1 | 34.4 | 44.1 | 160.0 | 23.7 | 100 | 100 | 100 | 100 | 187 | 100 |

![A 内存](charts/scale_A_rss.png)
![A 边际](charts/scale_A_perunit.png)

## 轴 B · 单张长表 × 行 N

![B 延迟](charts/scale_B_wall.png)

**warm 口径**

| 规模 N | papersharp ms | playwright ms | puppeteer ms | weasyprint ms | papersharp ms / 千行 | playwright ms / 千行 | puppeteer ms / 千行 | weasyprint ms / 千行 | papersharp 实测页数 | playwright 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 360 | 1039 | 1481 | 595 | 3597.5 | 10392.5 | 14807.0 | 5948.5 | 3 | 3 | 3 | 3 |
| 500 | 1009 | 1076 | 1624 | 3004 | 2018.0 | 2151.1 | 3248.9 | 6007.2 | 15 | 15 | 15 | 15 |
| 2000 | 3658 | 1420 | 2421 | 12267 | 1829.1 | 709.9 | 1210.3 | 6133.4 | 59 | 58 | 58 | 59 |
| 8000 | 14971 | 3239 | 8205 | 52845 | 1871.4 | 404.8 | 1025.7 | 6605.6 | 236 | 229 | 229 | 236 |

**warmvary 口径**

| 规模 N | papersharp ms | playwright ms | puppeteer ms | weasyprint ms | papersharp ms / 千行 | playwright ms / 千行 | puppeteer ms / 千行 | weasyprint ms / 千行 | papersharp 实测页数 | playwright 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2000 | 3350 | 1322 | 2311 | 10282 | 1675.1 | 661.1 | 1155.7 | 5140.9 | 59 | 58 | 58 | 59 |

**cold 口径**

| 规模 N | papersharp ms | chrome-cli ms | playwright ms | puppeteer ms | weasyprint ms | wkhtmltopdf ms | papersharp ms / 千行 | chrome-cli ms / 千行 | playwright ms / 千行 | puppeteer ms / 千行 | weasyprint ms / 千行 | wkhtmltopdf ms / 千行 | papersharp 实测页数 | chrome-cli 实测页数 | playwright 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 | wkhtmltopdf 实测页数 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 1204 | 937 | 1775 | 2318 | 1329 | 328 | 12042.9 | 9373.5 | 17754.5 | 23181.2 | 13293.5 | 3280.4 | 3 | 3 | 3 | 3 | 3 | 3 |
| 500 | 2812 | 1200 | 1834 | 2551 | 3195 | 468 | 5624.5 | 2399.6 | 3667.2 | 5101.9 | 6389.7 | 936.0 | 15 | 15 | 15 | 15 | 15 | 15 |
| 2000 | 5746 | 2220 | 2115 | 3278 | 11105 | 1172 | 2872.9 | 1110.1 | 1057.4 | 1638.9 | 5552.3 | 585.8 | 59 | 58 | 58 | 58 | 59 | 58 |
| 8000 | 17060 | 7964 | 3761 | 9147 | 41535 | 6155 | 2132.5 | 995.5 | 470.1 | 1143.3 | 5191.8 | 769.4 | 236 | 229 | 210 | 229 | 236 | 232 |

![B 内存](charts/scale_B_rss.png)
![B 边际](charts/scale_B_perunit.png)

## 轴 C · 深嵌套 + 300 条规则（按元素数拟合）

![C 延迟](charts/scale_C_wall.png)

**warm 口径**

| 规模 N | papersharp ms | puppeteer ms | weasyprint ms | papersharp ms / 千元素 | puppeteer ms / 千元素 | weasyprint ms / 千元素 | papersharp 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 |
|---|---|---|---|---|---|---|---|---|---|
| 4011 | 1136 | 1438 | — | 283.3 | 358.4 | — | 1 | 4 | — |
| 4327 | 2268 | 1462 | 1584 | 524.1 | 337.8 | 366.1 | 1 | 11 | 11 |
| 5061 | 6219 | 1592 | 2226 | 1228.8 | 314.6 | 439.8 | 1 | 35 | 36 |
| 7207 | 6670 | 1884 | 4347 | 925.4 | 261.4 | 603.2 | 89 | 104 | 107 |

**cold 口径**

| 规模 N | papersharp ms | puppeteer ms | weasyprint ms | wkhtmltopdf ms | papersharp ms / 千元素 | puppeteer ms / 千元素 | weasyprint ms / 千元素 | wkhtmltopdf ms / 千元素 | papersharp 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 | wkhtmltopdf 实测页数 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 4011 | 3274 | 2109 | — | 342 | 816.2 | 525.8 | — | 85.3 | 1 | 4 | — | 4 |
| 4327 | 4549 | 2148 | 2067 | 396 | 1051.2 | 496.5 | 477.7 | 91.6 | 1 | 11 | 11 | 11 |
| 5061 | 8205 | 2288 | 2731 | 605 | 1621.3 | 452.1 | 539.6 | 119.6 | 1 | 35 | 36 | 35 |
| 7207 | 8795 | 2581 | 4480 | 1178 | 1220.3 | 358.1 | 621.6 | 163.5 | 89 | 104 | 107 | 104 |

![C 内存](charts/scale_C_rss.png)
![C 边际](charts/scale_C_perunit.png)

## 轴 D · CJK/Latin 混排长文本

![D 延迟](charts/scale_D_wall.png)

**warm 口径**

| 规模 N | papersharp ms | puppeteer ms | weasyprint ms | papersharp ms / 万字 | puppeteer ms / 万字 | weasyprint ms / 万字 | papersharp 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 |
|---|---|---|---|---|---|---|---|---|---|
| 10000 | 342 | 1622 | 188 | 342.4 | 1621.8 | 188.1 | 2 | 5 | 5 |
| 50000 | 867 | 1662 | 610 | 173.3 | 332.4 | 122.0 | 9 | 25 | 25 |
| 200000 | 2836 | 1856 | 2674 | 141.8 | 92.8 | 133.7 | 27 | 97 | 99 |
| 500000 | 6698 | 2266 | 6563 | 134.0 | 45.3 | 131.3 | 74 | 241 | 246 |

**warmvary 口径**

| 规模 N | papersharp ms | puppeteer ms | weasyprint ms | papersharp ms / 万字 | puppeteer ms / 万字 | weasyprint ms / 万字 | papersharp 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 |
|---|---|---|---|---|---|---|---|---|---|
| 200000 | 2459 | 1791 | 2112 | 123.0 | 89.6 | 105.6 | 27 | 97 | 99 |

**cold 口径**

| 规模 N | papersharp ms | puppeteer ms | weasyprint ms | wkhtmltopdf ms | papersharp ms / 万字 | puppeteer ms / 万字 | weasyprint ms / 万字 | wkhtmltopdf ms / 万字 | papersharp 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 | wkhtmltopdf 实测页数 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 10000 | 1128 | 2360 | 763 | 332 | 1128.4 | 2360.1 | 763.0 | 331.6 | 2 | 5 | 5 | 5 |
| 50000 | 2197 | 2419 | 1184 | 464 | 439.4 | 483.8 | 236.8 | 92.8 | 9 | 25 | 25 | 24 |
| 200000 | 4606 | 2592 | 2892 | 974 | 230.3 | 129.6 | 144.6 | 48.7 | 27 | 97 | 99 | 95 |
| 500000 | 8645 | 3095 | 6205 | 1995 | 172.9 | 61.9 | 124.1 | 39.9 | 74 | 241 | 246 | 237 |

![D 内存](charts/scale_D_rss.png)
![D 边际](charts/scale_D_perunit.png)

## 轴 E · 图片张数 × N

![E 延迟](charts/scale_E_wall.png)

**warm 口径**

| 规模 N | papersharp ms | puppeteer ms | weasyprint ms | papersharp ms / 十图 | puppeteer ms / 十图 | weasyprint ms / 十图 | papersharp 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 |
|---|---|---|---|---|---|---|---|---|---|
| 10 | 183 | 1162 | 71 | 183.1 | 1162.0 | 70.8 | 1 | 1 | 4 |
| 30 | 216 | 1173 | 110 | 72.0 | 391.1 | 36.5 | 1 | 1 | 10 |
| 100 | 148 | 1168 | 238 | 14.8 | 116.8 | 23.8 | 2 | 3 | 34 |
| 300 | 330 | 1185 | 639 | 11.0 | 39.5 | 21.3 | 2 | 9 | 100 |

**cold 口径**

| 规模 N | papersharp ms | puppeteer ms | weasyprint ms | wkhtmltopdf ms | papersharp ms / 十图 | puppeteer ms / 十图 | weasyprint ms / 十图 | wkhtmltopdf ms / 十图 | papersharp 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 | wkhtmltopdf 实测页数 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 10 | 880 | 1932 | 665 | 278 | 880.2 | 1932.3 | 665.0 | 277.9 | 1 | 1 | 4 | 1 |
| 30 | 904 | 1911 | 688 | 298 | 301.3 | 637.1 | 229.4 | 99.4 | 1 | 1 | 10 | 1 |
| 100 | 841 | 1949 | 819 | 299 | 84.1 | 194.9 | 81.9 | 29.9 | 2 | 3 | 34 | 3 |
| 300 | 1005 | 1966 | 1233 | 354 | 33.5 | 65.5 | 41.1 | 11.8 | 2 | 9 | 100 | 9 |

![E 内存](charts/scale_E_rss.png)
![E 边际](charts/scale_E_perunit.png)

## 轴 X · 退化宽度：每层 padding 累积（旧 C 语料）

![X 延迟](charts/scale_X_wall.png)

**warm 口径**

| 规模 N | papersharp ms | puppeteer ms | weasyprint ms | papersharp ms / 千元素 | puppeteer ms / 千元素 | weasyprint ms / 千元素 | papersharp 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 |
|---|---|---|---|---|---|---|---|---|---|
| 4011 | 1003 | 1618 | — | 250.0 | 403.4 | — | 13 | 111 | — |
| 4327 | 8530 | 1496 | 3244 | 1971.3 | 345.7 | 749.8 | 1 | 40 | 40 |
| 5061 | 5116 | 1610 | 3745 | 1011.0 | 318.2 | 740.0 | 50 | 52 | 54 |
| 7207 | 7787 | 1950 | 6334 | 1080.5 | 270.6 | 878.8 | 124 | 126 | 134 |

**cold 口径**

| 规模 N | papersharp ms | puppeteer ms | weasyprint ms | wkhtmltopdf ms | papersharp ms / 千元素 | puppeteer ms / 千元素 | weasyprint ms / 千元素 | wkhtmltopdf ms / 千元素 | papersharp 实测页数 | puppeteer 实测页数 | weasyprint 实测页数 | wkhtmltopdf 实测页数 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 4011 | 3441 | 2368 | — | 435 | 858.0 | 590.4 | — | 108.3 | 13 | 111 | — | 106 |
| 4327 | 11009 | 2268 | 3447 | 436 | 2544.3 | 524.1 | 796.7 | 100.8 | 1 | 40 | 40 | 32 |
| 5061 | 7651 | 2375 | 3843 | 656 | 1511.8 | 469.3 | 759.3 | 129.6 | 50 | 52 | 54 | 50 |
| 7207 | 10228 | 2675 | 6080 | 1283 | 1419.2 | 371.2 | 843.7 | 178.0 | 124 | 126 | 134 | 122 |

![X 内存](charts/scale_X_rss.png)
![X 边际](charts/scale_X_perunit.png)

## 轴 Z · 锚点：仓库既有样例

![Z 延迟](charts/scale_Z_wall.png)

**warm 口径**

| 规模 3=report | papersharp ms | puppeteer ms | papersharp ms / 页 | puppeteer ms / 页 | papersharp 实测页数 | puppeteer 实测页数 |
|---|---|---|---|---|---|---|
| 1 | 70 | 1446 | 69.9 | 1445.7 | 1 | 1 |
| 2 | 494 | 2710 | 164.6 | 903.4 | 3 | 3 |
| 3 | 162 | 1871 | 81.1 | 935.6 | 2 | 2 |

**cold 口径**

| 规模 3=report | papersharp ms | puppeteer ms | wkhtmltopdf ms | papersharp ms / 页 | puppeteer ms / 页 | wkhtmltopdf ms / 页 | papersharp 实测页数 | puppeteer 实测页数 | wkhtmltopdf 实测页数 |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 810 | 2247 | 300 | 810.4 | 2247.4 | 300.3 | 1 | 1 | 1 |
| 2 | 1652 | 3488 | 382 | 550.6 | 1162.6 | 127.5 | 3 | 3 | 3 |
| 3 | 1002 | 2674 | 301 | 501.2 | 1337.2 | 150.7 | 2 | 2 | 2 |

![Z 内存](charts/scale_Z_rss.png)
![Z 边际](charts/scale_Z_perunit.png)

## 口径体检（横向比之前先看这页）

| 引擎 | 口径 | 档 | 状态 | n_ok/n | 页数 | MediaBox(pt) | 首个错误 |
|---|---|---|---|---|---|---|---|
| weasyprint | warm | X300 | fail | 0/1 | — | —x— |  |
| weasyprint | cold | X300 | fail | 0/1 | — | —x— |  |

**页面尺寸一致性**（吃同一份 @page 的证据）：

- `chrome-cli`：594.96×841.92 pt
- `papersharp`：595.00×842.00 pt
- `playwright`：594.96×841.92 pt
- `puppeteer`：594.96×841.92 pt
- `puppeteer`：595.92×841.92 pt
- `weasyprint`：595.28×841.89 pt
- `wkhtmltopdf`：595.00×842.00 pt

![slopes](charts/slopes.png)


## 完整性折扣（读上面三条曲线之前先看这页）

参照 = `chrome-cli`（无头 Chrome 直出）。判据是产物里抽出的字符数、图像 XObject 数、页数、末页字符数；产物都留在 `out/artifacts/`。

| 引擎 | ok | 少画 | 少画图像 | 页数不一致 | 尾部截断 | no-pdf | 档位合计 |
|---|---|---|---|---|---|---|---|
| `chrome-cli` | 24 | 0 | 0 | 0 | 0 | 0 | 24 |
| `papersharp` | 10 | 8 | 2 | 8 | 0 | 0 | 28 |
| `playwright` | 23 | 0 | 0 | 1 | 0 | 0 | 24 |
| `puppeteer` | 28 | 0 | 0 | 0 | 0 | 0 | 28 |
| `weasyprint` | 8 | 2 | 1 | 15 | 0 | 2 | 28 |
| `weasyprint-cli` | 2 | 0 | 0 | 4 | 0 | 2 | 8 |
| `wkhtmltopdf` | 13 | 6 | 0 | 8 | 1 | 0 | 28 |

- **`papersharp`**：B2000:页数不一致(字符 108%), B8000:页数不一致(字符 96%), C100:页数不一致(字符 93%), C10:页数不一致(字符 105%), C300:页数不一致(字符 67%), C30:页数不一致(字符 102%), D10000:少画(字符 25%), D200000:少画(字符 29%), D500000:少画(字符 32%), D50000:少画(字符 41%), E100:少画图像, E10:少画(字符 80%), E300:少画图像, E30:少画(字符 80%), X100:少画, X10:页数不一致, X300:少画, X30:页数不一致
- **`playwright`**：B8000:页数不一致(字符 91%)
- **`weasyprint`**：A100:少画(字符 83%), A10:页数不一致(字符 110%), A1:页数不一致(字符 110%), A30:页数不一致(字符 105%), A3:页数不一致(字符 99%), B2000:页数不一致(字符 108%), B8000:页数不一致(字符 96%), C10:页数不一致(字符 103%), C300:no-pdf, C30:页数不一致(字符 96%), D200000:页数不一致(字符 92%), D500000:页数不一致(字符 97%), E100:页数不一致(字符 344%), E10:少画(字符 80%), E300:页数不一致(字符 900%), E30:页数不一致(字符 100%), X10:页数不一致, X300:no-pdf, X30:页数不一致, Z2:少画图像
- **`weasyprint-cli`**：C10:页数不一致(字符 103%), C300:no-pdf, C30:页数不一致(字符 96%), X10:页数不一致, X300:no-pdf, X30:页数不一致
- **`wkhtmltopdf`**：A100:少画(字符 50%), A10:少画(字符 48%), A1:少画(字符 51%), A30:少画(字符 49%), A3:少画(字符 48%), B8000:页数不一致(字符 101%), D200000:页数不一致(字符 98%), D500000:页数不一致(字符 92%), D50000:页数不一致(字符 107%), X100:页数不一致, X10:页数不一致, X300:页数不一致, X30:页数不一致, Z2:少画(字符 77%), Z3:尾部截断

> 结论级提醒：任何一家的延迟只有在**它把这一档内容真画出来了**的时候才能和别家相减。
> `少画`＝产物字符/图像显著少于参照；`页数不一致`＝分页行为不同（可能多画也可能少画）。

## 判据列的优先级

`verdict` 按「图像 → 字符 → 页数 → 末页」顺序取第一个越界的：图像轴先看图，文本轴先看字符。所以 `页数不一致` 不等于「只是分页不同而内容齐全」——例如同一个 cell 既可能页数差 3 倍、字符也只剩 3 成，那会先被字符判成 `少画`；反过来 `页数不一致` 的行是字符没越界（≥0.9）但页数越界。所有原始比值都在 `out/integrity.csv` 里，别只读这一列。

## 这批数字是在什么状态下量的

- 时间 2026-10-03T12:12:59+08:00；Windows 11 build 10.0.26200；16 核；dotnet 10.0.302 / node v24.18.0 / python 3.14.6
- 引擎版本：Chrome 154.0.8037.93、wkhtmltopdf wkhtmltopdf 0.12.5 (with patched qt)、WeasyPrint 70.0
- PaperSharp：`master@be10324`（docs: 台账补 P1-16 —— m7 表头那 2px 的根因是 monospace 解析到 ASCII 子集），**工作树不干净：183 个改动文件，其中 48 个在 `src/`** —— 本报告的引擎侧数字全部是这棵在途树上的结果，回到提交态要重跑
- 参赛适配器：papersharp, puppeteer, playwright, weasyprint, chrome-cli, wkhtmltopdf
- 口径：{"latency": "驱动侧墙钟（含落盘），median", "regimes": ["warm", "warmvary", "cold"], "rss": "worker pid + 全部后代工作集峰值", "iters": "自适应：目标预算 / 首次探测耗时，cell 级硬上限"}

## 解读与取舍

<!-- 人工解读段：plot.py 会把它拼进 REPORT.md 的「## 解读与取舍」下。
     这里的每个数字都能在 out/scale_cells.csv / out/integrity.csv / out/summary.md 里指认。 -->

## 1. 赛道划分：这条赛道只有「吃 HTML 的引擎」

七家横向报告（[`../report/REPORT.md`](../report/REPORT.md)）里那四家 API 通道
（iText7 / PDFsharp-MigraDoc / QuestPDF / Aspose.PDF）必须把设计稿逐块手抄成 C#，
那份报告自己也承认它们的高覆盖里一大半是 `A`（近似/自绘）。它们与本语料不是同一件事，本扫描不纳入。

HTML 通道共 6 位：`papersharp`（自研 CSS 引擎 + Skia）、`puppeteer` / `playwright` / `chrome-cli`
（**同一个 Chromium 内核**的三种接法）、`wkhtmltopdf`（Qt WebKit 4.x）、`weasyprint`（独立 Python 排版引擎）。

## 2. 一句话结论

**PaperSharp 的延迟优势只在「小文档 + 常驻进程」这一个格子里，而且这个格子比想象的小。**
按 A 轴（富样式整页）线性拟合：`papersharp warm = 174ms + 51.6ms/页`，
`puppeteer warm = 1876ms + 16.0ms/页`，`playwright warm = 1586ms + 11.8ms/页`，
`chrome-cli cold = 1473ms + 20.4ms/页`，`wkhtmltopdf cold = 310ms + 20.7ms/页`。
固定成本它最低（Chromium 的 1/9），边际成本它最高（Chromium 的 3~4 倍），
两条线必然相交：**35 页被 playwright 反超、42 页被 chrome-cli 反超、48 页被 puppeteer 反超**。
按 B 轴（长表行数）相交点更近：529 行 / 612 行 / 995 行。
而 `wkhtmltopdf` 从**第 4 页**起就比它快、`weasyprint` 第 3 页起——这两家后面有完整性折扣，见 §6。

## 3. 固定成本 vs 边际成本：服务端该看哪个

| 引擎 | 口径 | 固定成本 a | 边际 b | 1 页 | 10 页 | 100 页 |
|---|---|---:|---:|---:|---:|---:|
| papersharp | warm | 174 ms | 51.6 ms/页 | 202 | 717 | 5354 |
| playwright | warm | 1586 ms | 11.8 ms/页 | 1618 | 1686 | 2745 |
| puppeteer | warm | 1876 ms | 16.0 ms/页 | 1973 | 2144 | 3490 |
| chrome-cli | cold | 1473 ms | 20.4 ms/页 | 1497 | 1661 | 3510 |
| wkhtmltopdf | cold | 310 ms | 20.7 ms/页 | 325 | 526 | 2372 |
| weasyprint | warm | —（线性拟合差，b=164 ms/页且超线性） | | 241 | 1487 | 16430 |

读法：**固定成本决定小票/单页单据场景，边际成本决定长报表场景**。
PaperSharp 在 1 页上是所有对手里最快的（202 ms，wkhtmltopdf 325 ms，Chromium 系 1500~2000 ms），
这个 8~9 倍差距是它作为「不必拉起浏览器」的引擎唯一无法被替代的价值；
一旦文档到几十页，优势按上面的相交点顺序蒸发干净。
`weasyprint` 是反面教材：它固定成本低（241 ms，跟 PaperSharp 同级），但边际成本 164 ms/页
（PaperSharp 的 3 倍、Chromium 的 10 倍），100 页要 16.4 秒——纯 Python 排版在长文档上没有未来。

## 4. 内存：Chromium 的量级问题，和 PaperSharp 的位置

`out/scale_cells.csv` 的 `rss_peak_mb` 是该 cell 内进程树工作集峰值（含 Chrome 全部子进程）：

- `wkhtmltopdf` 全场最轻，A100 与 B8000 也只有 **113 MB** 上限。
- `papersharp` 上限 **490 MB**（出现在 B8000：8000 行表），常规档 150~280 MB。
- `weasyprint` 上限 **1579 MB**（B8000），Python 侧对象图 + 页面树不吃 GC 的亏就在这。
- Chromium 三兄弟 **2173~2484 MB**（B2000/E300 档），是 PaperSharp 的 5 倍。

这一栏是 PaperSharp 第二个真正站得住的优势：**同等文档量级下它比任何浏览器路线省 4~5 倍内存**，
并且不需要为并发实例数预留 Chrome 进程。

## 5. PDF 体积（产物 KB，同一 cell 各家）

| 引擎 | A1 | A100 | B2000 | D200000 | E300 | Z1(invoice) |
|---|---:|---:|---:|---:|---:|---:|
| papersharp | 477 | 2471 | 1041 | 587 | 217 | 272 |
| puppeteer / chrome-cli | 208 | 6216 | 4423 | 603 | 202 | 50 |
| wkhtmltopdf | 43 | 1377 | 405 | 267 | 64 | 32 |
| weasyprint | 75 | 2150 | 469 | 154 | 74 | — |

两个结论：
**长文档上 Chromium 的字体子集是按页切的**（A100 6.2 MB vs PaperSharp 2.5 MB），
报表类文档浏览器路线体积翻倍；但**单页小单据上 Chromium 反而小 5 倍**（Z1 50 KB vs 272 KB），
因为 PaperSharp 每次都要把用到的整副字形子集塞进去，1 页文档摊不平这份固定开销。
⚠️ PaperSharp 这一列受工作树在途字体改动影响（子集重烤还没落，见 MEMORY 与 P1-18），
只当量级参考，别当体积结论。

## 6. 完整性折扣：§2~§5 里哪些数字不能直接相减

`integrity.py` 对每个 (引擎,档) 各出一份 PDF 存档，按「图像 XObject → 抽取字符 → 页数 → 末页字符」
四级判据与无头 Chrome 对照（参照自身 24/24 全绿，说明这套判据能过自己）：

| 引擎 | ok | 少画 | 少画图像 | 页数不一致 | 尾部截断 | no-pdf |
|---|---:|---:|---:|---:|---:|---:|
| chrome-cli（参照） | 24 | 0 | 0 | 0 | 0 | 0 |
| puppeteer | 28 | 0 | 0 | 0 | 0 | 0 |
| playwright | 23 | 0 | 0 | 1 | 0 | 0 |
| **papersharp** | 10 | 8 | 2 | 8 | 0 | 0 |
| wkhtmltopdf | 13 | 6 | 0 | 8 | 1 | 0 |
| weasyprint | 8 | 2 | 1 | 15 | 0 | 2 |

三件必须打折的事：

- **`wkhtmltopdf` 的 A 轴 5 档全部「少画」**：它不认 flex，`.row` 那套卡片栅格与表头布局被整块跳过，
  而 A 轴是 §2 里「第 4 页反超 PaperSharp」那条结论的依据。所以那句话只能读成
  「一个不画 flex 的老引擎画得少所以快」，不能读成它赢。它的真优势在 §4 的内存与 §5 的体积。
- **`weasyprint` 的 A 轴页数是参照的 1.7~2.0 倍、E 轴 10~11 倍**：它不吃 `.page{height:296mm;overflow:hidden}`
  的裁切语义，把内容摊到更多页上；`C300`/`X300` 两档直接 `RecursionError` 出不了图（Python 递归上限）。
  它的 164 ms/页 里有一部分是「多画了一倍页数」。
- **`papersharp` 的失效集中在 C/X（深嵌套块链）与 D（长文本流）两轴**，且**是两种不同机制**，见 §7。
  D 轴那些 12.98 ms/千字 的边际成本是**只画了两三成文字**的情况下量出来的。

## 7. 本次扫描挖出来的 PaperSharp 缺陷：两种不同的跨页失效

**先说不是测量噪声**：参照 `chrome-cli` 与 `puppeteer` 全 28 档 `ok`（同一内核换接法也 `ok`），
判据能过自己；`wkhtmltopdf`/`weasyprint` 也各有各的 flag。所以下面这两条是引擎行为。

### 7-A 深嵌套块链：分页整个不生效，内容全塞第 1 页（C / X 轴）

| 档 | 元素数 | papersharp 页数 | chrome / wkhtml / weasy 页数 | papersharp 字符 ÷ 参照 |
|---|---:|---:|---|---:|
| C10（depth 10） | 7,207 | 89 | 104 / 104 / 107 | 105% |
| C30 | 5,061 | **1** | 35 / — / 36 | 102% |
| C100 | 4,327 | **1** | 11 / 11 / 11 | 93% |
| C300 | 4,011 | **1** | 4 / — / no-pdf | 67% |

页数从 depth≥30 起塌成 1 页，但**抽出来的字符还在（93~105%）**——
说明内容都画进了那一页、纵向溢出页面被裁掉。视觉上是「只有第一屏」，
而 PDF 里其实塞着整篇。这类失效最阴：文本可搜索、体积正常、页数看似合理（1 页）。
`X` 轴（每层再叠 2px padding 的旧语料）同形，且字符只剩更多（X100 判 `少画`）。

> **这条轴的指数别当结论读**：`reps = 4000/depth` 让「元素数」与「深度」反向变化
> （depth 10→300 时元素 7207→4011），所以 `charts/slopes.png` 里 C 轴那根
> PaperSharp `n^2.73` 是把两件事混在一起的拟合。能站得住的只有两个原始事实：
> ① C10 用 6.7 s、C300 只用 1.1 s，而前者元素只多 1.8 倍 —— 时间跟着元素数走且明显超线性；
> ② 更深反而更快 —— 这本身可疑（每个元素的祖先链更长），需要单独一条「元素数恒定、只变深度」
> 的轴才能定性。**这一条留给后续，不在本报告下结论。**


### 7-B 长文本流：文字根本没进 PDF（D 轴）

| 字符数档 | papersharp 页数 | 参照页数 | papersharp 抽出字符 | 参照抽出字符（语料的 94%） | 留存 |
|---:|---:|---:|---:|---:|---:|
| 10,000 | 2 | 5 | 2,608 | 10,364 | 25% |
| 50,000 | 9 | 25 | 19,584 | 47,237 | 41% |
| 200,000 | 27 | 97 | 58,077 | 201,614 | 29% |
| 500,000 | 74 | 241 | 159,016 | 494,803 | 32% |

参照三家（Chrome / WeasyPrint / wkhtmltopdf）页数彼此只差 4% 以内，只有 PaperSharp 稳定停在 28~40%，
且**字符数只剩 25~41% → 内容是真的没写进 PDF 内容流**（7-A 那种「画了但看不见」在字符计数上会被判成 ~100%，两者能明确区分）。

逐层排除的过程（都在 `scale/` 下可复跑）：

| 证据 | 结论 |
|---|---|
| `probe_wrap.py`：20 段中文，四家留存率 104%、逐字符一致 | 排除「不会折中文」 |
| `probe_wrap.py` 第二轮：CJK-first 与 Latin-first 两种字体栈结果完全相同 | 排除「`font-family` 以 Latin 打头导致中文走兜底不折行」 |
| `probe_bisect.py`：固定语料样式，只改段数 n=20/50/100/200 | 塌缩是**阈值型**：103% → 93% → **46% → 23%**，页数停在 3 |
| `probe_shapes.py`：三种形状各钉一个变量 | 单个 `<p>` 撑 4 页 ✓ 正常；80 个定高 `<div>` 撑 40 页 ✓ 正常；**80 个多行 `<p>` 只剩 1 页** ✗ |

**能过的**：显式分页符（A 轴 1/3/10/30/100 页全档 `ok`）、表格跨页（B 轴 236 页，与参照差 3%）。
**过不了的**：多个普通块/行内内容自动流动分页。两种表现（7-A 塞成一页、7-B 直接丢字）
是否同一根因需要引擎侧继续定位（`LayoutBuilder` / `Paginator` 的块链递归 vs `InlineLayout` 的续排），
但可以确定的是：**这条不修，D/C 两轴的延迟数字对它自己的评价是偏乐观的，对别人是不公平的**。


## 8. 口径本身的自证

- **锚点 Z 标定**：`Z1 = 仓库样例 invoice`。自家 `PaperSharpSamples bench 30` 在同一棵树上量到
  **35.5 ms**（PERF_REPORT §11.5 记的 09-26 是 31.0 ms → **引擎侧没有退化**）；
  本 harness 量到墙钟 69.9 ms，其中 worker 内部计时 60.8 ms、驱动侧 IPC+落盘 9.1 ms。
  差的那 ~25 ms 判为「CSS 从参数传入改成内联 `<style>`」的路径差 + 文档外壳差异。
  读法：**PaperSharp 的绝对延迟在本报告里系统性偏高 ~35 ms**，对 Chromium 那种 1500 ms 固定成本
  无影响，但对「1 页单据 200 ms」这类结论要减掉再比。
- **warmvary 是负结果**：把每次的输入换成带唯一 token 的副本后，
  PaperSharp −8%、puppeteer −6~−9%、playwright −9~−10%、weasyprint −1~+8%。
  方向对所有引擎一致 → 判为**会话漂移**，不是跨文档缓存红利。也就是说这批曲线不是被缓存刷出来的，
  但同机不同会话之间有 ~10% 的漂移底噪。
- **轮间离散度**（`--rounds 2`，同机同会话）：−16%~+10%，中位幅度 ~4%；
  `wkhtmltopdf` 最稳（−2%~+5%），`weasyprint` 抖动最大（±15%）。
  **凡差距小于 1.2 倍的比较，本报告不下结论。**
- **页面尺寸**：PaperSharp/W 家 595.0×842.0、Chromium 594.96×841.92、WeasyPrint 595.28×841.89 pt
  （都按 @page A4 出，偏差 <0.4 pt，横向可比）。

## 9. 选型建议（HTML 赛道内部）

| 场景 | 建议 | 依据 |
|---|---|---|
| 单页/几页的票据、发票、凭证，要常驻、要省内存 | `papersharp` | 1 页 202 ms 全场最快；RSS 是 Chromium 的 1/5；无外部进程 |
| 长报表（几十页以上）、样式复杂、能接受拉起浏览器 | `playwright` 或 `chrome-cli` | 35~42 页后反超；同内核下 Playwright 包装比 Puppeteer 快 25~70%（B2000 1420 vs 2421 ms） |
| 只跑遗留 CSS、要求内存极小、能接受不画 flex | `wkhtmltopdf` | 113 MB 上限、A100 2.4 s；但 A 轴 5 档「少画」，还原度看七家报告只有 55% 覆盖 |
| Python 栈内、纯 CSS 分页语义、文档不深不长的 | `weasyprint` | 与 PaperSharp 同级的固定成本；但边际 164 ms/页，深嵌套直接 `RecursionError` |

**给 PaperSharp 的三件事（按爆炸半径排）**：①§7-A/§7-B 两种跨页失效（深嵌套块链塞成一页、
长文本流直接丢字）——同时污染性能数字与产物正确性，且都不报错；
②A 轴 51.6 ms/页 vs Chromium 11~16 ms/页 的边际成本差，是 35 页以后所有劣势的来源；
③D 轴 12.98 ms/千字 vs Chromium 1.32 ms/千字，长文本布局是它最薄的实现（且这数字还是在只画三成的情况下量的）。
