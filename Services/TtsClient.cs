using System.Text.Json;
using readSounds.Models;

namespace readSounds.Services;

/// <summary>Ошибка обращения к XTTS с понятным пользователю текстом.</summary>
public sealed class TtsException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Клиент XTTS-сервера.</summary>
public interface ITtsClient
{
    /// <summary>Проверяет, что сервер отвечает.</summary>
    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>Читает список голосов и языков с главной страницы сервера.</summary>
    Task<TtsCatalog> GetCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Синтезирует одну часть. Возвращает поток с WAV.</summary>
    Task<Stream> SynthesizeAsync(string text, string speakerId, string languageId, CancellationToken cancellationToken);
}

/// <summary>
/// Реализация поверх HTTP API <c>tts-server</c> (XTTS v2).
/// </summary>
/// <remarks>
/// Запрос идёт тем же GET-запросом с параметрами, что и демо-страница сервера.
/// Ответ — WAV, который склеивается на диске без перекодирования.
/// Внутри <c>tts-server</c> стоит глобальный <c>Lock</c>, поэтому параллельные
/// запросы всё равно выстроились бы в очередь; здесь они сериализуются явно,
/// чтобы не держать лишние соединения и давать честный отчёт об ожидании.
/// </remarks>
public sealed class TtsClient : ITtsClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly IAppSettingsProvider _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TtsClient(IAppSettingsProvider settings)
    {
        _settings = settings;

        // Системный прокси нам не нужен и часто вредит: XTTS живёт в локальной сети или на
        // localhost, а Windows-прокси (Hiddify, VPN-клиенты) отправляет такие запросы
        // наружу, где их обрывают. Поэтому шлюз выключен явно, а не полагаемся на NO_PROXY.
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
        };

        _http = new HttpClient(handler)
        {
            // Таймаут задаём на каждый запрос: он зависит от настроек и может меняться.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        using var response = await _http.GetAsync(new Uri(ServerUrl, UriKind.Absolute), timeout.Token);
        response.EnsureSuccessStatusCode();
    }

    public async Task<TtsCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        var uri = new Uri(ServerUrl, UriKind.Absolute);
        using var response = await _http.GetAsync(uri, timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);

        var html = await response.Content.ReadAsStringAsync(timeout.Token);
        return HtmlCatalogParser.Parse(html);
    }

    public async Task<Stream> SynthesizeAsync(
        string text, string speakerId, string languageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // Сервер на своей стороне превращает пустые language_id и speaker_id в None:
        //     if language_idx == "": language_idx = None
        // XTTS на language=None падает и отвечает 500 «TTS synthesis failed».
        // Поэтому ловим это у себя, до запроса, и говорим прямо, что не так.
        if (string.IsNullOrWhiteSpace(languageId))
        {
            throw new TtsException(
                "Не указан язык синтеза. Выберите язык в настройках проекта и повторите.");
        }

        if (string.IsNullOrWhiteSpace(speakerId))
        {
            throw new TtsException(
                "Не указан голос. Выберите голос в настройках проекта и повторите.");
        }

        var uri = BuildTtsUri(text, speakerId, languageId);

        using var timeout = CreateTimeout(cancellationToken);
        await _gate.WaitAsync(timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await EnsureSuccessAsync(response, timeout.Token);

            // Часть текста в пределах лимита — это десятки килобайт, держим в памяти.
            var buffer = new MemoryStream(capacity: 256 * 1024);
            await response.Content.CopyToAsync(buffer, timeout.Token);
            buffer.Position = 0;
            return buffer;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ServerUrl
    {
        get
        {
            var raw = _settings.Current.ServerUrl.Trim();
            if (!raw.EndsWith('/'))
            {
                raw += "/";
            }

            return raw;
        }
    }

    private Uri BuildTtsUri(string text, string speakerId, string languageId)
    {
        var builder = new UriBuilder(new Uri(ServerUrl, UriKind.Absolute))
        {
            Path = "api/tts",
        };

        var query = new List<string>(3)
        {
            "text=" + Uri.EscapeDataString(text),
            "speaker_id=" + Uri.EscapeDataString(speakerId ?? string.Empty),
            "language_id=" + Uri.EscapeDataString(languageId ?? string.Empty),
        };

        // UriBuilder кодирует Query сам, поэтому собираем его вручную без лишнего %25.
        return new Uri(builder.Uri.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', query));
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var seconds = Math.Clamp(_settings.Current.RequestTimeoutSeconds, 10, 7200);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = string.Empty;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            detail = ExtractErrorDetail(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Тело недоступно — покажем только код ответа.
        }

        var message = $"Сервер вернул {(int)response.StatusCode} {response.ReasonPhrase}.";
        if (detail.Length > 0)
        {
            message += " " + detail;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            message += " XTTS обычно отдаёт 503, пока модель прогревается на GPU.";
        }

        throw new TtsException(message);
    }

    /// <summary>
    /// Достаёт текст ошибки из тела ответа.
    /// </summary>
    /// <remarks>
    /// Актуальный <c>coqui-tts</c> отдаёт JSON: <c>{"error": "TTS synthesis failed: ..."}</c>,
    /// причём сам текст внутри ключа полезнее, чем обёртка. Старые сборки отдавали HTML —
    /// на этот случай остаётся запасной разбор.
    /// </remarks>
    private static string ExtractErrorDetail(string body)
    {
        if (body.Contains("\"error\"", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    var text = error.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }
            }
            catch (JsonException)
            {
                // Не JSON — падаем на разбор HTML ниже.
            }
        }

        return HtmlCatalogParser.ExtractText(body);
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
