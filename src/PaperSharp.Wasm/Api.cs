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

            byte[] bytes = format switch
            {
                "png" => Renderer.RenderPng(html, css, width, scale: scale),
                "jpeg" => Renderer.RenderImage(html, css, width, SKEncodedImageFormat.Jpeg, quality, scale: scale),
                "webp" => Renderer.RenderImage(html, css, width, SKEncodedImageFormat.Webp, quality, scale: scale),
                "svg" => Renderer.RenderSvg(html, css, width),
                "pdf" => Renderer.RenderPdf(html, css,
                    GetStr(root, "pageSize").Equals("letter", StringComparison.OrdinalIgnoreCase) ? PageSize.Letter : PageSize.A4),
                "ico" => Renderer.RenderIco(html, css,
                    root.TryGetProperty("icoSize", out var ic) ? ic.GetInt32() : 32, width),
                _ => throw new ArgumentException($"unsupported format: {format}"),
            };
            return Ok(Convert.ToBase64String(bytes));
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
