namespace SmartWiFi.Core.Models;

/// <summary>Everything the user can configure. Saved as JSON in %APPDATA%\SmartWiFi\settings.json.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Networks in priority order: index 0 is the most preferred.</summary>
    public List<NetworkEntry> Networks { get; set; } = new();

    public GeneralSettings General { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public TailscaleSettings Tailscale { get; set; } = new();

    public NetworkEntry? Find(string? ssid) =>
        ssid is null ? null : Networks.FirstOrDefault(n => string.Equals(n.Ssid, ssid, StringComparison.Ordinal));

    public int RankOf(string? ssid)
    {
        if (ssid is null) return int.MaxValue;
        var i = Networks.FindIndex(n => string.Equals(n.Ssid, ssid, StringComparison.Ordinal));
        return i < 0 ? int.MaxValue : i;
    }
}

public sealed class NetworkEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Ssid { get; set; } = "";

    /// <summary>Wi-Fi password, encrypted with DPAPI. Null means "use the profile Windows already has".</summary>
    public string? PasswordSecret { get; set; }

    /// <summary>A phone hotspot: counted by the data meter and subject to the daily cap and the Tailscale rule.</summary>
    public bool IsHotspot { get; set; }

    public PortalConfig Portal { get; set; } = new();

    public override string ToString() => Ssid;
}

public enum PortalPreset
{
    /// <summary>A plain HTML form: you give the URL and the fields.</summary>
    Custom,
    /// <summary>Fortinet FortiGate captive portal (the "fgtauth?..." pages common on campuses).</summary>
    FortiGate,
    /// <summary>Pronto Networks portal (phc.prontonetworks.com), used by many hostels.</summary>
    Pronto,
}

public sealed class PortalConfig
{
    public bool Enabled { get; set; }
    public PortalPreset Preset { get; set; } = PortalPreset.Custom;
    public string LoginUrl { get; set; } = "";
    public string Method { get; set; } = "POST";

    /// <summary>Form fields. Values may use {username}, {password}, {magic} and {redirect}.</summary>
    public List<FormField> Fields { get; set; } = new();

    public string Username { get; set; } = "";
    public string? PasswordSecret { get; set; }

    /// <summary>Many portals use self-signed certificates. Only affects requests to the portal.</summary>
    public bool IgnoreCertificateErrors { get; set; }
}

public sealed class FormField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    public FormField() { }
    public FormField(string name, string value) { Name = name; Value = value; }
}

public sealed class GeneralSettings
{
    public int CheckIntervalSeconds { get; set; } = 15;

    /// <summary>A more preferred network must be seen in this many scans in a row before switching up to it.</summary>
    public int SwitchUpAfterScans { get; set; } = 2;

    /// <summary>A joined network that has no internet for this long is skipped.</summary>
    public int OfflineSkipSeconds { get; set; } = 60;
    public int SkipMinutes { get; set; } = 10;

    /// <summary>A network picked by hand from the menu is kept for this long while it works.</summary>
    public int ManualHoldMinutes { get; set; } = 10;

    public bool KeepAwakeOnAC { get; set; } = true;

    public bool Watchdog { get; set; } = true;
    public int WatchdogMinutes { get; set; } = 5;

    /// <summary>0 = no cap.</summary>
    public long HotspotDailyCapMB { get; set; }

    public bool StartWithWindows { get; set; } = true;

    public string ProbeUrl { get; set; } = "http://www.msftconnecttest.com/connecttest.txt";
    public string ProbeExpected { get; set; } = "Microsoft Connect Test";
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = "";
    public string? TokenSecret { get; set; }

    public bool OnSwitch { get; set; } = true;
    public bool OnPortalFailure { get; set; } = true;
    public bool OnBackOnline { get; set; } = true;
    public bool OnDataCap { get; set; } = true;

    /// <summary>Also show Windows balloon notifications on this PC.</summary>
    public bool Desktop { get; set; } = true;
}

public sealed class TailscaleSettings
{
    /// <summary>Run "tailscale down" while on a hotspot and "tailscale up" again when back on a regular network.</summary>
    public bool DownOnHotspot { get; set; }

    /// <summary>Empty = look in Program Files and on PATH.</summary>
    public string CliPath { get; set; } = "";

    /// <summary>Arguments used to bring Tailscale back. Add your usual flags if you run "tailscale up" with any.</summary>
    public string UpArguments { get; set; } = "up";
}
