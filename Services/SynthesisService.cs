using readSounds.Models;
using readSounds.Services.Audio;

namespace readSounds.Services;

/// <summary>Ход работы: сколько частей готово, сколько всего и над чем идёт синтез.</summary>
public sealed record SynthesisProgress(
    int CompletedChunks,
    int TotalChunks,
    string? CurrentText,
    SynthesisStage Stage,
    int NormalizedSymbols = 0)
{
    public double Fraction => TotalChunks <= 0 ? 0d : Math.Clamp((double)CompletedChunks / TotalChunks, 0d, 1d);

    public string StatusText => Stage switch
    {
        SynthesisStage.Preparing => "Читаю текст и разбиваю на части…",
        SynthesisStage.Synthesizing =>
            $"Часть {CompletedChunks + 1} из {TotalChunks}: {TextPreview.Shorten(CurrentText)}",
        SynthesisStage.Finishing => "Завершаю файл…",
        _ => string.Empty,
    };
}

public enum SynthesisStage
{
    Preparing,
    Synthesizing,
    Finishing,
}

/// <summary>Часть, которая вышла подозрительной и которую стоит переозвучить.</summary>
/// <param name="Part">Номер части, начиная с 1.</param>
/// <param name="CharsPerSecond">Знаков речи в секунду. Норма чтения — примерно 12..17.</param>
public sealed record SuspiciousPart(int Part, double CharsPerSecond);

/// <summary>Итог прогона синтеза.</summary>
public sealed record SynthesisResult(int CompletedChunks, int TotalChunks, IReadOnlyList<SuspiciousPart> Suspicious)
{
    public static SynthesisResult Empty { get; } = new(0, 0, []);

    public bool HasSuspicious => Suspicious.Count > 0;
}

/// <summary>Прогоняет синтез проекта и умеет продолжить его с места обрыва.</summary>
public interface ISynthesisService
{
    Task<SynthesisResult> RunAsync(
        ProjectModel project, IProgress<SynthesisProgress> report, CancellationToken cancellationToken);

    /// <summary>Стирает результат и возвращает проект в состояние «не начат».</summary>
    void Reset(ProjectModel project);

    /// <summary>Отматывает проект так, чтобы следующий прогон начался с указанной части.</summary>
    void RewindTo(ProjectModel project, int partNumber);
}

/// <summary>
/// Основная логика: читает копию исходника, режет на части, по одной синтезирует
/// и дописывает в общий WAV.
/// </summary>
/// <remarks>
/// <para>
/// Состояние сохраняется после каждой части: <c>DataBytes</c> и <c>ChunkEnds</c>
/// помнят, где закончились целые данные. При следующем запуске файл обрезается до
/// отметки последней целой части, поэтому прерванный на середине кусок не попадёт
/// в результат, а работа продолжится ровно с первой неозвученной части.
/// </para>
/// <para>
/// Благодаря <c>ChunkEnds</c> можно переозвучить одну часть: файл обрезается до границы
/// предыдущей, и синтез идёт с выбранного места. Раньше такой возможности не было —
/// испорченный кусок можно было исправить только перезапуском всего проекта.
/// </para>
/// </remarks>
public sealed class SynthesisService(IProjectStore store, ITtsClient tts) : ISynthesisService
{
    public async Task<SynthesisResult> RunAsync(
        ProjectModel project, IProgress<SynthesisProgress> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(report);

        report.Report(new SynthesisProgress(project.Progress.CompletedChunks, 0, null, SynthesisStage.Preparing));

        if (!File.Exists(project.SourcePath))
        {
            throw new FileNotFoundException(
                $"В папке проекта нет исходного текста «{project.Meta.SourceFileName}».", project.SourcePath);
        }

        var source = TextFileReader.Read(project.SourcePath);
        var chunks = TextChunker.Split(source.Text, project.Options.EffectiveMaxChunkLength);

        if (chunks.Count == 0)
        {
            throw new InvalidOperationException(
                "В тексте нет ни одного непустого предложения — синтезировать нечего.");
        }

        var signature = project.Options.BuildSignature(source.Sha256);
        var resumeFrom = project.Progress.CompletedChunks;

        if (resumeFrom > 0)
        {
            EnsureCompatible(project, signature, chunks.Count);
        }
        else
        {
            project.Progress.Signature = signature;
        }

        project.Progress.TotalChunks = chunks.Count;
        project.Progress.Status = ProjectStatus.InProgress;
        project.Progress.LastError = null;
        store.Save(project);

        WavConcatWriter? writer = null;
        var normalized = 0;
        var suspicious = new List<SuspiciousPart>();

        try
        {
            if (resumeFrom > 0)
            {
                if (!File.Exists(project.AudioPath))
                {
                    throw new FileNotFoundException(
                        "Аудиофайл проекта не найден, продолжать не с чего. Начните синтез заново.", project.AudioPath);
                }

                writer = WavConcatWriter.Resume(project.AudioPath, project.Progress.DataBytes);
            }

            for (var index = resumeFrom; index < chunks.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Типографские кавычки и невидимые символы уходят на сервер как [UNK]
                // и засоряют озвучку. Чистим только отправляемое, исходник не трогаем.
                var part = TextNormalizer.Normalize(chunks[index].Text);
                normalized += part.Replacements;

                report.Report(new SynthesisProgress(
                    index, chunks.Count, chunks[index].Text, SynthesisStage.Synthesizing, normalized));

                await using var audio = await tts.SynthesizeAsync(
                    part.Text, project.Options.SpeakerId, project.Options.LanguageId, cancellationToken);

                // XTTS отдаёт кусок с тишиной по краям. Склеенные как есть, они дают на стыке
                // тишину конца плюс тишину начала — слышимые паузы там, где их нет в тексте.
                // Режем края и закрываем их коротким затуханием, чтобы стык не щёлкал.
                var prepared = ChunkPreparer.Prepare(audio, part.Text.Count(char.IsLetter));
                await using var preparedStream = prepared.Audio;

                if (prepared.LooksSuspicious)
                {
                    suspicious.Add(new SuspiciousPart(index + 1, prepared.SpeechCharsPerSecond));
                }

                // Формат выходного файла берётся из первой части, дальше — только дописывание.
                writer ??= WavConcatWriter.CreateEmpty(project.AudioPath, WavReader.Read(preparedStream));
                writer.Append(preparedStream);

                project.Progress.CompletedChunks = index + 1;
                project.Progress.DataBytes = writer.DataBytes;
                TrackChunkEnd(project, writer.DataBytes);
                store.Save(project);

                report.Report(new SynthesisProgress(
                    project.Progress.CompletedChunks, chunks.Count, chunks[index].Text,
                    SynthesisStage.Finishing, normalized));
            }

            project.Progress.Status = ProjectStatus.Completed;
            project.Progress.LastError = null;
            store.Save(project);
        }
        catch (OperationCanceledException)
        {
            // Отмена — это нормальный исход, состояние уже сохранено после последней целой части.
            project.Progress.Status = ProjectStatus.InProgress;
            store.Save(project);
            throw;
        }
        catch (Exception ex)
        {
            project.Progress.Status = ProjectStatus.Failed;
            project.Progress.LastError = ex.Message;
            store.Save(project);
            throw;
        }
        finally
        {
            writer?.Dispose();
        }

        return new SynthesisResult(
            project.Progress.CompletedChunks, project.Progress.TotalChunks, suspicious);
    }

    public void Reset(ProjectModel project)
    {
        ArgumentNullException.ThrowIfNull(project);

        // Файл мог остаться открытым плеером. Тогда удаление невозможно, но и не
        // нужно: следующая запись откроет его с FileMode.Create и обрежет сама.
        try
        {
            if (File.Exists(project.AudioPath))
            {
                File.Delete(project.AudioPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Оставляем файл — синтез его перезапишет.
        }

        project.Progress.CompletedChunks = 0;
        project.Progress.TotalChunks = 0;
        project.Progress.DataBytes = 0;
        project.Progress.ChunkEnds.Clear();
        project.Progress.Status = ProjectStatus.NotStarted;
        project.Progress.LastError = null;
        project.Progress.Signature = null;
        store.Save(project);
    }

    public void RewindTo(ProjectModel project, int partNumber)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (partNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber), partNumber, "Номер части начинается с 1.");
        }

        var progress = project.Progress;
        if (partNumber > progress.CompletedChunks)
        {
            throw new InvalidOperationException(
                $"Переозвучить можно только уже готовую часть: сделано {progress.CompletedChunks} из {progress.TotalChunks}.");
        }

        // Смещение конца предыдущей части — это и есть точка отсечения.
        var keep = partNumber - 1;

        if (keep > 0 && progress.ChunkEnds.Count < keep)
        {
            // Проекты, сделанные до появления ChunkEnds, не помнят границы частей.
            // Без них отсечь файл в нужном месте нельзя — честно предлагаем пересоздать.
            throw new InvalidOperationException(
                "Этот проект создан в прежней версии программы и не помнит границы частей, " +
                "поэтому переозвучить одну часть нельзя. Нажмите «Синтезировать заново».");
        }

        var offset = keep == 0 ? 0 : progress.ChunkEnds[keep - 1];

        progress.DataBytes = offset;
        progress.CompletedChunks = keep;
        progress.Status = keep == 0 ? ProjectStatus.NotStarted : ProjectStatus.InProgress;
        progress.LastError = null;
        progress.ChunkEnds.RemoveRange(keep, progress.ChunkEnds.Count - keep);

        store.Save(project);
    }

    /// <summary>Поправляет список смещений: он должен точно описывать готовые части.</summary>
    private static void TrackChunkEnd(ProjectModel project, long dataBytes)
    {
        var ends = project.Progress.ChunkEnds;
        var expected = project.Progress.CompletedChunks - 1;

        // Список мог остаться от прежнего прогона с другой разбивкой — тогда не учитываем его.
        if (ends.Count != expected)
        {
            ends.Clear();
        }

        // Последний элемент смотрим только когда он есть: на первой части список
        // пуст, и обращение к ends[^1] уронило бы весь синтез.
        var sameAsLast = ends.Count > 0 && ends[^1] == dataBytes;

        if (!sameAsLast)
        {
            ends.Add(dataBytes);
        }
    }

    private static void EnsureCompatible(ProjectModel project, string signature, int totalChunks)
    {
        if (!string.Equals(project.Progress.Signature, signature, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Текст или настройки синтеза изменились после начала работы, поэтому уже записанное аудио " +
                "нельзя продолжить. Нажмите «Начать заново».");
        }

        if (project.Progress.TotalChunks != totalChunks)
        {
            throw new InvalidOperationException(
                $"Разбивка изменилась: было {project.Progress.TotalChunks} частей, стало {totalChunks}. " +
                "Нажмите «Начать заново».");
        }
    }
}
