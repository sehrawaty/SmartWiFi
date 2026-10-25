using System.Net;
using SmartWiFi.Core.Models;

namespace SmartWiFi.Core.Portal;

/// <summary>
/// Submits a captive-portal login form. Every preset is just data (URL, method, fields with placeholders),
/// so a portal that isn't covered can be set up as "Custom" from the browser's dev tools.
/// </summary>
/// <remarks>
/// Placeholders, usable in the URL and in field values:
/// {username} {password} — your portal account;
/// {portalUrl} — the page the portal redirected to; {portalBase} — its scheme://host:port;
/// {magic} — the token FortiGate puts in its redirect (fgtauth?TOKEN or ?magic=TOKEN);
/// {redirect} — where to send the browser afterwards.
/// </remarks>
public sealed class PortalLogin : IPortalLogin
{
    public const string RedirectTarget = "http://www.msftconnecttest.com/redirect";
    private readonly Func<bool, HttpMessageHandler>? _handlerFactory;

    public PortalLogin(Func<bool, HttpMessageHandler>? handlerFactory = null) => _handlerFactory = handlerFactory;

    public static PortalConfig Defaults(PortalPreset preset) => preset switch
    {
        PortalPreset.FortiGate => new PortalConfig
        {
            Enabled = true,
            Preset = preset,
            LoginUrl = "{portalBase}/",
            Method = "POST",
            Fields =
            {
                new("4Tredir", "{redirect}"),
                new("magic", "{magic}"),
                new("username", "{username}"),
                new("password", "{password}"),
            },
        },
        PortalPreset.Pronto => new PortalConfig
        {
            Enabled = true,
            Preset = preset,
            LoginUrl = "http://phc.prontonetworks.com/cgi-bin/authlogin",
            Method = "POST",
            Fields =
            {
                new("userId", "{username}"),
                new("password", "{password}"),
                new("serviceName", "ProntoAuthentication"),
                new("Submit22", "Login"),
            },
        },
        _ => new PortalConfig
        {
            Enabled = true,
            Preset = PortalPreset.Custom,
            Method = "POST",
            Fields = { new("username", "{username}"), new("password", "{password}") },
        },
    };

    public async Task<PortalAttempt> LoginAsync(PortalConfig config, string? password, string? portalUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.LoginUrl))
            return new PortalAttempt(false, "no login URL set");

        var vars = Variables(config, password, portalUrl);
        if (NeedsPortalUrl(config) && portalUrl is null)
            return new PortalAttempt(false, "waiting for the portal's redirect address");

        var url = Expand(config.LoginUrl, vars, encode: false);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new PortalAttempt(false, $"login URL is not valid: {url}");

        var fields = config.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => new KeyValuePair<string, string>(f.Name, Expand(f.Value, vars, encode: false)))
            .ToList();

        using var handler = _handlerFactory?.Invoke(config.IgnoreCertificateErrors) ?? DefaultHandler(config.IgnoreCertificateErrors);
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SmartWiFi");

        try
        {
            // Open the portal page first: several portals only accept a login from a session they started.
            if (portalUrl is not null && Uri.TryCreate(portalUrl, UriKind.Absolute, out var portalUri))
            {
                using var pre = await http.GetAsync(portalUri, ct).ConfigureAwait(false);
            }

            HttpResponseMessage resp;
            if (config.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var b = new UriBuilder(uri);
                var q = string.Join("&", fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
                b.Query = string.IsNullOrEmpty(b.Query) ? q : b.Query.TrimStart('?') + "&" + q;
                resp = await http.GetAsync(b.Uri, ct).ConfigureAwait(false);
            }
            else
            {
                using var body = new FormUrlEncodedContent(fields);
                using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = body };
                if (portalUrl is not null) req.Headers.Referrer = new Uri(portalUrl);
                resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            }

            using (resp)
                return new PortalAttempt(true, $"HTTP {(int)resp.StatusCode} from {uri.Host}");
        }
        catch (HttpRequestException e)
        {
            return new PortalAttempt(false, $"couldn't reach {uri.Host}: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PortalAttempt(false, $"{uri.Host} didn't answer in time");
        }
    }

    internal static bool NeedsPortalUrl(PortalConfig c) =>
        c.LoginUrl.Contains("{portal", StringComparison.OrdinalIgnoreCase) ||
        c.Fields.Any(f => f.Value.Contains("{magic}", StringComparison.OrdinalIgnoreCase) ||
                          f.Value.Contains("{portal", StringComparison.OrdinalIgnoreCase));

    internal static Dictionary<string, string> Variables(PortalConfig c, string? password, string? portalUrl)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["username"] = c.Username,
            ["password"] = password ?? "",
            ["redirect"] = RedirectTarget,
            ["portalUrl"] = portalUrl ?? "",
            ["portalBase"] = "",
            ["magic"] = "",
        };
        if (portalUrl is not null && Uri.TryCreate(portalUrl, UriKind.Absolute, out var u))
        {
            v["portalBase"] = u.GetLeftPart(UriPartial.Authority);
            v["magic"] = MagicFrom(u);
        }
        return v;
    }

    internal static string MagicFrom(Uri u)
    {
        var q = u.Query.TrimStart('?');
        if (q.Length == 0) return "";
        foreach (var part in q.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("magic", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(kv[1]);
        }
        // FortiGate style: /fgtauth?0123456789abcdef
        return q.Contains('=') ? "" : Uri.UnescapeDataString(q);
    }

    internal static string Expand(string template, IReadOnlyDictionary<string, string> vars, bool encode)
    {
        var s = template;
        foreach (var (k, v) in vars)
            s = s.Replace("{" + k + "}", encode ? Uri.EscapeDataString(v) : v, StringComparison.OrdinalIgnoreCase);
        return s;
    }

    private static HttpMessageHandler DefaultHandler(bool ignoreCert)
    {
        var h = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            UseProxy = false,
        };
        if (ignoreCert)
            h.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return h;
    }
}

/// <summary>15 s, 30 s, 60 s, then every 2 minutes.</summary>
public static class PortalBackoff
{
    private static readonly TimeSpan[] Steps =
    {
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2),
    };

    public static TimeSpan After(int failedAttempts) =>
        failedAttempts <= 0 ? TimeSpan.Zero : Steps[Math.Min(failedAttempts, Steps.Length) - 1];
}
