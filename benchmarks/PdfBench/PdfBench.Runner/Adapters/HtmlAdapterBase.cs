namespace PdfBench.Runner.Adapters;

/// <summary>直接吃 challenge.html 的选手共用读取逻辑。</summary>
public abstract class HtmlAdapterBase : IPdfAdapter
{
    public abstract string Name { get; }
    public abstract void Generate(AdapterContext ctx, string outPdfPath);

    protected static string ReadHtml(AdapterContext ctx) => File.ReadAllText(ctx.ChallengeHtml);
    protected static string FileUrl(AdapterContext ctx) => new Uri(ctx.ChallengeHtml).AbsoluteUri;
}
