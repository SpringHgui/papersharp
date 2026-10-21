namespace PdfBench.Runner;

/// <summary>所有还原适配器共享的输入：spec 目录（challenge.html + assets）与输出目录。</summary>
public sealed record AdapterContext(string SpecDir, string OutDir)
{
    public string ChallengeHtml => Path.Combine(SpecDir, "challenge.html");
    public string AssetsDir => Path.Combine(SpecDir, "assets");
}

/// <summary>
/// 一个参赛库 = 一个实现本接口的类（放在 Adapters/ 下，反射自动发现）。
/// Generate 必须把 challenge.html 的 3 页 A4 内容还原为 outPdfPath 指向的 PDF；
/// 库不支持的特性按 truth 的占位约定渲染（灰色斜叉块），并在特性表中标 ❌。
/// </summary>
public interface IPdfAdapter
{
    /// <summary>输出文件名（out/{Name}.pdf），如 "questpdf"。</summary>
    string Name { get; }
    void Generate(AdapterContext ctx, string outPdfPath);
}
