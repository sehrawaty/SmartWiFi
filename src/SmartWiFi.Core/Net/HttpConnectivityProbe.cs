using System.Net;
using System.Text.RegularExpressions;

namespace SmartWiFi.Core.Net;

/// <summary>
/// The same check Windows itself uses (NCSI): fetch a tiny plain-HTTP page and compare its body.
/// A redirect or a different body means a captive portal is in the way; no answer means no internet.
/// </summary>
public sealed partial class HttpConnectivityProbe : IConnectivityProbe, IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<(string Url, string Expected)> _target;

    public HttpConnectivityProbe(Func<(string Url, string Expected)> target, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _target = target;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
    }

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        var (url, expected) = _target();
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            return await ClassifyAsync(resp, url, expected, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(Connectivity.NoInternet); // timeout
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(Connectivity.NoInternet);
        }
    }

    internal static async Task<ProbeResult> ClassifyAsync(HttpResponseMessage resp, string url, string expected, CancellationToken ct)
    {
        var code = (int)resp.StatusCode;

        if (code is >= 300 and < 400)
        {
            var loc = resp.Headers.Location;
            string? portal = loc is null ? null : (loc.IsAbsoluteUri ? loc : new Uri(new Uri(url), loc)).ToString();
            return new ProbeResult(Connectivity.CaptivePortal, portal);
        }

        if (resp.StatusCode == HttpStatusCode.NetworkAuthenticationRequired)
            return new ProbeResult(Connectivity.CaptivePortal, null);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.IsSuccessStatusCode && body.Trim() == expected.Trim())
            return new ProbeResult(Connectivity.Online);

        // Some portals answer the probe themselves with a page that redirects in HTML or JavaScript.
        if (resp.IsSuccessStatusCode || code == 403)
            return new ProbeResult(Connectivity.CaptivePortal, ExtractUrl(body, url));

        return new ProbeResult(Connectivity.NoInternet);
    }

    internal static string? ExtractUrl(string html, string baseUrl)
    {
        foreach (var re in new[] { MetaRefresh(), JsLocation() })
        {
            var m = re.Match(html);
            if (m.Success && Uri.TryCreate(new Uri(baseUrl), WebUtility.HtmlDecode(m.Groups["u"].Value), out var u))
                return u.ToString();
        }
        return null;
    }

    [GeneratedRegex("""<meta[^>]+http-equiv\s*=\s*["']?refresh["']?[^>]*content\s*=\s*["'][^"']*?url\s*=\s*(?<u>[^"'>\s]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefresh();

    [GeneratedRegex("""(?:window\.|document\.)?location(?:\.href)?\s*=\s*["'](?<u>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex JsLocation();

    public void Dispose() => _http.Dispose();
}
