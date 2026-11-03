using System.Text;
using UglyToad.PdfPig;

namespace CareerMentorAI.Helpers
{
    public static class ExtractToPdfText
    {
        public static string ExtractPdfText(Stream pdfStream)
        {
            using var doc = PdfDocument.Open(pdfStream);
            var sb = new StringBuilder();
            foreach (var page in doc.GetPages())
            {
                sb.AppendLine(page.Text);
            }
            return sb.ToString();
        }
    }
}
