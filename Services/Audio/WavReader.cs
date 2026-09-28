using System.Buffers.Binary;

namespace readSounds.Services.Audio;

/// <summary>Параметры аудиопотока WAV.</summary>
public readonly record struct WavFormat(int Channels, int SampleRate, int BitsPerSample, ushort FormatTag)
{
    public int BytesPerSample => Math.Max(1, BitsPerSample / 8);

    public int BlockAlign => Channels * BytesPerSample;

    public int ByteRate => SampleRate * BlockAlign;

    public TimeSpan DurationFor(long dataBytes) =>
        ByteRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)dataBytes / ByteRate);

    public string Describe() =>
        $"{SampleRate} Гц, {Channels} кан., {BitsPerSample} бит";
}

/// <summary>Разобранный WAV: параметры плюс расположение полезных данных.</summary>
public sealed class WavFile(WavFormat format, byte[] formatChunk, long dataOffset, long dataLength)
{
    public WavFormat Format { get; } = format;

    /// <summary>
    /// Полная нагрузка чанка <c>fmt</c> вместе с возможным расширением
    /// (WAVE_FORMAT_EXTENSIBLE). Сохраняем как есть, чтобы склейка не потеряла биты.
    /// </summary>
    public byte[] FormatChunk { get; } = formatChunk;

    /// <summary>Смещение первого байта PCM-данных в файле.</summary>
    public long DataOffset { get; } = dataOffset;

    public long DataLength { get; } = dataLength;

    public TimeSpan Duration => Format.DurationFor(DataLength);
}

/// <summary>Чтение WAV. Склеивание идёт по сырому PCM, без перекодирования.</summary>
public static class WavReader
{
    public static WavFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("Поток должен поддерживать позиционирование.", nameof(stream));
        }

        if (!Matches(ReadBytes(stream, 4), "RIFF"u8))
        {
            throw new InvalidDataException("Это не WAV: нет сигнатуры RIFF.");
        }

        Skip(stream, 4); // ChunkSize
        if (!Matches(ReadBytes(stream, 4), "WAVE"u8))
        {
            throw new InvalidDataException("Это не WAV: нет типа WAVE.");
        }

        byte[]? formatChunk = null;
        long dataOffset = -1;
        long dataLength = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = ReadBytes(stream, 4);
            var chunkSize = ReadUInt32(stream);
            long payloadStart = stream.Position;
            long payloadEnd = payloadStart + chunkSize;

            if (Matches(chunkId, "fmt "u8))
            {
                formatChunk = ReadBytes(stream, (int)Math.Min(chunkSize, int.MaxValue));
            }
            else if (Matches(chunkId, "data"u8))
            {
                dataOffset = payloadStart;
                // Размер из заголовка может быть больше файла, если запись оборвалась.
                dataLength = Math.Min(chunkSize, stream.Length - payloadStart);
            }

            if (payloadEnd > stream.Length)
            {
                break;
            }

            // После чанка нечётного размера идёт выравнивающий байт.
            stream.Position = payloadEnd + (chunkSize & 1);
        }

        if (formatChunk is null)
        {
            throw new InvalidDataException("В WAV нет чанка 'fmt '.");
        }

        if (dataOffset < 0)
        {
            throw new InvalidDataException("В WAV нет чанка 'data'.");
        }

        if (formatChunk.Length < 16)
        {
            throw new InvalidDataException("Чанк 'fmt ' повреждён: меньше 16 байт.");
        }

        return new WavFile(ParseFormat(formatChunk), formatChunk, dataOffset, dataLength);
    }

    private static WavFormat ParseFormat(byte[] fmt) => new(
        Channels: BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2)),
        SampleRate: (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4, 4)),
        BitsPerSample: BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2)),
        FormatTag: BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2)));

    private static bool Matches(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected) =>
        actual.SequenceEqual(expected);

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static void Skip(Stream stream, int count) => stream.Position += count;
}
