using System.Text.Json.Serialization;

namespace readSounds.Models;

/// <summary>
/// Настройки синтеза, которые хранятся в проекте: язык, голос и предел длины одной части.
/// </summary>
public sealed class ProjectOptions
{
    public const int DefaultMaxChunkLength = 200;
    public const int MinMaxChunkLength = 20;
    public const int MaxMaxChunkLength = 2000;

    /// <summary>Код языка из списка XTTS, например "ru" или "en".</summary>
    public string LanguageId { get; set; } = "en";

    /// <summary>Имя голоса из списка XTTS, например "Claribel Dervla". Пусто — голос по умолчанию.</summary>
    public string SpeakerId { get; set; } = string.Empty;

    /// <summary>
    /// Максимальная длина части в символах. Части длиннее разбиваются по точкам,
    /// а предложения без точек принудительно режутся по этому порогу.
    /// </summary>
    public int MaxChunkLength { get; set; } = DefaultMaxChunkLength;

    [JsonIgnore]
    public int EffectiveMaxChunkLength => Math.Clamp(MaxChunkLength, MinMaxChunkLength, MaxMaxChunkLength);

    /// <summary>
    /// Отпечаток исходных данных и настроек. Если он изменился, а синтез уже начат,
    /// ранее склеенное аудио больше не совместимо с новыми настройками и проект надо перезапустить.
    /// </summary>
    public string BuildSignature(string sourceHash) =>
        $"{sourceHash}|{EffectiveMaxChunkLength}|{LanguageId}|{SpeakerId}";

    public ProjectOptions Clone() => new()
    {
        LanguageId = LanguageId,
        SpeakerId = SpeakerId,
        MaxChunkLength = MaxChunkLength,
    };
}
