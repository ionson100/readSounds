namespace readSounds.Services;

/// <summary>Граница, на которой можно закончить часть.</summary>
/// <param name="End">Позиция сразу за последним знаком.</param>
/// <param name="IsSentence">
/// <see langword="false"/>, если это конец текста без завершающего знака: резать там
/// приходится по необходимости, и такую часть надо честно отметить как жёсткий рез.
/// </param>
public readonly record struct ChunkBoundary(int End, bool IsSentence);

/// <summary>Готовая к отправке часть текста.</summary>
/// <param name="Text">Собственно текст части.</param>
/// <param name="WasHardSplit">
/// <see langword="true"/>, если границу предложения найти было негде и часть
/// пришлось разрезать по пробелу. Такие части звучат хуже всех и их видно в списке.
/// </param>
public sealed record TextChunk(string Text, bool WasHardSplit);

/// <summary>
/// Разбивает текст на части для отправки в XTTS.
/// </summary>
/// <remarks>
/// <para>
/// Порог <c>maxChunkLength</c> — мягкий. Главное правило: <b>не разрывать
/// предложение</b>. Лучше отдать часть длиннее порога, чем озвучить обрывок фразы,
/// потому что интонация на стыке слышна и смысл теряется.
/// </para>
/// <para>Порядок выбора границы для части, начинающейся в позиции <c>start</c>:
/// <list type="number">
/// <item>первая точка в окне от порога до порога, умноженного на
/// <see cref="MaxOvershootFactor"/> — часть длиннее порога (например 245 при 200);</item>
/// <item>если в этом окне точек нет — последняя точка до порога, часть короче
/// порога (например 136);</item>
/// <item>если точек нет вовсе — разрез по ближайшему пробелу у порога,
/// иначе строго по порогу, чтобы не отправить в XTTS мегабайт текста разом.</item>
/// </list>
/// </para>
/// <para>
/// Верхняя граница окна нужна потому, что без неё первое правило перебивало бы
/// второе всегда: граница в конце текста существует по определению, и ждать точки
/// «сколько угодно» нельзя — одна фраза на пять тысяч символов стала бы одной
/// «частью». Поэтому ждать готовы не дольше чем в <see cref="MaxOvershootFactor"/>
/// раза длиннее порога, а дальше режем по последней доступной точке.
/// </para>
/// <para>
/// Границами считаются точка, <c>!</c>, <c>?</c>, <c>…</c> и перенос строки.
/// Сокращения и инициалы («г. Москве», «И. Иванов», «т.д.», «стр. 45») и точка
/// внутри числа («3.14») границами не считаются.
/// </para>
/// </remarks>
public static class TextChunker
{
    private static readonly char[] Terminators = ['.', '!', '?', '…'];

    private static readonly char[] FallbackBreaks = [' ', ',', ';', ':', '-', '—', '–'];

    /// <summary>
    /// Во сколько раз длиннее порога мы готовы ждать точку, прежде чем смириться
    /// и резать по последней доступной границе. При пороге 200 это 400 символов.
    /// </summary>
    private const int MaxOvershootFactor = 2;

    /// <summary>
    /// Сокращения, которые в книгах встречаются часто.
    /// </summary>
    /// <remarks>
    /// Список намеренно узкий и без слов, которые сами по себе осмыслены
    /// («час», «год», «мин», «ред»): такое лучше принять за конец предложения.
    /// </remarks>
    private static readonly HashSet<string> KnownAbbreviations = new(StringComparer.Ordinal)
    {
        "ул", "пр", "корп", "кв", "стр", "рис", "табл", "прим",
        "руб", "коп", "тыс", "млн", "млрд", "сек", "гг",
        "см", "мм", "км", "кг", "ж", "жд", "им", "др", "изд", "сост",
        "акад", "доц", "проф", "нач",
    };

    /// <summary>Делит текст на части по правилам мягкого порога.</summary>
    public static List<TextChunk> Split(string text, int maxChunkLength)
    {
        ArgumentNullException.ThrowIfNull(text);

        var limit = Math.Max(1, maxChunkLength);
        var ends = FindSentenceEnds(text);
        var chunks = new List<TextChunk>();

        var start = 0;
        var index = SkipPast(ends, start, 0);

        // Начинается ли текущая часть с границы предложения. Если предыдущая часть
        // кончилась жёстким резом, эта стартует с середины фразы — и тоже испорчена,
        // даже если заканчивается точкой.
        var startIsSentence = true;

        // Пунктуация без букв копится здесь, чтобы приклеиться к следующей части.
        var pending = string.Empty;

        while (start < text.Length)
        {
            var limitAt = start + limit;

            // Горизонт отсчитываем от начала части, а не от limitAt: иначе допуск
            // накапливался бы от части к части и последние части выходили бы
            // заметно длиннее первых.
            var horizon = start + (limit * MaxOvershootFactor);

            // Ищем первый конец предложения на пороге или дальше, но не дальше горизонта.
            while (index < ends.Count && ends[index].End < limitAt)
            {
                index++;
            }

            int cut;
            bool atSentence;

            if (index < ends.Count && ends[index].End <= horizon)
            {
                // 1. Точка рядом после порога — идём до неё, лучше длиннее, чем разрыв.
                cut = ends[index].End;
                atSentence = ends[index].IsSentence;
            }
            else if (index > 0)
            {
                // 2. Дальше точек нет (или они слишком далеко) — откатываемся к последней до порога.
                var previous = ends[index - 1];
                cut = previous.End;
                atSentence = previous.IsSentence;
            }
            else
            {
                // 3. Точек нет вовсе — режем по пробелу, иначе строго по порогу.
                cut = HardCut(text, start, limit);
                atSentence = false;
            }

            if (cut <= start)
            {
                // Страховка от бесконечного цикла на любых входных данных.
                cut = Math.Min(text.Length, start + limit);
                atSentence = false;
            }

            var piece = text[start..cut].Trim();
            if (piece.Length > 0)
            {
                // Часть из одной пунктуации («–», «.») для XTTS бесполезна: модель на
                // ней выдаёт короткий щелчок либо ошибку 400. Такое приклеиваем к
                // следующей части: текст не теряется, мусор на сервер не уходит.
                if (ContainsSpeech(piece))
                {
                    chunks.Add(new TextChunk(pending + piece, !(atSentence && startIsSentence)));
                    pending = string.Empty;
                }
                else
                {
                    pending += piece;
                }
            }

            start = cut;
            startIsSentence = atSentence;
            index = SkipPast(ends, start, index);
        }

        if (pending.Length > 0)
        {
            // Хвост из одной пунктуации приклеить уже некуда — отдаём как есть.
            chunks.Add(new TextChunk(pending, true));
        }

        return chunks;
    }

    /// <summary>Есть ли в куске хоть одна буква или цифра, то есть собственно речь.</summary>
    private static bool ContainsSpeech(string text) => text.Any(char.IsLetterOrDigit);

    /// <summary>Позиции, где заканчиваются предложения (смещение за последним знаком).</summary>
    private static List<ChunkBoundary> FindSentenceEnds(string text)
    {
        var ends = new List<ChunkBoundary>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c is '\n' or '\r')
            {
                ends.Add(new ChunkBoundary(i + 1, true));

                // Схлопываем подряд идущие переносы.
                while (i + 1 < text.Length && text[i + 1] is '\n' or '\r')
                {
                    i++;
                }

                start = i + 1;
                continue;
            }

            if (Array.IndexOf(Terminators, c) < 0)
            {
                continue;
            }

            if (c == '.' && IsAbbreviation(text, i))
            {
                continue;
            }

            // «3.14» — точка внутри числа, не конец предложения.
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                continue;
            }

            ends.Add(new ChunkBoundary(i + 1, true));
            start = i + 1;
        }

        if (start < text.Length)
        {
            // Хвост без завершающего знака — резать придётся, и это не граница фразы.
            ends.Add(new ChunkBoundary(text.Length, false));
        }

        return ends;
    }

    /// <summary>Продвигает указатель за все границы, уже попавшие в начало части.</summary>
    private static int SkipPast(List<ChunkBoundary> ends, int start, int from)
    {
        while (from < ends.Count && ends[from].End <= start)
        {
            from++;
        }

        return from;
    }

    /// <summary>
    /// Разрез без границы предложения: по последнему знаку препинания или пробелу
    /// в окне порога, иначе строго по порогу. Возвращает позицию строго больше
    /// <paramref name="start"/>.
    /// </summary>
    private static int HardCut(string text, int start, int limit)
    {
        var remaining = text.Length - start;
        if (remaining <= limit)
        {
            return text.Length;
        }

        var window = text.AsSpan(start, limit);
        var cut = window.LastIndexOfAny(FallbackBreaks);
        return cut > 0 ? start + cut + 1 : start + limit;
    }

    /// <summary>Отдаёт предложения вместе с завершающим знаком и пробелами после него.</summary>
    public static IEnumerable<string> EnumerateSentences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var start = 0;
        foreach (var end in FindSentenceEnds(text))
        {
            if (end.End > start)
            {
                yield return text[start..end.End];
                start = end.End;
            }
        }
    }

    /// <summary>Похоже ли это на сокращение: «г.», «стр.», «И.», «т.д.».</summary>
    private static bool IsAbbreviation(string text, int dotIndex)
    {
        // Идём назад по слову перед точкой: буквы, цифры, точки и апострофы.
        // Пробел и любой другой знак препинания останавливают scan.
        var begin = dotIndex;
        while (begin > 0)
        {
            var c = text[begin - 1];
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '\'')
            {
                break;
            }

            begin--;
        }

        var token = text[begin..dotIndex];
        if (token.Length == 0)
        {
            return false;
        }

        // «т.д.» — составное сокращение: каждая часть короткая.
        if (token.Contains('.'))
        {
            return token.Split('.').Where(p => p.Length > 0).All(p => p.Length <= 2);
        }

        token = token.Trim('\'');
        if (token.Length == 0)
        {
            return false;
        }

        // Короткое слово: инициал «И.» или обозначение «г.», «т.», «см.», «кг.»,
        // но «45.» — это число, и точку после него разрывать нельзя.
        if (token.Length <= 2 && token.Any(char.IsLetter))
        {
            return true;
        }

        // Слова вроде «час» или «дом» — обычные слова, а не сокращения.
        // Неизвестное короткое слово считаем концом предложения: лишняя короткая
        // часть интонацию не испортит, а вот склейка потом разрежется по порогу.
        return KnownAbbreviations.Contains(token.ToLowerInvariant());
    }
}
