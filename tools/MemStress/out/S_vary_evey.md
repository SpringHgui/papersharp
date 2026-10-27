# PaperSharp 批量内存压测
# 模板=challenge.html 文档数=120 并发=4 内容=vary 驱逐=every GC=Workstation CPU=16 runtime=10.0.10
# 说明: docs=已完成 ws=工作集MB priv=私有字节MB heap=托管堆MB alloc=累计分配MB g0/g1/g2=GC次数 thr=线程数 ms/doc=平均耗时
#   docs      ws    priv    heap  allocMB  fb/face  sub  gc0/g1/g2  thr  ms/doc
       0   263.7   169.4    44.6       0   357/  370  14   38/ 14/  7   9     0.0
      40   310.0   210.2    12.0    5540   597/  610   0  377/174/100   9   593.2   (每份后清空字面缓存)
      80   333.9   233.0    84.9   11147   771/  784   0  713/326/188  11   585.6   (每份后清空字面缓存)
     120   340.0   239.2    31.0   16754   878/  891   0 1049/484/277   8   576.4   (每份后清空字面缓存)
# 共驱逐常驻子集字面 1205 张（每份平均 10.0）
     120   340.0   239.2    31.0   16754   878/  891   0 1049/484/277   8   576.4
#------------------------------------------------------------------------------
# 跑完（120 份, 墙钟 69.2s, 吞吐 1.7 docs/s, 产物合计 97.0 MB）
     120   324.2   223.3     9.4   16754   878/  891   0 1053/488/281   8   576.4   <- 强制 Full GC + 等终结器后
# 峰值: priv 239.2 MB   ws 340.0 MB   heap 84.9 MB   （起点 priv 169.6 / ws 263.9 MB）
# 增长斜率（后半段线性拟合）: priv +154.5 MB/1k 份   heap -1349.0 MB/1k 份   priv/回退码位条目 +59 KB
# 托管累计分配 142969 KB/份   GC gen0 1016 / gen1 475 / gen2 275
# Full GC 后: priv 223.3 MB（比峰值降 15.9） 托管堆滞留 9.4 MB → 原生侧 ≈ 213.9 MB
# 静态缓存条目数（120 份之后；只列 ≥4 条的）:
#        891  PaperSharp.Layout.TextShaper.ListFaceCache
#        878  PaperSharp.Layout.TextShaper.FallbackCache
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
# 采样明细: G:\Github\papersharp\tools\MemStress\bin\rel4\out\mem_serial_120x4_vary_p800_evevery.csv
