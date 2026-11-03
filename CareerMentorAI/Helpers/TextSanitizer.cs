namespace CareerMentorAI.Helpers;

public static class TextSanitizer
{
    public static string Clean(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        var cleaned = new string(s.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray());

        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[ \t]+", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\r\n|\r|\n", "\n");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(\n\s*){3,}", "\n\n"); 

        return cleaned.Trim();
    }
}
