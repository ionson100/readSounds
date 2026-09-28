using System.Text;

namespace readSounds.Services;

/// <summary>Результат нормализации: сам текст и число замен.</summary>
public readonly record struct NormalizationResult(string Text, int Replacements)
{
    public bool Changed => Replacements > 0;
}

/// <summary>
/// Приводит типографские символы к тем, которые понимает XTTS.
/// </summary>
/// <remarks>
/// <para>
/// Чистка на стороне сервера вырезает <b>только ASCII-кавычки</b> <c>"</c>:
/// <c>text.replace('"', "")</c>. Типографские кавычки она не трогает, а в словаре
/// модели их нет — значит каждая уходит в <c>[UNK]</c> и становится лишним шумом
/// в озвучке. Заменяя их на ASCII, мы добиваемся того, что сервер вырежет их
/// совсем: это лучше, чем просто убрать символы самому.
/// </para>
/// <para>
/// Остальные замены орфографически нейтральны: тире становятся дефисом,
/// неразрывные и узкие пробелы — обычными, невидимые символы (мягкий перенос,
/// zero-width, BOM) удаляются. Мягкий перенос удаляется намеренно: он ставится
/// для переноса по строкам и в озвучке только мешает.
/// </para>
/// <para>
/// Нормализация применяется к каждой части <b>перед отправкой</b>, а не к
/// исходнику: файл в проекте остаётся вашим, разбивка на части и состояние
/// продолжения не меняются.
/// </para>
/// <para>
/// Знаки ударения сюда <b>намеренно не входят</b>: XTTS их всё равно не читает,
/// они превращаются в <c>[UNK]</c> и на произношение не влияют.
/// </para>
/// </remarks>
public static class TextNormalizer
{
    /// <summary>
    /// Видимые символы — литералами: их в исходнике видно и правдоподобно проверить.
    /// Повторы допустимы, одна и та же буква может встретиться дважды.
    /// </summary>
    private static readonly (char From, string To)[] VisibleRules =
    [
        // Типографские двойные кавычки -> ASCII, чтобы сервер вырезал их совсем.
        ('“', "\""),  // левая английская
        ('”', "\""),  // правая английская
        ('„', "\""),  // низкая
        ('‟', "\""),  // обращённая
        ('«', "\""),  // ёлочки левая
        ('»', "\""),  // ёлочки правая
        ('‹', "\""),  // одинарная левая
        ('›', "\""),  // одинарная правая

        // Одинарные типографские -> ASCII.
        ('‘', "'"),  // левая английская
        ('’', "'"),  // правая английская
        ('‚', "'"),  // низкая
        ('‛', "'"),  // обращённая
        ('′', "'"),  // штрих
        ('‵', "'"),  // обратный штрих
        ('´', "'"),  // острый акцент

        // Тире -> дефис.
        ('–', "-"),  // en dash
        ('—', "-"),  // em dash
        ('―', "-"),  // горизонтальная черта
        ('‒', "-"),  // figure dash
        ('−', "-"),  // минус
    ];

    /// <summary>
    /// Невидимые символы заданы кодовыми точками. Литералами их писать нельзя:
    /// такие символы невозможно увидеть в исходнике, они молча теряются при
    /// копировании, и таблица превращается в синтаксическую ошибку.
    /// </summary>
    private static readonly (int Code, string To)[] InvisibleRules =
    [
        (0x00A0, " "),  // неразрывный пробел
        (0x202F, " "),  // узкий неразрывный пробел
        (0x2007, " "),  // цифровая ширина
        (0x2009, " "),  // тонкий пробел
        (0x200A, " "),  // волосяной пробел
        (0x00AD, ""),   // мягкий перенос
        (0x200B, ""),   // zero-width space
        (0x200C, ""),   // zero-width non-joiner
        (0x200D, ""),   // zero-width joiner
        (0x2060, ""),   // word joiner
        (0xFEFF, ""),   // BOM
    ];

    private static readonly Dictionary<char, string> Map = BuildMap();

    private static Dictionary<char, string> BuildMap()
    {
        var map = new Dictionary<char, string>();

        foreach (var (from, to) in VisibleRules)
        {
            map[from] = to;
        }

        foreach (var (code, to) in InvisibleRules)
        {
            map[(char)code] = to;
        }

        return map;
    }

    /// <summary>Заменить типографские символы на понятные модели.</summary>
    public static NormalizationResult Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!NeedsNormalization(text))
        {
            return new NormalizationResult(text, 0);
        }

        var builder = new StringBuilder(text.Length);
        var replaced = 0;

        foreach (var c in text)
        {
            if (Map.TryGetValue(c, out var replacement))
            {
                builder.Append(replacement);
                replaced++;
            }
            else
            {
                builder.Append(c);
            }
        }

        return new NormalizationResult(builder.ToString(), replaced);
    }

    /// <summary>Посчитать, сколько замен будет сделано, ничего не переписывая.</summary>
    public static int CountReplacements(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var c in text)
        {
            if (Map.ContainsKey(c))
            {
                count++;
            }
        }

        return count;
    }

    private static bool NeedsNormalization(string text)
    {
        foreach (var c in text)
        {
            if (Map.ContainsKey(c))
            {
                return true;
            }
        }

        return false;
    }
}
