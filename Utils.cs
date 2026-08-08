using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NinjaPricer;

/// <summary>
/// Small, shared HTTP client for poe.ninja requests.
///
/// The PoE1 implementation uses one long-lived client and conditional requests.  The same
/// transport behavior is useful for PoE2, but the endpoint and item model remain PoE2-specific.
/// </summary>
public static class Utils
{
    public readonly record struct DownloadResult(string Body, bool NotModified);

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip |
            DecompressionMethods.Deflate |
            DecompressionMethods.Brotli,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    // ETags are intentionally process-local.  A persisted ETag without its matching body could
    // produce a 304 response that cannot be used after a plugin restart.
    private static readonly ConcurrentDictionary<string, string> Etags = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<bool> LastRequestNotModified = new();
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly TimeSpan MinimumRequestSpacing = TimeSpan.FromMilliseconds(200);
    private static DateTime _nextRequestAtUtc = DateTime.MinValue;

    static Utils()
    {
        // poe.ninja asks clients to identify themselves with a descriptive User-Agent.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "NinjaPricer-PoE2/1.0 (+https://github.com/exCore2/NinjaPricer)");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    [Obsolete("Use DownloadFromUrlWithStatus and inspect DownloadResult.NotModified.")]
    public static bool LastRequestWasNotModified => LastRequestNotModified.Value;

    public static async Task<string> DownloadFromUrl(string url)
        => (await DownloadFromUrlWithStatus(url, null, CancellationToken.None).ConfigureAwait(false)).Body;

    public static async Task<string> DownloadFromUrl(string url, CancellationToken cancellationToken)
        => (await DownloadFromUrlWithStatus(url, null, cancellationToken).ConfigureAwait(false)).Body;

    public static async Task<string> DownloadFromUrl(string url, string cachedBody)
        => (await DownloadFromUrlWithStatus(url, cachedBody, CancellationToken.None).ConfigureAwait(false)).Body;

    public static async Task<DownloadResult> DownloadFromUrlWithStatus(string url, string cachedBody)
        => await DownloadFromUrlWithStatus(url, cachedBody, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Downloads JSON and revalidates an existing body with If-None-Match when possible.
    /// </summary>
    public static async Task<DownloadResult> DownloadFromUrlWithStatus(
        string url,
        string cachedBody,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        cancellationToken.ThrowIfCancellationRequested();
        LastRequestNotModified.Value = false;

        string etag = null;
        var canRevalidate = !string.IsNullOrWhiteSpace(cachedBody) &&
            Etags.TryGetValue(url, out etag) &&
            !string.IsNullOrWhiteSpace(etag);

        if (!canRevalidate)
        {
            return await DownloadAndRememberETagWithStatus(url, cancellationToken).ConfigureAwait(false);
        }

        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                return request;
            },
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            LastRequestNotModified.Value = true;
            return new DownloadResult(cachedBody, true);
        }

        response.EnsureSuccessStatusCode();
        RememberETag(url, response);
        return new DownloadResult(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
            false);
    }

    /// <summary>Remember an ETag from a response, including poe.ninja's unquoted header form.</summary>
    public static void RememberETag(string url, HttpResponseMessage response)
    {
        if (string.IsNullOrWhiteSpace(url) || response == null)
        {
            return;
        }

        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag) &&
            response.Headers.TryGetValues("ETag", out var rawValues))
        {
            foreach (var rawValue in rawValues)
            {
                if (!string.IsNullOrWhiteSpace(rawValue))
                {
                    etag = rawValue.Trim();
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(etag))
        {
            Etags[url] = etag;
        }
    }

    public static async Task<byte[]> DownloadBytesFromUrl(string url)
        => await DownloadBytesFromUrl(url, CancellationToken.None).ConfigureAwait(false);

    public static async Task<byte[]> DownloadBytesFromUrl(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DownloadResult> DownloadAndRememberETagWithStatus(
        string url,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequestNotModified.Value = false;
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        RememberETag(url, response);
        return new DownloadResult(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
            false);
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = requestFactory();
            var response = await SendRateLimitedAsync(request, cancellationToken).ConfigureAwait(false);
            if (!IsTransient(response.StatusCode) || attempt == maxAttempts)
                return response;

            var retryDelay = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } retryAt
                    ? retryAt - DateTimeOffset.UtcNow
                    : TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1)));
            response.Dispose();
            if (retryDelay < TimeSpan.FromMilliseconds(100))
                retryDelay = TimeSpan.FromMilliseconds(100);
            if (retryDelay > TimeSpan.FromSeconds(10))
                retryDelay = TimeSpan.FromSeconds(10);
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("The poe.ninja request retry loop ended unexpectedly.");
    }

    private static async Task<HttpResponseMessage> SendRateLimitedAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var delay = _nextRequestAtUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            _nextRequestAtUtc = DateTime.UtcNow + MinimumRequestSpacing;
            return await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;
}
