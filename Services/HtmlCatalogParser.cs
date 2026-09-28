using System.Text.RegularExpressions;
using readSounds.Models;

namespace readSounds.Services;

/// <summary>
/// Достаёт список голосов и языков из HTML главной страницы <c>tts-server</c>.
/// </summary>
/// <remarks>
/// JSON-эндпоинта для списков у XTTS нет: <c>tts-server</c> подставляет их прямо в шаблон
/// из конфига модели. Поэтому парсим <c>&lt;select id="speaker_id"&gt;</c> и
/// <c>&lt;select id="language_id"&gt;</c>. Если у модели один голос, селекта не будет —
/// это нормально, вернёмся к значениям по умолчанию.
/// </remarks>
public static partial class HtmlCatalogParser
{
    [GeneratedRegex(
        """<select[^>]*\bid\s*=\s*["'](?<id>[^"']+)["'][^>]*>(?<body>.*?)</select>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SelectRegex();

    [GeneratedRegex(
        """<option[^>]*\bvalue\s*=\s*["'](?<value>[^"']*)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex OptionRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.None)]
    private static partial Regex WhitespaceRegex();

    public static TtsCatalog Parse(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return TtsCatalog.Empty;
        }

        List<string> speakers = [];
        List<string> languages = [];

        foreach (Match select in SelectRegex().Matches(html))
        {
            var values = ExtractValues(select.Groups["body"].Value);

            switch (select.Groups["id"].Value)
            {
                case "speaker_id":
                    speakers = values;
                    break;
                case "language_id":
                    languages = values;
                    break;
            }
        }

        return new TtsCatalog { Speakers = speakers, Languages = languages };
    }

    /// <summary>Готовит тело ответа об ошибке к показанию: без тегов, одной строкой, обрезанное.</summary>
    public static string ExtractText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = System.Net.WebUtility.HtmlDecode(TagRegex().Replace(html, " "));
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private static List<string> ExtractValues(string selectBody) =>
        OptionRegex().Matches(selectBody)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups["value"].Value).Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
