using System.Net.NetworkInformation;
using ManagedNativeWifi;
using SmartWiFi.Core;
using SmartWiFi.Core.Wifi;

namespace SmartWiFi.Platform;

/// <summary>The Windows Native Wi-Fi API (wlanapi.dll), through ManagedNativeWifi.</summary>
public sealed class WindowsWifiAdapter : IWifiAdapter
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private Guid? Interface()
    {
        try
        {
            var all = NativeWifi.EnumerateInterfaces().ToList();
            var pick = all.FirstOrDefault(i => i.State != InterfaceState.NotReady) ?? all.FirstOrDefault();
            return pick?.Id;
        }
        catch (Exception)
        {
            return null; // WLAN service stopped, no adapter, etc.
        }
    }

    public bool IsAvailable => Interface() is not null;

    public async Task ScanAsync(CancellationToken ct)
    {
        if (Interface() is not { } id) return;
        await NativeWifi.ScanNetworksAsync(ScanMode.OnlySpecified, new[] { id }, ScanTimeout, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<VisibleNetwork> GetVisibleNetworks()
    {
        if (Interface() is not { } id) return Array.Empty<VisibleNetwork>();
        var (result, list) = NativeWifi.EnumerateAvailableNetworks(id);
        if (result != ActionResult.Success || list is null) return Array.Empty<VisibleNetwork>();

        return list
            .Where(n => n.BssType == BssType.Infrastructure)
            .Select(n => new VisibleNetwork(
                n.Ssid.ToString(),
                n.SignalQuality,
                string.IsNullOrEmpty(n.ProfileName) ? null : n.ProfileName,
                Map(n.AuthenticationAlgorithm)))
            .Where(n => n.Ssid.Length > 0) // hidden networks
            .GroupBy(n => n.Ssid, StringComparer.Ordinal)
            .Select(g => g
                .OrderByDescending(n => n.ProfileName is not null)
                .ThenBy(n => n.Security == WifiSecurity.Wpa2Personal ? 0 : 1) // WPA2/WPA3 transition: WPA2 is the safer bet
                .ThenByDescending(n => n.SignalQuality)
                .First() with { SignalQuality = g.Max(n => n.SignalQuality) })
            .OrderByDescending(n => n.SignalQuality)
            .ToList();
    }

    public CurrentLink? GetCurrent()
    {
        if (Interface() is not { } id) return null;
        var (result, info) = NativeWifi.GetCurrentConnection(id);
        if (result != ActionResult.Success || info is null || info.InterfaceState != InterfaceState.Connected) return null;

        int? rssi = null;
        var (rr, value) = NativeWifi.GetRssi(id);
        if (rr == ActionResult.Success && value < 0) rssi = value;

        return new CurrentLink(info.Ssid.ToString(), string.IsNullOrEmpty(info.ProfileName) ? null : info.ProfileName, info.SignalQuality, rssi);
    }

    public ProfileResult EnsureProfile(VisibleNetwork network, string? password)
    {
        // Already saved in Windows and we have nothing newer: use it as is.
        if (network.ProfileName is not null && string.IsNullOrEmpty(password))
            return ProfileResult.Success(network.ProfileName);

        if (network.Security == WifiSecurity.Enterprise || network.Security == WifiSecurity.Other)
            return network.ProfileName is not null
                ? ProfileResult.Success(network.ProfileName)
                : ProfileResult.Fail("this network uses a login type SmartWiFi can't set up; connect to it once from Windows Settings");

        if (network.Security != WifiSecurity.Open && string.IsNullOrEmpty(password))
            return ProfileResult.Fail("no password saved; add it in SmartWiFi settings or connect once from Windows");

        if (Interface() is not { } id) return ProfileResult.Fail("no Wi-Fi adapter");

        var xml = WlanProfileXml.Build(network.Ssid, network.Security, password);
        // All-user profiles also work at the sign-in screen; fall back to a per-user one if policy forbids it.
        if (NativeWifi.SetProfile(id, ProfileType.AllUser, xml, null, overwrite: true) ||
            NativeWifi.SetProfile(id, ProfileType.PerUser, xml, null, overwrite: true))
            return ProfileResult.Success(network.Ssid);

        return network.ProfileName is not null
            ? ProfileResult.Success(network.ProfileName)
            : ProfileResult.Fail("Windows refused to save the network profile");
    }

    public async Task<bool> ConnectAsync(string profileName, CancellationToken ct)
    {
        if (Interface() is not { } id) return false;
        return await NativeWifi.ConnectNetworkAsync(id, profileName, BssType.Infrastructure, ConnectTimeout, ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (Interface() is not { } id) return;
        await NativeWifi.DisconnectNetworkAsync(id, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    public long? GetTotalBytes()
    {
        if (Interface() is not { } id) return null;
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => Guid.TryParse(n.Id, out var g) && g == id);
            if (nic is null) return null;
            var stats = nic.GetIPStatistics();
            return stats.BytesReceived + stats.BytesSent;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>Networks Windows already remembers (for the "add network" picker).</summary>
    public IReadOnlyList<string> SavedProfileNames()
    {
        try { return NativeWifi.EnumerateProfileNames().Distinct().OrderBy(n => n).ToList(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static WifiSecurity Map(AuthenticationAlgorithm a) => a switch
    {
        AuthenticationAlgorithm.Open => WifiSecurity.Open,
        AuthenticationAlgorithm.RSNA_PSK or AuthenticationAlgorithm.WPA_PSK => WifiSecurity.Wpa2Personal,
        AuthenticationAlgorithm.WPA3_SAE => WifiSecurity.Wpa3Personal,
        AuthenticationAlgorithm.RSNA or AuthenticationAlgorithm.WPA or AuthenticationAlgorithm.WPA3_ENT
            or AuthenticationAlgorithm.WPA3_ENT_192 => WifiSecurity.Enterprise,
        _ => WifiSecurity.Other,
    };
}
