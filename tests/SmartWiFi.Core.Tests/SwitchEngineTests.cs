using SmartWiFi.Core;
using static SmartWiFi.Core.Tests.Rig;

namespace SmartWiFi.Core.Tests;

public class SwitchEngineTests
{
    [Fact]
    public async Task Hotspot_leaving_falls_back_to_hostel_right_away()
    {
        var r = new Rig();
        r.Wifi.InRange.UnionWith(new[] { Hotspot, Hostel });
        r.Wifi.Connected = Hotspot;
        await r.Tick();

        r.Wifi.Lose(Hotspot);
        await r.Tick();

        Assert.Equal(Hostel, r.Wifi.Connected);
        Assert.Contains(r.Notifier.Sent, n => n.Title == $"Switched to {Hostel}");
    }

    [Fact]
    public async Task Switches_back_up_to_hotspot_only_after_two_scans_in_a_row()
    {
        var r = new Rig();
        r.Wifi.InRange.Add(Hostel);
        r.Wifi.Connected = Hostel;
        await r.Tick();

        r.Wifi.InRange.Add(Hotspot);
        await r.Tick();
        Assert.Equal(Hostel, r.Wifi.Connected); // seen once

        await r.Tick();
        Assert.Equal(Hotspot, r.Wifi.Connected); // seen twice
    }

    [Fact]
    public async Task A_flicker_in_one_scan_does_not_trigger_a_switch()
    {
        var r = new Rig();
        r.Wifi.InRange.Add(Hostel);
        r.Wifi.Connected = Hostel;
        await r.Tick();

        r.Wifi.InRange.Add(Hotspot);
        await r.Tick();
        r.Wifi.InRange.Remove(Hotspot);
        await r.Tick();
        r.Wifi.InRange.Add(Hotspot);
        await r.Tick();

        Assert.Equal(Hostel, r.Wifi.Connected);
    }

    [Fact]
    public async Task Does_not_scan_while_online_on_the_top_network()
    {
        var r = new Rig();
        r.Wifi.InRange.UnionWith(new[] { Hotspot, Hostel });
        r.Wifi.Connected = Hotspot;
        await r.Tick(); // first check scans (state unknown yet)
        var scans = r.Wifi.Scans;

        await r.Tick();
        await r.Tick();

        Assert.Equal(scans, r.Wifi.Scans);
    }

    [Fact]
    public async Task Logs_in_to_the_captive_portal_and_stays()
    {
        var r = new Rig();
        r.Wifi.InRange.Add(Hostel);
        r.Wifi.Connected = Hostel;
        r.Probe.BySsid[Hostel] = Connectivity.CaptivePortal;

        await r.Tick();

        Assert.Single(r.Portal.Attempts);
        Assert.Equal(r.Probe.PortalUrl, r.Portal.Attempts[0]);
        Assert.Equal(Connectivity.Online, r.Engine.Status.State);
        Assert.Contains("portal: logged in, online", r.Logs);
    }

    [Fact]
    public async Task Portal_retries_back_off_15_30_60_then_120_seconds()
    {
        var r = new Rig();
        r.Settings.General.OfflineSkipSeconds = 3600; // keep it from moving away during the test
        r.Settings.General.Watchdog = false;
        r.Settings.General.CheckIntervalSeconds = 5;
        r.Wifi.InRange.Add(Hostel);
        r.Wifi.Connected = Hostel;
        r.Probe.BySsid[Hostel] = Connectivity.CaptivePortal;
        r.Portal.Works = false;

        var times = new List<DateTime>();
        var start = r.Clock.Now;
        for (var i = 0; i < 80; i++)
        {
            var before = r.Portal.Attempts.Count;
            var at = r.Clock.Now;
            await r.Tick();
            if (r.Portal.Attempts.Count > before) times.Add(at);
        }

        var gaps = times.Zip(times.Skip(1), (a, b) => (b - a).TotalSeconds).Take(4).ToArray();
        Assert.Equal(new double[] { 15, 30, 60, 120 }, gaps);
        Assert.Contains(r.Notifier.Sent, n => n.Title == "Portal login failing");
    }

    [Fact]
    public async Task A_network_without_internet_for_a_minute_is_skipped()
    {
        var r = new Rig();
        r.Settings.Networks[1].Portal.Enabled = false;
        r.Wifi.InRange.UnionWith(new[] { Hostel, Library });
        r.Wifi.Connected = Hostel;
        r.Probe.BySsid[Hostel] = Connectivity.NoInternet;

        await r.TickFor(TimeSpan.FromSeconds(50));
        Assert.Equal(Hostel, r.Wifi.Connected);

        await r.TickFor(TimeSpan.FromSeconds(30));
        Assert.Equal(Library, r.Wifi.Connected);
        Assert.Contains("skipping it for 10 min", r.Logs);
    }

    [Fact]
    public async Task Stays_put_when_nothing_better_is_available()
    {
        var r = new Rig();
        r.Settings.Networks[1].Portal.Enabled = false;
        r.Settings.General.Watchdog = false;
        r.Wifi.InRange.Add(Hostel);
        r.Wifi.Connected = Hostel;
        r.Probe.BySsid[Hostel] = Connectivity.NoInternet;

        await r.TickFor(TimeSpan.FromMinutes(3));

        Assert.Equal(Hostel, r.Wifi.Connected);
        Assert.Empty(r.Wifi.Joins);
    }

    [Fact]
    public async Task Joins_something_when_disconnected_even_if_everything_was_skipped()
    {
        var r = new Rig();
        r.Settings.Networks[1].Portal.Enabled = false;
        r.Wifi.InRange.Add(Hostel);
        r.Wifi.Connected = Hostel;
        r.Probe.BySsid[Hostel] = Connectivity.NoInternet;
        await r.TickFor(TimeSpan.FromSeconds(75)); // now skipped

        r.Wifi.Connected = null;
        await r.Tick();

        Assert.Equal(Hostel, r.Wifi.Connected);
    }

    [Fact]
    public async Task Manual_pick_is_held_for_ten_minutes_then_released()
    {
        var r = new Rig();
        r.Wifi.InRange.UnionWith(new[] { Hotspot, Hostel });
        r.Wifi.Connected = Hotspot;
        await r.Tick();

        Assert.True(await r.Engine.SwitchToAsync(Hostel, CancellationToken.None));
        Assert.Equal(Hostel, r.Wifi.Connected);

        await r.TickFor(TimeSpan.FromMinutes(9));
        Assert.Equal(Hostel, r.Wifi.Connected);

        await r.TickFor(TimeSpan.FromMinutes(2));
        Assert.Equal(Hotspot, r.Wifi.Connected);
    }

    [Fact]
    public async Task Pause_stops_switching_until_resumed()
    {
        var r = new Rig();
        r.Wifi.InRange.UnionWith(new[] { Hotspot, Hostel });
        r.Wifi.Connected = Hostel;
        r.Engine.Pause(TimeSpan.FromHours(1));

        await r.TickFor(TimeSpan.FromMinutes(2));
        Assert.Equal(Hostel, r.Wifi.Connected);

        r.Engine.Resume();
        await r.Tick();
        await r.Tick();
        Assert.Equal(Hotspot, r.Wifi.Connected);
    }

    [Fact]
    public async Task Daily_data_cap_moves_off_the_hotspot_and_allow_more_brings_it_back()
    {
        var r = new Rig();
        r.Settings.General.HotspotDailyCapMB = 100;
        r.Wifi.InRange.UnionWith(new[] { Hotspot, Hostel });
        r.Wifi.Connected = Hotspot;
        await r.Tick();

        r.Wifi.Bytes += 150 * 1024 * 1024;
        await r.Tick();
        Assert.Equal(Hostel, r.Wifi.Connected);
        Assert.Contains(r.Notifier.Sent, n => n.Title == "Hotspot data cap reached");

        await r.TickFor(TimeSpan.FromMinutes(1));
        Assert.Equal(Hostel, r.Wifi.Connected);

        r.Engine.AllowMoreData(500);
        await r.Tick();
        await r.Tick();
        Assert.Equal(Hotspot, r.Wifi.Connected);
    }

    [Fact]
    public async Task Cap_resets_at_midnight()
    {
        var r = new Rig();
        r.Settings.General.HotspotDailyCapMB = 100;
        r.Wifi.InRange.UnionWith(new[] { Hotspot, Hostel });
        r.Wifi.Connected = Hotspot;
        await r.Tick();
        r.Wifi.Bytes += 150 * 1024 * 1024;
        await r.Tick();
        Assert.Equal(Hostel, r.Wifi.Connected);

        r.Clock.Now = r.Clock.Now.Date.AddDays(1).AddMinutes(1);
        await r.Tick();
        await r.Tick();
        Assert.Equal(Hotspot, r.Wifi.Connected);
    }

    [Fact]
    public async Task Leaves_a_network_the_user_joined_by_hand_alone_while_it_works()
    {
        var r = new Rig();
        r.Wifi.InRange.UnionWith(new[] { "Friend's WiFi", Hotspot });
        r.Wifi.Connected = "Friend's WiFi";

        await r.TickFor(TimeSpan.FromMinutes(2));

        Assert.Equal("Friend's WiFi", r.Wifi.Connected);
    }

    [Fact]
    public async Task Watchdog_reconnects_after_five_minutes_without_internet()
    {
        var r = new Rig();
        r.Settings.Networks[1].Portal.Enabled = false;
        r.Wifi.InRange.Add(Hostel);
        r.Wifi.Connected = Hostel;
        r.Probe.BySsid[Hostel] = Connectivity.NoInternet;

        await r.TickFor(TimeSpan.FromMinutes(4));
        Assert.Equal(0, r.Wifi.Disconnects);

        await r.TickFor(TimeSpan.FromMinutes(2));
        Assert.Equal(1, r.Wifi.Disconnects);
        Assert.Equal(Hostel, r.Wifi.Connected);

        await r.TickFor(TimeSpan.FromMinutes(5));
        Assert.Equal(1, r.Wifi.Disconnects); // cooldown
    }

    [Fact]
    public async Task Failed_join_is_retried_after_a_short_wait()
    {
        var r = new Rig();
        r.Wifi.InRange.UnionWith(new[] { Hotspot, Hostel });
        r.Wifi.FailJoin.Add(Hotspot);

        await r.Tick(); // tries the hotspot first and fails
        Assert.Null(r.Wifi.Connected);
        await r.Tick(); // hotspot is benched for 2 min, so the hostel Wi-Fi it is
        Assert.Equal(Hostel, r.Wifi.Connected);

        r.Wifi.FailJoin.Clear();
        await r.TickFor(TimeSpan.FromMinutes(1));
        Assert.Equal(Hostel, r.Wifi.Connected);
        await r.TickFor(TimeSpan.FromMinutes(2));
        Assert.Equal(Hotspot, r.Wifi.Connected);
    }

    [Fact]
    public async Task Back_online_notification_after_an_outage()
    {
        var r = new Rig();
        r.Settings.Networks[1].Portal.Enabled = false;
        r.Settings.General.Watchdog = false;
        r.Wifi.InRange.Add(Hostel);
        r.Wifi.Connected = Hostel;
        r.Probe.BySsid[Hostel] = Connectivity.NoInternet;
        await r.TickFor(TimeSpan.FromMinutes(3));

        r.Probe.BySsid[Hostel] = Connectivity.Online;
        await r.Tick();

        Assert.Contains(r.Notifier.Sent, n => n.Title == "Back online");
    }

    [Fact]
    public async Task Tailscale_goes_down_on_hotspot_and_up_again_only_if_we_did_it()
    {
        var r = new Rig();
        r.Settings.Tailscale.DownOnHotspot = true;
        r.Wifi.InRange.UnionWith(new[] { Hotspot, Hostel });
        r.Wifi.Connected = Hotspot;

        await r.Tick();
        Assert.False(r.Tailscale.Up);
        Assert.True(r.Engine.TailscaleDownByUs);

        r.Wifi.Lose(Hotspot);
        await r.Tick(); // switches to hostel
        await r.Tick();
        Assert.True(r.Tailscale.Up);
        Assert.Equal(new[] { false, true }, r.Tailscale.Calls);
    }

    [Fact]
    public async Task Tailscale_left_alone_when_the_user_had_it_off()
    {
        var r = new Rig();
        r.Settings.Tailscale.DownOnHotspot = true;
        r.Tailscale.Up = false;
        r.Wifi.InRange.UnionWith(new[] { Hotspot, Hostel });
        r.Wifi.Connected = Hostel;

        await r.TickFor(TimeSpan.FromMinutes(1));

        Assert.Empty(r.Tailscale.Calls);
    }

    [Fact]
    public async Task No_wifi_adapter_is_reported_not_crashed()
    {
        var r = new Rig();
        r.Wifi.IsAvailable = false;
        await r.Tick();
        Assert.False(r.Engine.Status.WifiAvailable);
        Assert.Equal("no Wi-Fi adapter", r.Engine.Status.StateText);
    }
}
