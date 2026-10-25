namespace SmartWiFi.Core.Engine;

/// <summary>A snapshot of what the engine knows, for the tray icon and menu.</summary>
public sealed record EngineStatus
{
    public bool WifiAvailable { get; init; } = true;
    public string? Ssid { get; init; }
    public int? SignalQuality { get; init; }
    public int? Rssi { get; init; }
    public Connectivity State { get; init; } = Connectivity.Disconnected;
    public DateTime? ConnectedSince { get; init; }
    public bool IsHotspot { get; init; }
    public bool Checking { get; init; }

    public long DataTodayBytes { get; init; }
    public long? DataLimitBytes { get; init; }
    public bool DataCapReached { get; init; }

    public DateTime? PausedUntil { get; init; }
    public string? HoldSsid { get; init; }
    public DateTime? HoldUntil { get; init; }

    public bool TailscaleInstalled { get; init; }
    public bool? TailscaleUp { get; init; }

    public IReadOnlyList<VisibleNetwork> Visible { get; init; } = Array.Empty<VisibleNetwork>();

    public string StateText => Checking ? "checking…" : State switch
    {
        Connectivity.Online => "online",
        Connectivity.CaptivePortal => "portal login needed",
        Connectivity.NoInternet => "no internet",
        _ => WifiAvailable ? "not connected" : "no Wi-Fi adapter",
    };

    public static EngineStatus Starting { get; } = new() { Checking = true };
}
