using System.Net;
using SmartWiFi.Core;
using SmartWiFi.Core.Data;
using SmartWiFi.Core.Engine;
using SmartWiFi.Core.Logging;
using SmartWiFi.Core.Models;

namespace SmartWiFi.Core.Tests;

internal sealed class FakeClock : IClock
{
    public DateTime Now { get; set; } = new(2026, 10, 26, 10, 0, 0);
    public void Advance(TimeSpan t) => Now += t;
}

internal sealed class PlainSecrets : ISecretStore
{
    public string Protect(string plain) => plain;
    public string? Unprotect(string? secret) => secret;
}

internal sealed class FakeWifi : IWifiAdapter
{
    public bool IsAvailable { get; set; } = true;
    public HashSet<string> InRange { get; } = new();
    public string? Connected { get; set; }
    public long Bytes { get; set; }
    public List<string> Joins { get; } = new();
    public int Disconnects { get; private set; }
    public int Scans { get; private set; }
    public HashSet<string> FailJoin { get; } = new();

    public Task ScanAsync(CancellationToken ct) { Scans++; return Task.CompletedTask; }

    public IReadOnlyList<VisibleNetwork> GetVisibleNetworks() =>
        InRange.Select(s => new VisibleNetwork(s, 80, s, WifiSecurity.Wpa2Personal)).ToList();

    public CurrentLink? GetCurrent() => Connected is null ? null : new CurrentLink(Connected, Connected, 80, -50);

    public ProfileResult EnsureProfile(VisibleNetwork network, string? password) => ProfileResult.Success(network.Ssid);

    public Task<bool> ConnectAsync(string profileName, CancellationToken ct)
    {
        Joins.Add(profileName);
        if (!InRange.Contains(profileName) || FailJoin.Contains(profileName)) return Task.FromResult(false);
        Connected = profileName;
        return Task.FromResult(true);
    }

    public Task DisconnectAsync(CancellationToken ct) { Disconnects++; Connected = null; return Task.CompletedTask; }

    public long? GetTotalBytes() => Bytes;

    /// <summary>The phone walked away.</summary>
    public void Lose(string ssid)
    {
        InRange.Remove(ssid);
        if (Connected == ssid) Connected = null;
    }
}

internal sealed class FakeProbe : IConnectivityProbe
{
    private readonly FakeWifi _wifi;
    public Dictionary<string, Connectivity> BySsid { get; } = new();
    public string? PortalUrl { get; set; } = "http://10.0.0.1:1000/fgtauth?abc123";
    public int Calls { get; private set; }

    public FakeProbe(FakeWifi wifi) => _wifi = wifi;

    public Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        Calls++;
        if (_wifi.Connected is null) return Task.FromResult(new ProbeResult(Connectivity.Disconnected));
        var state = BySsid.TryGetValue(_wifi.Connected, out var s) ? s : Connectivity.Online;
        return Task.FromResult(new ProbeResult(state, state == Connectivity.CaptivePortal ? PortalUrl : null));
    }
}

internal sealed class FakePortal : IPortalLogin
{
    private readonly FakeProbe _probe;
    private readonly FakeWifi _wifi;
    public bool Works { get; set; } = true;
    public List<string?> Attempts { get; } = new();

    public FakePortal(FakeProbe probe, FakeWifi wifi) { _probe = probe; _wifi = wifi; }

    public Task<PortalAttempt> LoginAsync(PortalConfig config, string? password, string? portalUrl, CancellationToken ct)
    {
        Attempts.Add(portalUrl);
        if (Works && _wifi.Connected is { } ssid) _probe.BySsid[ssid] = Connectivity.Online;
        return Task.FromResult(new PortalAttempt(true, Works ? "HTTP 200" : "HTTP 401"));
    }
}

internal sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Message)> Sent { get; } = new();
    public void Enqueue(string title, string message, string? tags = null, int priority = 3) => Sent.Add((title, message));
    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeTailscale : ITailscaleControl
{
    public bool IsInstalled => true;
    public bool Up { get; set; } = true;
    public List<bool> Calls { get; } = new();
    public Task<bool?> IsUpAsync(CancellationToken ct) => Task.FromResult<bool?>(Up);
    public Task<bool> SetUpAsync(bool up, CancellationToken ct) { Calls.Add(up); Up = up; return Task.FromResult(true); }
}

internal sealed class Rig
{
    public const string Hotspot = "Pixel Hotspot";
    public const string Hostel = "Hostel-WiFi";
    public const string Library = "Library";

    public FakeClock Clock { get; } = new();
    public FakeWifi Wifi { get; } = new();
    public FakeProbe Probe { get; }
    public FakePortal Portal { get; }
    public FakeNotifier Notifier { get; } = new();
    public FakeTailscale Tailscale { get; } = new();
    public DataMeter Meter { get; } = new(new DataUsageState());
    public Log Log { get; }
    public AppSettings Settings { get; } = new();
    public SwitchEngine Engine { get; }

    public Rig()
    {
        Probe = new FakeProbe(Wifi);
        Portal = new FakePortal(Probe, Wifi);
        Log = new Log(null, Clock);
        Settings.Networks.Add(new NetworkEntry { Ssid = Hotspot, IsHotspot = true, PasswordSecret = "pw" });
        Settings.Networks.Add(new NetworkEntry
        {
            Ssid = Hostel,
            Portal = new PortalConfig { Enabled = true, LoginUrl = "http://portal/login", Username = "21BCE0001", PasswordSecret = "x" },
        });
        Settings.Networks.Add(new NetworkEntry { Ssid = Library });
        Settings.Notifications.Enabled = true;
        Engine = new SwitchEngine(() => Settings, Wifi, Probe, Portal, Notifier, Tailscale, Meter, new PlainSecrets(), Log, Clock,
            delay: (_, _) => Task.CompletedTask);
    }

    /// <summary>Runs one check, then moves the clock on by the delay the engine asked for.</summary>
    public async Task<TimeSpan> Tick()
    {
        var next = await Engine.TickAsync(CancellationToken.None);
        Clock.Advance(next);
        return next;
    }

    public async Task TickFor(TimeSpan total)
    {
        var end = Clock.Now + total;
        while (Clock.Now < end) await Tick();
    }

    public string Logs => string.Join("\n", Log.Recent.Select(e => e.Message));
}

/// <summary>An HttpMessageHandler that answers from a function and records requests.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return _respond(request);
    }

    public static HttpResponseMessage Text(string s, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(s) };
}
