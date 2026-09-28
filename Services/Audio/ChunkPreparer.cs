using System.Buffers.Binary;
using System.Text;

namespace readSounds.Services.Audio;

/// <summary>Результат подготовки куска перед склейкой.</summary>
/// <param name="Audio">WAV с обрезанными краями и коротким затуханием.</param>
/// <param name="Duration">Длительность после обрезки.</param>
/// <param name="TrimmedLeadingMs">Сколько тишины убрано в начале.</param>
/// <param name="TrimmedTrailingMs">Сколько тишины убрано в конце.</param>
/// <param name="SpeechCharsPerSecond">
/// Знаков речи в секунду. Норма чтения вслух — примерно 12..17. Сильное отклонение
/// означает, что кусок либо «проглочен», либо модель договорила лишнего.
/// </param>
public sealed record PreparedChunk(
    MemoryStream Audio,
    TimeSpan Duration,
    int TrimmedLeadingMs,
    int TrimmedTrailingMs,
    double SpeechCharsPerSecond)
{
    public bool LooksSuspicious => SpeechCharsPerSecond is < 6 or > 26;
}

/// <summary>
/// Готовит кусок, полученный от XTTS, к склейке с соседями.
/// </summary>
/// <remarks>
/// <para>
/// XTTS почти всегда отдаёт кусок с тишиной по краям. Если склеивать как есть,
/// на стыке получается тишина конца предыдущего куска плюс тишина начала следующего —
/// это и есть «лишние звуки», которые слышны между фразами. Здесь эта тишина
/// срезается, оставляя небольшой запас, чтобы не съесть начало звука.
/// </para>
/// <para>
/// Срезанные края закрываются коротким затуханием (5 мс). Без него последний отсчёт
/// куска и первый отсчёт следующего — независимые значения, и на стыке возникает
/// скачок, который слышен как щелчок. Затухание сводит стык к нулю.
/// </para>
/// <para>
/// Преобразование безопасно для продолжения работы: результат — обычный PCM того же
/// формата, а смещения в <c>project.json</c> обновляются как обычно.
/// </para>
/// </remarks>
public static class ChunkPreparer
{
    /// <summary>Порог «тишины». Около −42 дБФС: ниже этого уже не речь.</summary>
    private const int SilenceThreshold = 250;

    /// <summary>Сколько тишины оставляем у края, чтобы не срезать начало звука.</summary>
    private const int KeepMarginMs = 15;

    /// <summary>Длина затухания на срезанном крае.</summary>
    private const int FadeMs = 5;

    public static PreparedChunk Prepare(Stream wav, int speechChars)
    {
        ArgumentNullException.ThrowIfNull(wav);

        wav.Position = 0;
        var info = WavReader.Read(wav);

        // Обрезать умеем только 16-битный PCM. Остальное склеиваем как пришло.
        if (info.Format.FormatTag != 1 || info.Format.BitsPerSample != 16)
        {
            var copy = new MemoryStream();
            wav.Position = 0;
            wav.CopyTo(copy);
            copy.Position = 0;
            return new PreparedChunk(
                copy, info.Duration, 0, 0, Rate(speechChars, info.Duration));
        }

        var frameCount = info.DataLength / 2;
        var samples = new short[frameCount];
        var buffer = new byte[info.DataLength];
        wav.Position = info.DataOffset;
        wav.ReadExactly(buffer);
        for (var i = 0; i < frameCount; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2, 2));
        }

        var rate = info.Format.SampleRate;
        var margin = rate * KeepMarginMs / 1000;
        var first = FirstAudibleSample(samples);
        var last = LastAudibleSample(samples);

        // Кусок целиком тихий — резать нечего, но это повод сообщить.
        if (first < 0)
        {
            var silent = BuildWav(info.Format, samples);
            return new PreparedChunk(silent, info.Duration, 0, 0, 0);
        }

        // Оставляем запас, чтобы не отрезать взрывной согласный.
        var from = Math.Max(0, first - margin);
        var to = Math.Min(samples.Length, last + margin);

        var trimmed = samples[from..to];
        ApplyFade(trimmed, rate);

        var duration = TimeSpan.FromSeconds(trimmed.Length / (double)rate);
        return new PreparedChunk(
            BuildWav(info.Format, trimmed),
            duration,
            (from * 1000) / rate,
            ((samples.Length - to) * 1000) / rate,
            Rate(speechChars, duration));
    }

    private static double Rate(int chars, TimeSpan duration) =>
        duration.TotalSeconds <= 0.01 ? 0 : chars / duration.TotalSeconds;

    /// <summary>Первый отсчёт громче порога, иначе −1.</summary>
    /// <remarks>
    /// Ищем просто первый громкий отсчёт, а не «первый после тишины». Если кусок
    /// начинается сразу со звука, правильный ответ — 0; при прежнем условии такой
    /// кусок ошибочно считался полностью тихим, оставался без затухания (то есть
    /// со щелчком на стыке) и попадал в подозрительные.
    /// </remarks>
    private static int FirstAudibleSample(short[] samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            if (Math.Abs((int)samples[i]) > SilenceThreshold)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Последний отсчёт громче порога, иначе −1.</summary>
    private static int LastAudibleSample(short[] samples)
    {
        for (var i = samples.Length - 1; i >= 0; i--)
        {
            if (Math.Abs((int)samples[i]) > SilenceThreshold)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Плавное затухание на обоих краях — щелчок на стыке не появляется.</summary>
    private static void ApplyFade(short[] samples, int rate)
    {
        var fade = Math.Min(rate * FadeMs / 1000, samples.Length / 2);
        for (var i = 0; i < fade; i++)
        {
            var gain = (double)i / fade;
            samples[i] = (short)(samples[i] * gain);
            samples[samples.Length - 1 - i] = (short)(samples[samples.Length - 1 - i] * gain);
        }
    }

    private static MemoryStream BuildWav(WavFormat format, short[] samples)
    {
        var dataSize = samples.Length * 2;
        var ms = new MemoryStream(dataSize + 44);

        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + dataSize);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write(format.FormatTag);
            w.Write((ushort)format.Channels);
            w.Write(format.SampleRate);
            w.Write(format.ByteRate);
            w.Write((ushort)format.BlockAlign);
            w.Write((ushort)format.BitsPerSample);
            w.Write("data"u8);
            w.Write(dataSize);
            foreach (var s in samples)
            {
                w.Write(s);
            }
        }

        ms.Position = 0;
        return ms;
    }
}
