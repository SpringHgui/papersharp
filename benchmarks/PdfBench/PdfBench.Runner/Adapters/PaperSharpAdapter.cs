namespace PdfBench.Runner.Adapters;

public sealed class PaperSharpAdapter : HtmlAdapterBase
{
    public override string Name => "papersharp";

    public override void Generate(AdapterContext ctx, string outPdfPath)
    {
        var pdf = global::PaperSharp.Renderer.RenderPdf(ReadHtml(ctx), "", global::PaperSharp.PageSize.A4, baseDir: ctx.SpecDir);
        File.WriteAllBytes(outPdfPath, pdf);
    }
}
