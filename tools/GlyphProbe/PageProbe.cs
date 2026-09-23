using UglyToad.PdfPig;

namespace GlyphProbe;

public static class PageProbe
{
    public static void Run(string pdfPath)
    {
        using var doc = PdfDocument.Open(pdfPath);
        Console.WriteLine($"pages={doc.NumberOfPages}");
        for (int i = 1; i <= doc.NumberOfPages; i++)
        {
            var text = doc.GetPage(i).Text.Replace("\n", " ").Trim();
            Console.WriteLine($"p{i} len={text.Length} head=\"{text[..Math.Min(60, text.Length)]}\"");
        }
    }
}
