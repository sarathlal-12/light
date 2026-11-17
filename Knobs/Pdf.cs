using System.IO;
using System.Text;

namespace DotLight.Knobs;

/// <summary>A tiny PDF writer: one JPEG image per page, page sized to the image (shrunk to fit A4).</summary>
static class Pdf
{
    public static void Write(string path, List<(byte[] Jpeg, int W, int H)> pages)
    {
        using var fs = File.Create(path);
        var offsets = new List<long>();
        void Text(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }
        void Begin(int id) { offsets.Add(fs.Position); Text($"{id} 0 obj\n"); }

        Text("%PDF-1.4\n");
        int n = pages.Count;
        // objects: 1 catalog, 2 pages, then per page: page, content, image
        Begin(1); Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + i * 3} 0 R"));
        Begin(2); Text($"<< /Type /Pages /Kids [{kids}] /Count {n} >>\nendobj\n");

        for (int i = 0; i < n; i++)
        {
            var (jpeg, w, h) = pages[i];
            // 96 dpi pixels → points, then shrink to fit an A4 page (either way round).
            double pw = w * 0.75, ph = h * 0.75;
            double fit = Math.Min(1, Math.Min(Math.Max(595, 842) / Math.Max(pw, ph), Math.Min(595, 842) / Math.Min(pw, ph)));
            pw = Math.Round(pw * fit, 2); ph = Math.Round(ph * fit, 2);
            int page = 3 + i * 3, content = page + 1, image = page + 2;
            string P(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

            Begin(page);
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {P(pw)} {P(ph)}] /Resources << /XObject << /Im{i} {image} 0 R >> >> /Contents {content} 0 R >>\nendobj\n");
            var draw = $"q {P(pw)} 0 0 {P(ph)} 0 0 cm /Im{i} Do Q";
            Begin(content); Text($"<< /Length {draw.Length} >>\nstream\n{draw}\nendstream\nendobj\n");
            Begin(image);
            Text($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            fs.Write(jpeg, 0, jpeg.Length);
            Text("\nendstream\nendobj\n");
        }

        long xref = fs.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Text($"{o:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
