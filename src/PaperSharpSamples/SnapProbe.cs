using SkiaSharp;
using PaperSharp;
using System.Runtime.InteropServices;

namespace PaperSharpSamples;

/// <summary>
/// 基线落位探针：<c>dotnet run -- snap &lt;name&gt; &lt;browser2x.png&gt; [width]</c>。
///
/// 为什么要这一条（2026-09-29 取证）：像素对拍里绝大多数文本带都显示「把浏览器层上下挪 1 设备像素，
/// 差异从 100% 掉到 20%」，而水平方向挪不动（dx=0）。1 设备像素 = 0.5 CSS px，正好是
/// 「布局里把行基线折成整 CSS px」与「布局保留分数、只在绘制时贴设备网格」两种口径之差。
/// 本探针把两者直接量出来：逐行取墨迹**纵向质心**（按 255−亮度加权，对亚像素落位连续敏感），
/// 引擎层与浏览器层各算一次，看质心的小数部分——
///   浏览器侧恒 ≈0 → Chrome 把文字贴到整数设备行（snap to pixel layout boundaries）；
///   浏览器侧随 top 的分数连续走 → Chrome 保留分数落位。
/// 样例页 <c>_probe-snap.html</c> 每行 top 递增 0.1 CSS px，正好把 0~0.9 的小数部分扫一遍。
/// </summary>
public static class SnapProbe
{
    public static void Run(string assets, string outDir, string name, string browserPng, float width)
    {
        var fonts = Path.Combine(assets, "fonts");
        if (Directory.Exists(fonts)) PaperSharp.Layout.FontManager.RegisterFontDirectory(fonts);
        var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
        var cssPath = Path.Combine(assets, name + ".css");
        var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";
        var r = Renderer.RenderRasterWithBoxes(html, css, width, baseDir: assets, scale: 2);
        var (ew, eh, ep) = PixelDiff.Decode(r.Bytes);
        var (bw, bh, bp) = PixelDiff.Decode(File.ReadAllBytes(browserPng));
        int ox0 = (int)Math.Round(r.BodyX * r.Scale), oy0 = (int)Math.Round(r.BodyY * 2);
        int w = Math.Min(ew - ox0, bw), h = Math.Min(eh - oy0, bh - 0);

        // 引擎层的逐行墨量 → 找带（连续有墨的行段），带间留空即换行
        var mass = new long[h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = ((y + oy0) * ew + x + ox0) * 4;
                mass[y] += Math.Max(0, 255 - (On(ep[i], ep[i + 3]) + On(ep[i + 1], ep[i + 3]) + On(ep[i + 2], ep[i + 3])) / 3);
            }
        Console.WriteLine($"snap {name}: 引擎 {ew}x{eh}@({ox0},{oy0}) · 浏览器 {bw}x{bh} · 比较区 {w}x{h}");
        Console.WriteLine("  #  top(css)   文字质心 引擎/浏览器   Δ文字   实心条质心 引擎/浏览器   Δ条     （单位：设备px，2x）");
        int li = 0;
        for (int y = 0; y < h; y++)
        {
            if (mass[y] < 400) continue;
            int y1 = y; while (y1 + 1 < h && mass[y1 + 1] > 120) y1++;
            // 带上下各扩 3 行，保证两层的墨迹（可能差 1~2 行）都落进同一个窗口
            int a = Math.Max(0, y - 3), b = Math.Min(h, y1 + 4);
            double te = Centroid(ep, ew, ox0, oy0, a, b, 250, 0), tb = Centroid(bp, bw, 0, 0, a, b, 250, 0);
            double be = Centroid(ep, ew, ox0, oy0, a, b, 1040, 640), bb = Centroid(bp, bw, 0, 0, a, b, 1040, 640);
            // 首条有墨设备行（阈值 128）：比质心更硬，判「落在哪条行上」用它；质心判亚像素连续落位用它。
            int fe = FirstInk(ep, ew, ox0, oy0, a, b, w), fb = FirstInk(bp, bw, 0, 0, a, b, w);
            string extra = name == "_probe-snap" ? $"   top={40.1 * li:0.0}" : "";
            li++;
            Console.WriteLine($" {li,2}   首墨行 引擎{fe,5}/浏览器{fb,5}  Δ{fb - fe,+3}   文字 {te,8:0.00}/{tb,8:0.00} Δ{tb - te,+6:0.00}   条 {be,8:0.00}/{bb,8:0.00} Δ{bb - be,+6:0.00}{extra}");
            y = y1;
        }
    }

    /// <summary>窗口内第一条有墨设备行（任一像素亮度 < 128），没有则 -1。</summary>
    static int FirstInk(byte[] p, int pw, int px0, int py0, int ya, int yb, int w)
    {
        for (int y = ya; y < yb; y++)
            for (int x = 0; x < w; x++)
            {
                int i = ((y + py0) * pw + x + px0) * 4;
                if ((On(p[i], p[i + 3]) + On(p[i + 1], p[i + 3]) + On(p[i + 2], p[i + 3])) / 3 < 128) return y;
            }
        return -1;
    }

    /// <summary>窗口内墨迹纵向质心（按 255−亮度加权，逐列求和后加权）。全白窗口返回 NaN。</summary>
    static double Centroid(byte[] p, int pw, int px0, int py0, int ya, int yb, int xEnd, int xFrom)
    {
        double sum = 0, wsum = 0;
        for (int y = ya; y < yb; y++)
            for (int x = xFrom; x < xEnd; x++)
            {
                int i = ((y + py0) * pw + x + px0) * 4;
                int lum = (On(p[i], p[i + 3]) + On(p[i + 1], p[i + 3]) + On(p[i + 2], p[i + 3])) / 3;
                int m = Math.Max(0, 255 - lum);
                sum += m; wsum += m * (double)y;
            }
        return sum < 1 ? double.NaN : wsum / sum;
    }

    static int On(int c, int a) => a >= 255 ? c : (c * a + 255 * (255 - a)) / 255;
}
