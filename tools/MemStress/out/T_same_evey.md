# PaperSharp 批量内存压测
# 模板=challenge.html 文档数=120 并发=4 内容=same 驱逐=every GC=Workstation CPU=16 runtime=10.0.10
# 说明: docs=已完成 ws=工作集MB priv=私有字节MB heap=托管堆MB alloc=累计分配MB g0/g1/g2=GC次数 thr=线程数 ms/doc=平均耗时
#   docs      ws    priv    heap  allocMB  fb/face  sub  gc0/g1/g2  thr  ms/doc
       0   249.7   157.5    33.5       0   321/  334  10   36/ 13/  6   9     0.0
      40   299.1   200.1    86.1    5434   321/  334   0  365/181/ 99   9   564.0   (每份后清空字面缓存)
      80   330.1   230.1    87.2   10952   321/  334   0  698/363/196  11   564.9   (每份后清空字面缓存)
     120   316.7   216.6   104.4   16471   321/  334   0 1025/539/287   8   556.4   (每份后清空字面缓存)
# 共驱逐常驻子集字面 1200 张（每份平均 10.0）
     120   316.7   216.6   104.4   16471   321/  334   0 1025/539/287   8   556.4
#------------------------------------------------------------------------------
# 跑完（120 份, 墙钟 66.8s, 吞吐 1.8 docs/s, 产物合计 97.0 MB）
     120   282.1   181.6     9.3   16471   321/  334   0 1029/543/291   8   556.4   <- 强制 Full GC + 等终结器后
# 峰值: priv 230.1 MB   ws 330.1 MB   heap 104.4 MB   （起点 priv 157.7 / ws 249.9 MB）
# 增长斜率（后半段线性拟合）: priv -337.3 MB/1k 份   heap +429.8 MB/1k 份   priv/回退码位条目 +0 KB
# 托管累计分配 140553 KB/份   GC gen0 994 / gen1 531 / gen2 286
# Full GC 后: priv 181.6 MB（比峰值降 48.5） 托管堆滞留 9.2 MB → 原生侧 ≈ 172.4 MB
# 静态缓存条目数（120 份之后；只列 ≥4 条的）:
#        334  PaperSharp.Layout.TextShaper.ListFaceCache
#        321  PaperSharp.Layout.TextShaper.FallbackCache
#        179  PaperSharp.Layout.EastAsianWidth.AmbiguousRanges
#         40  PaperSharp.Css.StyleResolver.UnsupportedProps
#         13  PaperSharp.Layout.FontManager.Fallbacks
#         13  PaperSharp.Layout.KernTable.Cache
#         13  PaperSharp.Css.CounterStyle.Romans
#         13  PaperSharp.Css.FlatCascade.Inherited
#         11  PaperSharp.Layout.FontManager.Cache
#         11  PaperSharp.Layout.FontManager.SlantedFace
#         11  PaperSharp.Layout.FontManager.MetricsTable
#         10  PaperSharp.Pdf.PdfGlyphUsage.FaceKeys
#         10  PaperSharp.Pdf.PdfGlyphUsage.ColorFont
#         10  PaperSharp.Css.CssValueParser.LengthUnitChars
#         10  PaperSharp.Css.CssValueParser.BolderByParent
#         10  PaperSharp.Css.CssValueParser.LighterByParent
#         10  PaperSharp.Css.PropCache.ShorthandOnAsk
#          9  PaperSharp.Layout.FontManager.NoBoldFamilies
#          9  PaperSharp.Css.StyleResolver.NonRenderingTags
#          8  PaperSharp.Imaging.PngEncoder.Signature
#          7  PaperSharp.Css.CssValueParser.AbsoluteUnits
#          6  PaperSharp.Pdf.TtfSubsetter.Drop
#          6  PaperSharp.Layout.FontManager.LegacyMonoFamilies
#          6  PaperSharp.Css.AtPageParser.Sizes
#          6  PaperSharp.Css.StyleResolver.SizeProps
#          5  PaperSharp.Layout.FontManager.BundledSubsetNames
#          5  PaperSharp.Layout.FontManager.YaheiSubsetFamilies
#          5  PaperSharp.Fonts.Bundled.Cache
# 子集缓存：空（本轮被驱逐过）
# 采样明细: G:\Github\papersharp\tools\MemStress\bin\rel4\out\mem_serial_120x4_same_evevery.csv
