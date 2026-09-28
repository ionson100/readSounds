#if WINDOWS
using NAudio.Wave;

namespace readSounds.Services.Playback;

/// <summary>
/// Воспроизведение готового WAV-файла через NAudio.
/// </summary>
/// <remarks>
/// В Windows-сборке используется waveOut из NAudio.WinMM. Если устройства вывода
/// нет, <see cref="IsAvailable"/> возвращает false — интерфейс в этом случае
/// предлагает открыть файл системным плеером.
/// </remarks>
public sealed class AudioPlayer : IAudioPlayer, IDisposable
{
    private WaveOut? _output;
    private AudioFileReader? _reader;
    private PlaybackState _state = PlaybackState.Stopped;
    private bool _disposed;

    public AudioPlayer()
    {
        try
        {
            // Пробуем поднять устройство вывода. Шрокий catch здесь намеренный:
            // это проверка возможности, и на машине без звука стек может бросить что угодно
            // (MmException, DllNotFound, NotSupported) — все варианты означают одно и то же.
            using var probe = new WaveOut();
            IsAvailable = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            IsAvailable = false;
        }
    }

    public bool IsAvailable { get; }

    public PlaybackState State => _state;

    public TimeSpan Position
    {
        get
        {
            try
            {
                return _reader?.CurrentTime ?? TimeSpan.Zero;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
            {
                return TimeSpan.Zero;
            }
        }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public event EventHandler? StateChanged;

    public void Play(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsAvailable)
        {
            throw new NotSupportedException("Аудиоустройство вывода недоступно.");
        }

        Stop();

        _reader = new AudioFileReader(path);
        _output = new WaveOut();
        _output.Init(_reader);
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Play();

        SetState(PlaybackState.Playing);
    }

    public void Pause()
    {
        if (_state != PlaybackState.Playing)
        {
            return;
        }

        _output?.Pause();
        SetState(PlaybackState.Paused);
    }

    public void Resume()
    {
        if (_state != PlaybackState.Paused)
        {
            return;
        }

        _output?.Play();
        SetState(PlaybackState.Playing);
    }

    public void Stop()
    {
        var output = _output;
        var reader = _reader;
        _output = null;
        _reader = null;

        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                output.Stop();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or DllNotFoundException)
            {
                // Устройство уже исчезло — просто освобождаем ресурсы.
            }

            output.Dispose();
        }

        reader?.Dispose();

        if (_state != PlaybackState.Stopped)
        {
            SetState(PlaybackState.Stopped);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => SetState(PlaybackState.Stopped);

    private void SetState(PlaybackState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
#else
namespace readSounds.Services.Playback;

/// <summary>
/// Заглушка воспроизведения для Linux.
/// </summary>
/// <remarks>
/// <para>
/// Сборка переносимая, и .deb в том числе, поэтому waveOut из NAudio.WinMM
/// в ней недоступен. Диапазон X11 в Linux — это ALSA или PulseAudio, а в
/// контейнере XTTS звуковой карты обычно нет вовсе.
/// </para>
/// <para>
/// Отказываем честно, а не падаем: <see cref="IsAvailable"/> равен false, интерфейс
/// прячет кнопки прослушивания и предлагает открыть готовый WAV системным
/// плеером. Синтез, разбивка, склейка и продолжение работы на Linux
/// полностью доступны — недоступно только проигрывание.
/// </para>
/// </remarks>
public sealed class AudioPlayer : IAudioPlayer, IDisposable
{
    public bool IsAvailable => false;

    public PlaybackState State => PlaybackState.Stopped;

    public TimeSpan Position => TimeSpan.Zero;

    public TimeSpan Duration => TimeSpan.Zero;

    /// <summary>Событие объявлено ради полноты интерфейса, но не вызывается никогда.</summary>
    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public void Play(string path) =>
        throw new NotSupportedException(
            "В этой сборке прослушивание недоступно: оно работает через waveOut, " +
            "который есть только в Windows. Откройте готовый файл системным плеером.");

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
#endif
