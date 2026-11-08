#if ITEXT
using iText.Html2pdf;
using iText.IO.Font;
using iText.Layout.Font;

namespace CiBench.Engines;

/// <summary>
/// iText 7 Core 9.8 + pdfHTML 6.3.4（AGPLv3 / 商业双授权）。
/// 与另外两家 Chromium 方案根本不同：pdfHTML 自己解析 HTML/CSS，再用 iText Layout 排版的
/// **纯托管解析型引擎**，不启动浏览器；CSS 支持面比 Blink 窄（不支持 flex/grid 等），
/// 因此本报告只比较「同一份语料的排版成本」，不比较视觉还原度。
///
/// 中文需要 CJK 字体：InitializeAsync 进程级探测一个可加载的系统中文字体文件，
/// 每个并发 worker 独立构造 FontProvider + ConverterProperties（iText 的字体缓存不共享，
/// 与其他引擎「每 worker 独占不可共享渲染对象」的口径一致）。优先加载单个字体文件，
/// 比 AddSystemFonts() 全盘扫描更快、更可复现；找不到时才回退全系统扫描。
/// </summary>
public sealed class ITextEngine : IPdfEngine
{
    string? _fontPath;

    public string Name => "itext";
    public string Display => "iText 7 pdfHTML";
    public string RenderingEngine => "解析型 HTML/CSS 布局（纯托管，无浏览器）";
    public string Version => typeof(HtmlConverter).Assembly.GetName().Version?.ToString() ?? "unknown";

    // AGPL 版本不需要 license key；不设任何预检，让真实渲染结果说话。
    public string? Preflight() => null;

    public Task InitializeAsync(string html, string baseDir, CancellationToken ct)
    {
        foreach (var path in CjkCandidates())
        {
            // TTC 需要 ",索引" 寻址，实际文件名要去掉索引再判断是否存在。
            if (!File.Exists(path.Split(',')[0])) continue;
            try
            {
                // 必须检查返回值：AddFont 加载失败时返回 false 而不抛异常，
                // 只看异常会把「不可用的 TTC」误判成可用，最终中文全被 Helvetica 吃掉。
                var probe = new FontProvider();
                bool ok = probe.GetFontSet().AddFont(path, PdfEncodings.IDENTITY_H, "Microsoft YaHei");
                if (!ok) continue;
                _fontPath = path;
                break;
            }
            catch { /* 该字体不可用，继续试下一个 */ }
        }
        if (_fontPath is null)
            Console.Error.WriteLine("[itext] 未找到可加载的 CJK 字体文件，回退 AddSystemFonts()" +
                                    "（中文可能缺字，产物体积会偏小，耗时仍如实测量）");
        return Task.CompletedTask;
    }

    public Task<IPdfWorker> CreateWorkerAsync(string html, string baseDir, CancellationToken ct)
    {
        var fonts = new FontProvider();
        fonts.AddStandardPdfFonts();
        if (_fontPath is not null)
        {
            // 光 AddFont(path) 不够：iText 按字体自身的 family name 匹配 CSS font-family，
            // msyh.ttc / Noto CJK 的内部名与语料里写的 "Microsoft YaHei" 等未必对得上，
            // 结果中文被静默丢弃、只剩 Helvetica（产物只有 3KB）。FontSet 的 alias 会
            // 替换原 family 参与选择算法，把语料声明的每个 family 都显式指向同一个 CJK 字体。
            var set = fonts.GetFontSet();
            bool registered = false;
            foreach (string alias in CssFamilies)
            {
                try
                {
                    // 同一个 FontProgram 重复加入会被 FontSet 去重（第二次起返回 false），
                    // 注册成功一次即可：CSS 的主 family 命中后中文就会用这个字体渲染。
                    if (set.AddFont(_fontPath, PdfEncodings.IDENTITY_H, alias))
                    {
                        registered = true;
                        break;
                    }
                }
                catch { /* 换下一个别名重试 */ }
            }
            if (!registered) fonts.AddSystemFonts();
        }
        else fonts.AddSystemFonts();

        var props = new ConverterProperties();
        props.SetFontProvider(fonts);
        props.SetBaseUri(baseDir);       // 语料自包含，但与其它引擎一样给出资源根
        props.SetCharset("UTF-8");
        return Task.FromResult<IPdfWorker>(new Worker(html, props));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // 与 spec/invoice.html 的 font-family 声明一一对应（改语料时这里也要同步）
    static readonly string[] CssFamilies =
        ["Microsoft YaHei", "Noto Sans SC", "PingFang SC", "sans-serif"];

    static IEnumerable<string> CjkCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            yield return Path.Combine(dir, "msyh.ttc") + ",0";   // TTC 需按索引寻址
            yield return Path.Combine(dir, "msyh.ttc");
            yield return Path.Combine(dir, "simhei.ttf");
            yield return Path.Combine(dir, "msyhbd.ttc") + ",0";
            yield return Path.Combine(dir, "simsun.ttc") + ",0";
        }
        else if (OperatingSystem.IsLinux())
        {
            // GitHub ubuntu-latest 装 fonts-noto-cjk 后的常见路径
            yield return "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc,0";
            yield return "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc";
            yield return "/usr/share/fonts/opentype/noto/NotoSansCJKsc-Regular.otf";
            yield return "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc,0";
            yield return "/usr/share/fonts/truetype/arphic/uming.ttc,0";
        }
        else
        {
            yield return "/System/Library/Fonts/PingFang.ttc";
            yield return "/Library/Fonts/Arial Unicode.ttf";
        }
    }

    sealed class Worker : IPdfWorker
    {
        readonly string _html;
        readonly ConverterProperties _props;
        public Worker(string html, ConverterProperties props) { _html = html; _props = props; }

        public Task<byte[]> RenderAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream();
            // 与其它引擎一致：每次从同一 HTML 字符串重新排版，返回 byte[] 由测量内核统一落盘。
            HtmlConverter.ConvertToPdf(_html, ms, _props);
            return Task.FromResult(ms.ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
#endif
