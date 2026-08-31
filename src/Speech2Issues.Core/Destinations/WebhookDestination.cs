using System.Net;
using System.Net.Http.Json;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Destinations;

public sealed class WebhookDestination(HttpClient httpClient, WebhookSettings settings, IReadOnlyDictionary<string, string> secretHeaders) : ITaskDestination
{
    public DestinationKind Kind => DestinationKind.Webhook;

    public async Task<ConnectionCheck> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return new(false, "Укажите корректный HTTP(S) URL webhook.");
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, uri);
            AddHeaders(request);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound
                ? new(true, $"Webhook endpoint отвечает HTTP {(int)response.StatusCode}.")
                : new(false, $"Webhook endpoint вернул HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    public Task<IReadOnlyList<DestinationTarget>> LoadTargetsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DestinationTarget>>([new(settings.Url, settings.Url)]);

    public async Task<CreatedTaskResult> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Webhook URL не настроен.");
        }

        var payload = new { schemaVersion = 1, source = "Speech2Issues", task = draft };
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(payload) };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", draft.Id);
            AddHeaders(request);
            try
            {
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var location = response.Headers.Location?.ToString() ?? uri.ToString();
                    return new("Webhook", draft.Id, location);
                }
                if (!ShouldRetry(response.StatusCode) || attempt == 3)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"Webhook HTTP {(int)response.StatusCode}: {body}");
                }
            }
            catch (HttpRequestException) when (attempt < 3)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
        }
        throw new HttpRequestException("Webhook request failed after retries.");
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        var merged = new Dictionary<string, string>(settings.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in secretHeaders)
        {
            merged[pair.Key] = pair.Value;
        }
        foreach (var pair in merged)
        {
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
}
