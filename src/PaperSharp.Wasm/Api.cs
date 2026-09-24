using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using PaperSharp.Layout;

namespace PaperSharp.Wasm;

/// <summary>
/// JS（node/bun/浏览器）经 dotnet.getAssemblyExports 调用的导出面。
/// 入参/出参统一 JSON 信封，避免多参数编组差异；图片字节用 base64 传输。
/// 只用 JsonDocument（DOM）解析、手写序列化，不依赖反射（wasm 侧可能被裁剪）。
/// </summary>
public static partial class Api
{
    static readonly string FontDir = Path.Combine(AppContext.BaseDirectory, "wasm-fonts");

    static Api()
    {
        // 自研 PNG 编码器在 native 宿主靠分块并行拿到 19×，WASM 是单线程 + Mono 解释器，
        // 每字节托管循环反而比 Skia 原生编码慢：实测 23.7Mpx 快照 2533→3383ms（scale=2 5959→8830ms）。
        Renderer.UseFastPng = false;
    }

    [JSExport]
    public static string Render(string requestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            var html = GetStr(root, "html");
            var css = GetStr(root, "css");
            var format = GetStr(root, "format").ToLowerInvariant();
            float width = root.TryGetProperty("width", out var w) ? (float)w.GetDouble() : 794f;
            float scale = root.TryGetProperty("scale", out var s) ? (float)s.GetDouble() : 1f;
            int quality = root.TryGetProperty("quality", out var q) ? q.GetInt32() : 90;
            // 确定性取帧时刻（秒）；缺省则不做动画覆盖，走元素自身样式
            double? animationTime = root.TryGetProperty("animationTime", out var at) ? at.GetDouble() : null;
            // 展平快照（浏览器算好的 used px）：跳过 AngleSharp 的第二轮层叠（GetDeclarations），实测省 ~1.4ms/元素
            bool precomputed = root.TryGetProperty("precomputed", out var pc) && pc.ValueKind == JsonValueKind.True;

            byte[] bytes = format switch
            {
                "png" => Renderer.RenderPng(html, css, width, scale: scale, animationTime: animationTime, precomputed: precomputed),
                "jpeg" => Renderer.RenderImage(html, css, width, SKEncodedImageFormat.Jpeg, quality, scale: scale, animationTime: animationTime, precomputed: precomputed),
                "webp" => Renderer.RenderImage(html, css, width, SKEncodedImageFormat.Webp, quality, scale: scale, animationTime: animationTime, precomputed: precomputed),
                "svg" => Renderer.RenderSvg(html, css, width, animationTime: animationTime, precomputed: precomputed),
                "pdf" => Renderer.RenderPdf(html, css,
                    GetStr(root, "pageSize").Equals("letter", StringComparison.OrdinalIgnoreCase) ? PageSize.Letter : PageSize.A4,
                    animationTime: animationTime, precomputed: precomputed),
                "ico" => Renderer.RenderIco(html, css,
                    root.TryGetProperty("icoSize", out var ic) ? ic.GetInt32() : 32, width, precomputed: precomputed),
                _ => throw new ArgumentException($"unsupported format: {format}"),
            };
            return Ok(Convert.ToBase64String(bytes));
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    /// <summary>
    /// 安装引擎的字形级兜底子集字体（FontManager 固定到 AppContext.BaseDirectory/assets/fonts/YaheiSub-*.ttf）。
    /// 必须在首次 Render 前调用：EnsureSubsetLoaded 只尝试一次，之后所有未注册 family 的文本
    /// 都落到该兜底字体——wasm 没有系统字体，不装则未知 font-family 的中文出豆腐块。
    /// weight &gt;= 700 安装为 bold，否则 regular。
    /// </summary>
    [JSExport]
    public static string InstallSubsetFont(string base64Data, int weight)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "assets", "fonts");
            Directory.CreateDirectory(dir);
            var name = weight >= 700 ? "YaheiSub-Bold.ttf" : "YaheiSub-Regular.ttf";
            File.WriteAllBytes(Path.Combine(dir, name), Convert.FromBase64String(base64Data));
            return Ok(name);
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    /// <summary>注册一条字体（base64 的 ttf/otf）；返回登记后的 MEMFS 路径。</summary>
    [JSExport]
    public static string RegisterFont(string family, string base64Data, int weight)
    {
        try
        {
            Directory.CreateDirectory(FontDir);
            var safe = string.Concat(family.Where(char.IsLetterOrDigit).ToArray());
            var path = Path.Combine(FontDir, $"{safe}-{weight}.ttf");
            File.WriteAllBytes(path, Convert.FromBase64String(base64Data));
            FontManager.RegisterFont(family, path, weight);
            return Ok(path);
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    [JSExport]
    public static string Info()
        => Ok($"{{\"papersharp\":\"{typeof(Renderer).Assembly.GetName().Version}\",\"runtime\":\"{Environment.Version}\",\"wasm\":true}}");

    static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    static string Ok(string payload)
        => "{\"ok\":true,\"data\":" + JsonSerializer.Serialize(payload) + "}";

    static string Fail(Exception e)
    {
        var msg = $"{e.GetType().Name}: {e.Message}";
        if (e.InnerException != null) msg += " <- " + e.InnerException.Message;
        return "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(msg) + "}";
    }
}
