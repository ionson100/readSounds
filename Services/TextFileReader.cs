using System.Security.Cryptography;
using System.Text;

namespace readSounds.Services;

/// <summary>Прочитанный исходный текст вместе с его отпечатком.</summary>
public sealed record SourceText(string Text, string Sha256, int CharCount, string EncodingName);

/// <summary>
/// Чтение текстового файла с определением кодировки.
/// </summary>
/// <remarks>
/// Порядок: BOM → строгий UTF-8 → Windows-1251 → Latin-1. Русские книги часто лежат
/// в 1251, и без этого шага текст превратится в мусор ещё до отправки в XTTS.
/// Отпечаток считается по UTF-8 байтам уже декодированного текста, поэтому одинаковые
/// тексты в разных кодировках дают одинаковый хеш.
/// </remarks>
public static class TextFileReader
{
    public static SourceText Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var bytes = File.ReadAllBytes(path);
        var (encoding, preambleLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);

        // Снимаем BOM, если декодер его не съел.
        text = text.TrimStart('\uFEFF');

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

        return new SourceText(normalized, hash, normalized.Length, encoding.WebName);
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (HasPrefix(bytes, [0xEF, 0xBB, 0xBF]))
        {
            return (new UTF8Encoding(false), 3);
        }

        if (HasPrefix(bytes, [0xFF, 0xFE]))
        {
            return (new UnicodeEncoding(false, false), 2);
        }

        if (HasPrefix(bytes, [0xFE, 0xFF]))
        {
            return (new UnicodeEncoding(true, false), 2);
        }

        // Строгий UTF-8 бросает на любых невалидных байтах — это и есть проверка.
        var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        try
        {
            _ = strictUtf8.GetString(bytes);
            return (strictUtf8, 0);
        }
        catch (DecoderFallbackException)
        {
            // Не UTF-8. Дальше — однобайтовые кодировки.
        }

        try
        {
            var cyrillic = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            _ = cyrillic.GetString(bytes);
            return (cyrillic, 0);
        }
        catch (DecoderFallbackException)
        {
            // Не 1251 — последний вариант, который не бросает.
        }

        return (Encoding.Latin1, 0);
    }

    private static bool HasPrefix(byte[] bytes, byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
