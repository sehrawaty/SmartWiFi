namespace SmartWiFi.Core;

public enum WifiSecurity { Open, Wpa2Personal, Wpa3Personal, Enterprise, Other }

/// <summary>A network seen in the last scan. One entry per SSID (the strongest).</summary>
public sealed record VisibleNetwork(string Ssid, int SignalQuality, string? ProfileName, WifiSecurity Security);

/// <summary>The network the Wi-Fi adapter is joined to right now.</summary>
public sealed record CurrentLink(string Ssid, string? ProfileName, int SignalQuality, int? Rssi);

public interface IWifiAdapter
{
    /// <summary>False when the machine has no usable Wi-Fi interface.</summary>
    bool IsAvailable { get; }

    Task ScanAsync(CancellationToken ct);
    IReadOnlyList<VisibleNetwork> GetVisibleNetworks();
    CurrentLink? GetCurrent();

    /// <summary>Make sure Windows has a profile for this SSID, creating one from the password if needed.</summary>
    ProfileResult EnsureProfile(VisibleNetwork network, string? password);

    Task<bool> ConnectAsync(string profileName, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);

    /// <summary>Bytes sent + received on the Wi-Fi interface since it came up, or null if unknown.</summary>
    long? GetTotalBytes();
}

public sealed record ProfileResult(bool Ok, string ProfileName, string? Error)
{
    public static ProfileResult Success(string name) => new(true, name, null);
    public static ProfileResult Fail(string error) => new(false, "", error);
}

public enum Connectivity { Online, CaptivePortal, NoInternet, Disconnected }

public sealed record ProbeResult(Connectivity State, string? PortalUrl = null);

public interface IConnectivityProbe
{
    Task<ProbeResult> ProbeAsync(CancellationToken ct);
}

public interface IPortalLogin
{
    /// <summary>Submits the login. Whether it worked is decided by probing afterwards.</summary>
    Task<PortalAttempt> LoginAsync(Models.PortalConfig config, string? password, string? portalUrl, CancellationToken ct);
}

public sealed record PortalAttempt(bool RequestSent, string Detail);

public interface INotifier
{
    void Enqueue(string title, string message, string? tags = null, int priority = 3);
    Task FlushAsync(CancellationToken ct);
}

public interface ITailscaleControl
{
    bool IsInstalled { get; }
    Task<bool?> IsUpAsync(CancellationToken ct);
    Task<bool> SetUpAsync(bool up, CancellationToken ct);
}

public interface ISecretStore
{
    string Protect(string plain);
    string? Unprotect(string? secret);
}

public interface IClock
{
    DateTime Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
}

/// <summary>Sends every notification to several notifiers (e.g. the phone and the Windows desktop).</summary>
public sealed class CompositeNotifier : INotifier
{
    private readonly INotifier[] _all;
    public CompositeNotifier(params INotifier[] all) => _all = all;

    public void Enqueue(string title, string message, string? tags = null, int priority = 3)
    {
        foreach (var n in _all) n.Enqueue(title, message, tags, priority);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        foreach (var n in _all) await n.FlushAsync(ct).ConfigureAwait(false);
    }
}
