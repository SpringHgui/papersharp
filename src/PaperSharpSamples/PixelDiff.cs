using SkiaSharp;
using PaperSharp;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PaperSharpSamples;

/// <summary>
/// 像素级对拍：引擎 2x 光栅 vs 无头浏览器 2x 截图，逐像素找「重影」。
///
/// 盒级对拍（boxdiff）只能证明每个元素的矩形对上了，盒内的文字仍可以整条偏 1px、
/// 或者沿行方向逐渐累积偏移——那正是 Playground 叠加模式里看到的重影。
///
/// 两层都裁到 body 边框盒原点，但引擎的 body 原点在 2x 下带 .4 之类的小数（@page 毫米边距
/// 换算），取整就凭空造出半像素整体位移。所以先在全页范围搜一个整数设备平移把两层钉齐
/// （globalShift 就是这步的补偿量），再逐文本带搜残差：
///   残差 ≈ 0（SAD ≤25%）→ 真位移（可修）；残差只降一半 → 字形光栅化差异（提示/抗锯齿，属固有），
///   单列 soft 不计入评分。
/// 另外给一个 1x 指标：Playground 里图是按 1x CSS px 显示的，降采样后的差才是人眼看到的重影。
///
/// 用法：node tools/boxdiff.mjs &lt;name&gt; --dsf 2 --shot out.png → PaperSharpSamples pixdiff &lt;name&gt; out.png
/// </summary>
public static class PixelDiff
{
    public static void Run(string assets, string outDir, string name, string browserPng, float width)
    {
        var metaPath = browserPng + ".meta.json";
        if (!File.Exists(metaPath)) { Console.WriteLine($"缺 {metaPath}（用 boxdiff --shot 生成）"); return; }
        using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        float mDsf = Idx(meta, "dsf", 2f), mBodyX = Idx(meta, "bodyX"), mBodyY = Idx(meta, "bodyY");

        var fonts = Path.Combine(assets, "fonts");
        if (Directory.Exists(fonts)) PaperSharp.Layout.FontManager.RegisterFontDirectory(fonts);
        var html = File.ReadAllText(Path.Combine(assets, name + ".html"));
        var cssPath = Path.Combine(assets, name + ".css");
        var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";

        const float Scale = 2f;
        var r = Renderer.RenderRasterWithBoxes(html, css, width, baseDir: assets, scale: Scale);
        var (ew, eh, ep) = Decode(r.Bytes);
        var (bw, bh, bp) = Decode(File.ReadAllBytes(browserPng));

        // 裁剪原点必须与**落笔**同一口径：绘制层把盒原点折到整 CSS px（见 <see cref="PaperSharp.Paint.PaintSnap"/>），
        // 所以 body 的落笔位置是 round(bodyX)×scale，而不是 round(bodyX×scale)。
        // 两者在 @page 边距是毫米的时候必然分道（16mm = 60.4724px → 折整 60 → 设备 120；直接乘 120.94 → 121），
        // 差的就是那 1 设备像素——report / m1 / polish 三张的「整页上下差一格」全是这么来的（2026-09-29 取证）。
        static int Snap(float cssPx, float scale) =>
            (int)(Math.Round(cssPx, MidpointRounding.AwayFromZero) * scale);
        int ox0 = Snap(r.BodyX, r.Scale), oy0 = Snap(r.BodyY, Scale);
        int bx0 = Snap(mBodyX, mDsf), by0 = Snap(mBodyY, mDsf);
        int w = Math.Min(ew - ox0, bw - bx0), h = Math.Min(eh - oy0, bh - by0);
        if (w <= 0 || h <= 0) { Console.WriteLine($"裁完没重叠：引擎 {ew}x{eh}@({ox0},{oy0}) 浏览器 {bw}x{bh}@({bx0},{by0})"); return; }

        // 全局对齐：整页搜一次整数设备平移（±6），把它当成两层原点差的补偿。
        var (gdx, gdy, gsad0, gsad) = Shift(w, h, 0, h, ox0, oy0, bx0, by0, bw, bp, ep, ew, 6);
        bx0 += gdx; by0 += gdy;
        // 补偿后原点可能探出位图负侧：两层一起平移，相对差不变，索引恒非负。
        int sx = Math.Min(0, Math.Min(ox0, bx0)), sy = Math.Min(0, Math.Min(oy0, by0));
        ox0 -= sx; bx0 -= sx; oy0 -= sy; by0 -= sy;
        w = Math.Min(ew - ox0, bw - bx0); h = Math.Min(eh - oy0, bh - by0);
        Console.WriteLine($"{name}: 引擎 {ew}x{eh} body@({ox0},{oy0}) · 浏览器 body@({bx0},{by0}) 全局补偿 dx{S(gdx)} dy{S(gdy)} (SAD {Pct(gsad, gsad0)}) · 重叠 {w}x{h} · 总高 引擎 {(eh - oy0) / Scale:0.#} vs 浏览器 {(bh - by0) / Scale:0.#} CSSpx");

        var d = new int[w * h];
        var ie = new int[w * h];   // 离白距离（墨量）
        var ib = new int[w * h];
        long ink2 = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int ei = ((y + oy0) * ew + (x + ox0)) * 4, bi = ((y + by0) * bw + (x + bx0)) * 4, k = y * w + x;
                int er = On(ep[ei], ep[ei + 3]), eg = On(ep[ei + 1], ep[ei + 3]), eb = On(ep[ei + 2], ep[ei + 3]);
                int br = On(bp[bi], bp[bi + 3]), bg = On(bp[bi + 1], bp[bi + 3]), bb = On(bp[bi + 2], bp[bi + 3]);
                d[k] = Math.Max(Math.Abs(er - br), Math.Max(Math.Abs(eg - bg), Math.Abs(eb - bb)));
                ie[k] = Math.Max(Math.Abs(er - 255), Math.Max(Math.Abs(eg - 255), Math.Abs(eb - 255)));
                ib[k] = Math.Max(Math.Abs(br - 255), Math.Max(Math.Abs(bg - 255), Math.Abs(bb - 255)));
                if (ie[k] > 24 || ib[k] > 24) ink2++;
            }
        }
        int Strong2() { int n = 0; for (int i = 0; i < d.Length; i++) if (d[i] > 96) n++; return n; }

        // 1x 可见重影：两层各自 2x2 均值降采样到 1x 再求差——Playground 就是按 1x 显示引擎图的，
        // 只有降采样后仍然看得见的差，才是用户嘴里的「重影」。
        int w1 = w / 2, h1 = h / 2, ink1 = 0, vis = 0, mild = 0;
        var vis1 = new int[w1 * h1];
        var ebuf = new byte[w1 * h1 * 4];
        var bbuf = new byte[w1 * h1 * 4];
        for (int y = 0; y < h1; y++) for (int x = 0; x < w1; x++)
        {
            long eR = 0, eG = 0, eB = 0, bR = 0, bG = 0, bB = 0;
            for (int j = 0; j < 2; j++) for (int i = 0; i < 2; i++)
            {
                int ei = ((oy0 + y * 2 + j) * ew + ox0 + x * 2 + i) * 4, bi = ((by0 + y * 2 + j) * bw + bx0 + x * 2 + i) * 4;
                eR += On(ep[ei], ep[ei + 3]); eG += On(ep[ei + 1], ep[ei + 3]); eB += On(ep[ei + 2], ep[ei + 3]);
                bR += On(bp[bi], bp[bi + 3]); bG += On(bp[bi + 1], bp[bi + 3]); bB += On(bp[bi + 2], bp[bi + 3]);
            }
            int o1 = (y * w1 + x) * 4;
            ebuf[o1] = (byte)(eR / 4); ebuf[o1 + 1] = (byte)(eG / 4); ebuf[o1 + 2] = (byte)(eB / 4); ebuf[o1 + 3] = 255;
            bbuf[o1] = (byte)(bR / 4); bbuf[o1 + 1] = (byte)(bG / 4); bbuf[o1 + 2] = (byte)(bB / 4); bbuf[o1 + 3] = 255;
            int dd = Math.Max(Math.Abs(ebuf[o1] - bbuf[o1]), Math.Max(Math.Abs(ebuf[o1 + 1] - bbuf[o1 + 1]), Math.Abs(ebuf[o1 + 2] - bbuf[o1 + 2])));
            int ink = Math.Max(Math.Max(Math.Abs(ebuf[o1] - 255), Math.Max(Math.Abs(ebuf[o1 + 1] - 255), Math.Abs(ebuf[o1 + 2] - 255))),
                               Math.Max(Math.Abs(bbuf[o1] - 255), Math.Max(Math.Abs(bbuf[o1 + 1] - 255), Math.Abs(bbuf[o1 + 2] - 255))));
            vis1[y * w1 + x] = dd;
            if (ink > 24) { ink1++; if (dd > 40) vis++; else if (dd > 20) mild++; }
        }
        double vp = (double)vis / Math.Max(1, ink1) * 100;
        Console.WriteLine($"2x 强差 {Strong2():N0}/{ink2:N0} = {(double)Strong2() / Math.Max(1, ink2) * 100:0.0}%  |  1x 可见重影 {vis:N0}/{ink1:N0} = {vp:0.00}%  (弱差 {mild:N0} = {(double)mild / Math.Max(1, ink1) * 100:0.00}%)  → {(vp < 1 ? "干净" : vp < 4 ? "轻微" : vp < 12 ? "明显" : "严重")}");

        var rows = new int[h / 2 + 1];
        for (int y = 0; y < h; y++) { int n = 0; for (int x = 0; x < w; x++) if (d[y * w + x] > 96) n++; rows[y / 2] += n; }
        var bands = new List<(int y0, int y1, int sum)>();
        for (int ry = 0; ry < rows.Length; ry++)
        {
            if (rows[ry] < Math.Max(8, w / 200)) continue;
            if (bands.Count > 0 && ry == bands[^1].y1 + 1) bands[^1] = (bands[^1].y0, ry, bands[^1].sum + rows[ry]);
            else bands.Add((ry, ry, rows[ry]));
        }
        // 每条带搜一次最佳整数平移：能靠平移消掉大半差异的，是真位移（可修）；
        // 平移不动的，是 Skia 与 Chrome 各自的字形提示/抗锯齿（固有，不算重影）。
        // 阈值取 2 设备像素 = 1 CSS px：引擎 body 原点在 2x 下带 .4 之类小数，整数裁剪本身
        // 就会造出至多 ±1 设备像素的假位移，只有到 2 以上才是真错开。
        long shiftPix = 0, softPix = 0; int shiftBands = 0, softBands = 0, halfBands = 0;
        var report = new List<(int y0, int y1, int sum, int dx, int dy, double pct)>();
        foreach (var b in bands)
        {
            var (dx, dy, sad0, sadB) = Shift(w, h, b.y0 * 2, Math.Min(h, (b.y1 + 1) * 2), ox0, oy0, bx0, by0, bw, bp, ep, ew, 4);
            double ratio = sad0 == 0 ? 1 : sadB * 1.0 / sad0;
            bool moved = Math.Max(Math.Abs(dx), Math.Abs(dy)) >= 2 && ratio <= 0.6;
            // 平移后 SAD 几乎归零（≤25%）才是真位移（布局可修）；只抹掉一半的是字形光栅化差——
            // Skia/FreeType 与浏览器 DirectWrite 各自提示取整不同，同一字形墨迹差半格到一格
            // （challenge 的 20px 粗体 h1：引擎占 19 行、浏览器 20 行，平移 1px 仍剩 41% 差），布局侧修不掉。
            if (moved && ratio <= 0.25) { shiftPix += b.sum; shiftBands++; report.Add((b.y0, b.y1, b.sum, dx, dy, ratio)); }
            else if (moved) { softPix += b.sum; softBands++; report.Add((b.y0, b.y1, b.sum, dx, dy, ratio)); }
            else if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == 1 && ratio <= 0.6) halfBands++;
        }
        Console.WriteLine($"GHOST {name} vis={vp:0.00} bands={shiftBands}/{bands.Count} shiftPix={shiftPix:N0} ofInk={(double)shiftPix / Math.Max(1, ink2) * 100:0.00} soft={softBands}/{softPix:N0} halfNoise={halfBands}");
        Console.WriteLine($"真位移带 {shiftBands}/{bands.Count} 条、差像素 {shiftPix:N0} = 墨面的 {(double)shiftPix / Math.Max(1, ink2) * 100:0.0}%（重影评分，目标 0）· 光栅差 {softBands} 条 {softPix:N0} · 半像素噪声 {halfBands}");

        // PGHOST=1：分数平移取证。上面的位移带只搜**整数**设备像素且要求 ≥2 才算数，
        // 于是「半格系统错位」与「逐字累积漂移」两类真错全部落在判据之外，却被 vis% 计入重影。
        // 这里把每条带横切成若干段，逐段搜 ±2.5 设备像素、步长 0.1 的最佳分数平移：
        //   各段 dx 同值 → 整段错位（原点/取整策略差）；dx 沿 x 单调走 → 逐字推进宽度漂移（可修）；
        //   dx≈0 而残差仍高 → 字形本身不同（轮廓/提示），平移救不了。
        if (Environment.GetEnvironmentVariable("PGHOST") == "1") GhostScan(ep, bp, ew, bw, ox0, oy0, bx0, by0, w, h, bands);
        foreach (var b in report.OrderByDescending(x => x.sum).Take(10))
        {
            // 带坐标本身就是 CSS px（行号 = 设备行 ÷ 2）。只看高度与文本带同量级的盒：
            // 容器盒高出一大截，报出来全是 div[1] 这种噪音。
            float bandH = b.y1 - b.y0;
            var hits = r.Boxes
                .Where(x => x.Y < b.y1 + 2 && x.Y + x.H >= b.y0 - 2 && x.H >= bandH * 0.4f && x.H <= bandH * 3 + 8)
                .OrderBy(x => Math.Abs(x.H - bandH)).ThenBy(x => x.W * x.H)
                .Select(x => $"{x.Tag} {x.DomPath[(x.DomPath.LastIndexOf('/') + 1)..]}@{x.Y:0.#}x{x.H:0.#}")
                .Take(3);
            Console.WriteLine($"  {(b.pct <= 0.25 ? "位移" : "光栅")} y {b.y0,5}..{b.y1,5} 差 {b.sum,7:N0}  位移 dx{S(b.dx / Scale)} dy{S(b.dy / Scale)}  SAD {b.pct * 100:0}%  {string.Join(" | ", hits)}");
        }

        WriteDiffImage(outDir, name, vis1, w1, h1);
        WriteRgba(outDir, "pixdiff-" + name + "-e1", ebuf, w1, h1);
        WriteRgba(outDir, "pixdiff-" + name + "-b1", bbuf, w1, h1);
        // PDIFF2=1：另存对齐后的 2x 两层裁剪，用来看「同一行墨迹落在哪条设备行」——
        // 1x 图只能判出整 CSS px 的位移，亚像素取整的分歧必须回到 2x 行上量。
        if (Environment.GetEnvironmentVariable("PDIFF2") == "1")
        {
            var e2 = new byte[w * h * 4]; var b2 = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                Array.Copy(ep, ((y + oy0) * ew + ox0) * 4, e2, y * w * 4, w * 4);
                Array.Copy(bp, ((y + by0) * bw + bx0) * 4, b2, y * w * 4, w * 4);
            }
            WriteRgba(outDir, "pixdiff-" + name + "-e2", e2, w, h);
            WriteRgba(outDir, "pixdiff-" + name + "-b2", b2, w, h);
        }
        WriteRows(outDir, name, d, ie, ib, w, h);
        Console.WriteLine($"→ {Path.Combine(outDir, $"pixdiff-{name}.png")}（差异）/ -e1（引擎）/ -b1（浏览器）");
    }

    /// <summary>叠到白底后的通道值（截图带 alpha，引擎图通常不透明）。</summary>
    static int On(int c, int a) => a >= 255 ? c : (c * a + 255 * (255 - a)) / 255;

    static string S(double v) => (v >= 0 ? "+" : "") + v.ToString("0.##");
    static string Pct(long a, long b) => (b == 0 ? 0 : a * 100.0 / b).ToString("0") + "%";

    /// <summary>分数平移取证（<c>PGHOST=1</c>）：逐带横切成段，每段单独搜 ±2.5 设备像素、步长 0.1 的
    /// 最佳水平分数平移（双线性重采样浏览器层）。三类结论直接读 dx 沿 x 的走势：
    /// 各段同值=整段错位；单调走=逐字推进宽度漂移；恒 ≈0 而残差仍高=字形不同（平移无效）。</summary>
    static void GhostScan(byte[] ep, byte[] bp, int ew, int bw, int ox0, int oy0, int bx0, int by0,
        int w, int h, List<(int y0, int y1, int sum)> bands)
    {
        // 先判参照物本身：浏览器若走 LCD 次像素渲染，字干两侧带 R≠B 的彩边，引擎一律灰度 AA，
        // 这类差是显示口径不是画错，逐像素差会把它们全计进重影。
        int inkPx = 0, chromaB = 0, chromaE = 0;
        for (int y = 0; y < h; y += 2) for (int x = 0; x < w; x += 2)
        {
            int ei = ((y + oy0) * ew + x + ox0) * 4, bi = ((y + by0) * bw + x + bx0) * 4;
            int er = On(ep[ei], ep[ei + 3]), eg = On(ep[ei + 1], ep[ei + 3]), eb = On(ep[ei + 2], ep[ei + 3]);
            int br = On(bp[bi], bp[bi + 3]), bg = On(bp[bi + 1], bp[bi + 3]), bb = On(bp[bi + 2], bp[bi + 3]);
            if (Math.Max(255 - er, 255 - eg) <= 24 && Math.Max(255 - br, 255 - bg) <= 24) continue;
            inkPx++;
            if (Math.Abs(br - bb) > 24 && Math.Min(br, Math.Min(bg, bb)) < 210) chromaB++;
            if (Math.Abs(er - eb) > 24 && Math.Min(er, Math.Min(eg, eb)) < 210) chromaE++;
        }
        Console.WriteLine($"GHOSTSCAN 彩边率（|R−B|>24 的墨像素）浏览器 {100.0 * chromaB / Math.Max(1, inkPx):0.00}% · 引擎 {100.0 * chromaE / Math.Max(1, inkPx):0.00}%");

        foreach (var b in bands.OrderByDescending(x => x.sum).Take(8))
        {
            int ya = b.y0 * 2, yb = Math.Min(h, (b.y1 + 1) * 2);
            const int Seg = 320, Stride = 2;
            var parts = new List<string>();
            double sumX = 0, sumDx = 0, sumXX = 0, sumXDx = 0; int n = 0;
            for (int sx = 0; sx + Seg <= w + Seg; sx += Seg)
            {
                int xe = Math.Min(w, sx + Seg);
                int ink = 0;
                for (int y = ya; y < yb; y += 2) for (int x = sx; x < xe; x += 4)
                        if (On(ep[((y + oy0) * ew + x + ox0) * 4 + 1], ep[((y + oy0) * ew + x + ox0) * 4 + 3]) < 231) ink++;
                if (ink < 25) continue;
                long sad0 = SadFrac(ep, bp, ew, bw, ox0, oy0, bx0, by0, sx, xe, ya, yb, Stride, 0, 0);
                // 粗搜 ±2 设备像素（步长 .5）→ 最优点周围 ±0.5 细搜（步长 .1）。
                // 只搜水平会漏掉「基线差半格」这一整类——那同样是平移可消、布局可修的真错。
                double bdx = 0, bdy = 0; long bsd = sad0;
                for (double dy = -2; dy <= 2.0001; dy += 0.5) for (double dx = -2; dx <= 2.0001; dx += 0.5)
                {
                    long s = SadFrac(ep, bp, ew, bw, ox0, oy0, bx0, by0, sx, xe, ya, yb, Stride, dx, dy);
                    if (s < bsd) { bsd = s; bdx = dx; bdy = dy; }
                }
                for (double dy = bdy - 0.5; dy <= bdy + 0.5001; dy += 0.1) for (double dx = bdx - 0.5; dx <= bdx + 0.5001; dx += 0.1)
                {
                    long s = SadFrac(ep, bp, ew, bw, ox0, oy0, bx0, by0, sx, xe, ya, yb, Stride, dx, dy);
                    if (s < bsd) { bsd = s; bdx = dx; bdy = dy; }
                }
                // 墨量比（Σ255−亮度）：平移抹不掉的差还要分「轮廓不同」与「浓淡不同」，
                // 比值偏离 1 超过 8% 就是覆盖率/字重口径的问题，跟落位无关。
                double mass = InkMass(ep, ew, ox0, oy0, sx, xe, ya, yb)
                            / (double)Math.Max(1, InkMass(bp, bw, bx0, by0, sx, xe, ya, yb));
                double cx = (sx + xe) / 2.0;
                sumX += cx; sumDx += bdx; sumXX += cx * cx; sumXDx += cx * bdx; n++;
                parts.Add($"x{sx / 2:D4} dx{bdx:+0.0;-0.0;0} dy{bdy:+0.0;-0.0;0} SAD {(sad0 == 0 ? 0 : bsd * 100.0 / sad0):0}% 墨量比 {mass:0.00}");
            }
            string slope = n >= 2 ? $" 斜率 {(n * sumXDx - sumX * sumDx) / Math.Max(1e-9, n * sumXX - sumX * sumX) * 1000:+0.00;-0.00;0.00} 设备px/1000px" : "";
            Console.WriteLine($"  带 y{b.y0}..{b.y1} 差 {b.sum,6:N0} 段数 {n}{slope}");
            foreach (var p in parts) Console.WriteLine($"      {p}");
        }
    }

    /// <summary>区域墨量：Σ(255−亮度)，亮度取三通道均值。用来分「轮廓不同」与「浓淡不同」。</summary>
    static long InkMass(byte[] p, int pw, int px0, int py0, int xs, int xe, int ya, int yb)
    {
        long m = 0;
        for (int y = ya; y < yb; y++) for (int x = xs; x < xe; x += 2)
        {
            int i = ((y + py0) * pw + x + px0) * 4;
            int lum = (On(p[i], p[i + 3]) + On(p[i + 1], p[i + 3]) + On(p[i + 2], p[i + 3])) / 3;
            m += Math.Max(0, 255 - lum);
        }
        return m;
    }

    /// <summary>一段文本带在分数平移 (dx,dy) 下的 SAD（浏览器层双线性重采样，0/0 时退化为整数取样）。</summary>
    static long SadFrac(byte[] ep, byte[] bp, int ew, int bw, int ox0, int oy0, int bx0, int by0,
        int xs, int xe, int ya, int yb, int stride, double dx, double dy)
    {
        long sad = 0;
        for (int y = ya; y < yb; y++)
        {
            for (int x = xs; x < xe; x += stride)
            {
                int ei = ((y + oy0) * ew + x + ox0) * 4;
                if (ep[ei + 3] == 0) continue;
                int er = On(ep[ei], ep[ei + 3]), eg = On(ep[ei + 1], ep[ei + 3]), eb = On(ep[ei + 2], ep[ei + 3]);
                Bilinear(bp, bw, x + dx, y + dy, bx0, by0, out int br, out int bg, out int bb);
                sad += Math.Abs(er - br) + Math.Abs(eg - bg) + Math.Abs(eb - bb);
            }
        }
        return sad;
    }

    static void Bilinear(byte[] p, int pw, double fx, double fy, int bx0, int by0, out int r, out int g, out int b)
    {
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double ax = fx - x0, ay = fy - y0;
        Tap(p, pw, x0, y0, bx0, by0, out int r00, out int g00, out int b00);
        Tap(p, pw, x0 + 1, y0, bx0, by0, out int r10, out int g10, out int b10);
        Tap(p, pw, x0, y0 + 1, bx0, by0, out int r01, out int g01, out int b01);
        Tap(p, pw, x0 + 1, y0 + 1, bx0, by0, out int r11, out int g11, out int b11);
        r = (int)Math.Round((r00 * (1 - ax) + r10 * ax) * (1 - ay) + (r01 * (1 - ax) + r11 * ax) * ay);
        g = (int)Math.Round((g00 * (1 - ax) + g10 * ax) * (1 - ay) + (g01 * (1 - ax) + g11 * ax) * ay);
        b = (int)Math.Round((b00 * (1 - ax) + b10 * ax) * (1 - ay) + (b01 * (1 - ax) + b11 * ax) * ay);
    }

    /// <summary>浏览器层 (x,y) 处叠到白底后的三通道值；越界夹到边缘。</summary>
    static void Tap(byte[] p, int pw, int x, int y, int bx0, int by0, out int r, out int g, out int b)
    {
        if (x < 0) x = 0;
        if (x >= pw) x = pw - 1;
        int i = ((y + by0) * pw + x + bx0) * 4;
        r = On(p[i], p[i + 3]); g = On(p[i + 1], p[i + 3]); b = On(p[i + 2], p[i + 3]);
    }

    /// <summary>在 ±range 设备像素内搜使 SAD 最小的整数平移（平移只在重叠区内取值）。</summary>
    static (int dx, int dy, long sad0, long sadBest) Shift(int w, int h, int y0, int y1,
        int ox0, int oy0, int bx0, int by0, int bw, byte[] bp, byte[] ep, int ew, int range)
    {
        long Eval(int dx, int dy)
        {
            long sad = 0;
            for (int y = y0; y < y1; y += 2)
            {
                int cy = y + dy;
                if (cy < 0 || cy >= h) continue;
                for (int x = 0; x < w; x += 2)
                {
                    int cx = x + dx;
                    if (cx < 0 || cx >= w) continue;
                    int ei = ((y + oy0) * ew + (x + ox0)) * 4, bi = ((cy + by0) * bw + (cx + bx0)) * 4;
                    sad += Math.Abs(ep[ei] - bp[bi]) + Math.Abs(ep[ei + 1] - bp[bi + 1]) + Math.Abs(ep[ei + 2] - bp[bi + 2]);
                }
            }
            return sad;
        }
        long sad0 = Eval(0, 0), best = sad0; int bdx = 0, bdy = 0;
        for (int dy = -range; dy <= range; dy++) for (int dx = -range; dx <= range; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            long s = Eval(dx, dy);
            if (s < best) { best = s; bdx = dx; bdy = dy; }
        }
        return (bdx, bdy, sad0, best);
    }

    public static (int w, int h, byte[] px) Decode(byte[] bytes)
    {
        using var img = SKImage.FromEncodedData(bytes) ?? throw new InvalidDataException("PNG 解不开");
        int w = img.Width, h = img.Height;
        var dst = new byte[w * h * 4];
        var g = GCHandle.Alloc(dst, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            if (!img.ReadPixels(info, g.AddrOfPinnedObject(), w * 4)) throw new InvalidDataException("取像素失败");
        }
        finally { g.Free(); }
        return (w, h, dst);
    }

    static float Idx(JsonDocument d, string name, float dflt = 0) =>
        d.RootElement.TryGetProperty(name, out var v) ? v.GetSingle() : dflt;

    /// <summary>逐设备行的墨迹剖面：y, 引擎墨像素数, 浏览器墨像素数, 强差数。
    /// 两条剖线的错位一眼可辨，比平移搜索更直白（awk 里做互相关即可定量）。
    /// 落 output/pixdiff-&lt;name&gt;-rows.tsv。</summary>
    static void WriteRows(string outDir, string name, int[] d, int[] ie, int[] ib, int w, int h)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("y\tinkE\tingkB\tdiff\n");
        for (int y = 0; y < h; y++)
        {
            int ne = 0, nb = 0, nd = 0;
            for (int x = 0; x < w; x++)
            {
                int k = y * w + x;
                if (ie[k] > 24) ne++;
                if (ib[k] > 24) nb++;
                if (d[k] > 96) nd++;
            }
            if (ne + nb + nd > 0) sb.Append(y).Append('\t').Append(ne).Append('\t').Append(nb).Append('\t').Append(nd).Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, $"pixdiff-{name}-rows.tsv"), sb.ToString());
    }

    /// <summary>1x 差异图：越红越重影（vis1 大），淡蓝=弱差。</summary>
    static void WriteDiffImage(string outDir, string name, int[] v, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0, p = 0; i < w * h; i++, p += 4)
        {
            int t = Math.Min(255, v[i] * 2);
            px[p] = (byte)t; px[p + 1] = (byte)(t / 4); px[p + 2] = (byte)(255 - t); px[p + 3] = 255;
        }
        WriteRgba(outDir, "pixdiff-" + name, px, w, h);
    }

    /// <summary>把 RGBA8888 缓冲按不透明写成 PNG（1x 对齐层，肉眼核对用）。</summary>
    static void WriteRgba(string outDir, string tag, byte[] rgba, int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var g = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using var img = SKImage.FromPixels(info, g.AddrOfPinnedObject(), w * 4)
                ?? throw new InvalidDataException("FromPixels 失败");
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            Directory.CreateDirectory(outDir);
            using var fs = File.Create(Path.Combine(outDir, $"{tag}.png"));
            data.SaveTo(fs);
        }
        finally { g.Free(); }
    }
}
