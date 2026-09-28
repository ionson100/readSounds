namespace readSounds.Services.Playback;

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>Воспроизведение готового WAV-файла.</summary>
public interface IAudioPlayer
{
    /// <summary>Есть ли в системе рабочее аудиоустройство.</summary>
    bool IsAvailable { get; }

    PlaybackState State { get; }

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    event EventHandler? StateChanged;

    void Play(string path);

    void Pause();

    void Resume();

    void Stop();
}
