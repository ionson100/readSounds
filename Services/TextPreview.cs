namespace readSounds.Services;

/// <summary>Укорачивает текст для показа в одну строку.</summary>
public static class TextPreview
{
    public static string Shorten(string? text, int maxLength = 60)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var single = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= maxLength ? single : single[..maxLength] + "…";
    }
}
