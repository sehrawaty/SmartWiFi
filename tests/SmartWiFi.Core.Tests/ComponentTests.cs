using System.Net;
using SmartWiFi.Core;
using SmartWiFi.Core.Data;
using SmartWiFi.Core.Models;
using SmartWiFi.Core.Net;
using SmartWiFi.Core.Notify;
using SmartWiFi.Core.Portal;
using SmartWiFi.Core.Wifi;

namespace SmartWiFi.Core.Tests;

public class ProbeTests
{
    private const string Url = "http://www.msftconnecttest.com/connecttest.txt";
    private const string Expected = "Microsoft Connect Test";

    private static Task<ProbeResult> Run(HttpResponseMessage resp)
    {
        var probe = new HttpConnectivityProbe(() => (Url, Expected), new StubHandler(_ => resp));
        return probe.ProbeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Expected_body_means_online() =>
        Assert.Equal(Connectivity.Online, (await Run(StubHandler.Text("Microsoft Connect Test"))).State);

    [Fact]
    public async Task Redirect_means_portal_with_its_address()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Found);
        resp.Headers.Location = new Uri("http://10.10.0.1:1000/fgtauth?0805a1b2c3");
        var r = await Run(resp);
        Assert.Equal(Connectivity.CaptivePortal, r.State);
        Assert.Equal("http://10.10.0.1:1000/fgtauth?0805a1b2c3", r.PortalUrl);
    }

    [Fact]
    public async Task Html_meta_refresh_is_followed()
    {
        var r = await Run(StubHandler.Text("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=http://phc.prontonetworks.com/cgi-bin/authlogin?URI=x\"></head></html>"));
        Assert.Equal(Connectivity.CaptivePortal, r.State);
        Assert.Equal("http://phc.prontonetworks.com/cgi-bin/authlogin?URI=x", r.PortalUrl);
    }

    [Fact]
    public async Task Javascript_redirect_is_followed()
    {
        var r = await Run(StubHandler.Text("<script>window.location.href='/login.html?x=1';</script>"));
        Assert.Equal("http://www.msftconnecttest.com/login.html?x=1", r.PortalUrl);
    }

    [Fact]
    public async Task Network_error_means_no_internet()
    {
        var probe = new HttpConnectivityProbe(() => (Url, Expected), new StubHandler(_ => throw new HttpRequestException("dns")));
        Assert.Equal(Connectivity.NoInternet, (await probe.ProbeAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task Server_error_means_no_internet() =>
        Assert.Equal(Connectivity.NoInternet, (await Run(StubHandler.Text("", HttpStatusCode.BadGateway))).State);
}

public class PortalTests
{
    [Fact]
    public async Task FortiGate_preset_posts_the_magic_token_to_the_portal_host()
    {
        var handler = new StubHandler(_ => StubHandler.Text("ok"));
        var login = new PortalLogin(_ => handler);
        var config = PortalLogin.Defaults(PortalPreset.FortiGate);
        config.Username = "21BCE0001";

        var r = await login.LoginAsync(config, "s3cret&", "http://10.10.0.1:1000/fgtauth?0805a1b2c3", CancellationToken.None);

        Assert.True(r.RequestSent);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Request.Method);
        var post = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, post.Request.Method);
        Assert.Equal("http://10.10.0.1:1000/", post.Request.RequestUri!.ToString());
        Assert.Contains("magic=0805a1b2c3", post.Body);
        Assert.Contains("username=21BCE0001", post.Body);
        Assert.Contains("password=s3cret%26", post.Body);
        Assert.Contains("4Tredir=http%3A%2F%2Fwww.msftconnecttest.com%2Fredirect", post.Body);
    }

    [Fact]
    public async Task Waits_for_a_redirect_when_the_preset_needs_one()
    {
        var login = new PortalLogin(_ => new StubHandler(_ => StubHandler.Text("ok")));
        var r = await login.LoginAsync(PortalLogin.Defaults(PortalPreset.FortiGate), "p", null, CancellationToken.None);
        Assert.False(r.RequestSent);
    }

    [Fact]
    public async Task Pronto_preset_posts_to_the_fixed_url()
    {
        var handler = new StubHandler(_ => StubHandler.Text("ok"));
        var config = PortalLogin.Defaults(PortalPreset.Pronto);
        config.Username = "u";
        var r = await new PortalLogin(_ => handler).LoginAsync(config, "p", null, CancellationToken.None);

        Assert.True(r.RequestSent);
        var post = Assert.Single(handler.Requests);
        Assert.Equal("phc.prontonetworks.com", post.Request.RequestUri!.Host);
        Assert.Equal("userId=u&password=p&serviceName=ProntoAuthentication&Submit22=Login", post.Body);
    }

    [Fact]
    public async Task Get_method_puts_fields_in_the_query()
    {
        var handler = new StubHandler(_ => StubHandler.Text("ok"));
        var config = new PortalConfig
        {
            Enabled = true, LoginUrl = "http://192.168.1.1/login?lang=en", Method = "GET", Username = "me",
            Fields = { new("user", "{username}"), new("pass", "{password}") },
        };
        await new PortalLogin(_ => handler).LoginAsync(config, "a b", null, CancellationToken.None);
        Assert.Equal("http://192.168.1.1/login?lang=en&user=me&pass=a%20b", handler.Requests.Single().Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Unreachable_portal_is_reported_not_thrown()
    {
        var config = PortalLogin.Defaults(PortalPreset.Pronto);
        var r = await new PortalLogin(_ => new StubHandler(_ => throw new HttpRequestException("refused")))
            .LoginAsync(config, "p", null, CancellationToken.None);
        Assert.False(r.RequestSent);
        Assert.Contains("couldn't reach", r.Detail);
    }

    [Theory]
    [InlineData("http://10.0.0.1:1000/fgtauth?abc123", "abc123")]
    [InlineData("http://10.0.0.1/login?magic=xyz&foo=1", "xyz")]
    [InlineData("http://10.0.0.1/login?foo=1", "")]
    [InlineData("http://10.0.0.1/login", "")]
    public void Magic_token_extraction(string url, string magic) =>
        Assert.Equal(magic, PortalLogin.MagicFrom(new Uri(url)));

    [Fact]
    public void Backoff_steps() =>
        Assert.Equal(new[] { 0, 15, 30, 60, 120, 120 },
            Enumerable.Range(0, 6).Select(i => (int)PortalBackoff.After(i).TotalSeconds).ToArray());
}

public class DataMeterTests
{
    private static readonly DateTime Day = new(2026, 10, 26, 9, 0, 0);

    [Fact]
    public void Adds_deltas_and_survives_counter_resets()
    {
        var m = new DataMeter(new DataUsageState());
        m.Sample(Day, 1000);
        m.Sample(Day, 5000);   // +4000
        m.Sample(Day, 200);    // counter restarted: +200
        m.Sample(Day, null);   // left the hotspot
        m.Sample(Day, 90_000); // back: baseline only
        m.Sample(Day, 90_100); // +100
        Assert.Equal(4300, m.TodayBytes);
    }

    [Fact]
    public void Rolls_over_at_midnight()
    {
        var m = new DataMeter(new DataUsageState());
        m.Sample(Day, 0);
        m.Sample(Day, 500 * DataMeter.MB);
        Assert.True(m.CapReached(Day, 100));
        Assert.False(m.CapReached(Day.AddDays(1), 100));
        Assert.Equal(0, m.TodayBytes);
    }

    [Fact]
    public void Allow_more_counts_from_current_usage()
    {
        var m = new DataMeter(new DataUsageState());
        m.Sample(Day, 0);
        m.Sample(Day, 150 * DataMeter.MB);
        Assert.True(m.CapReached(Day, 100));

        m.AllowMore(Day, 200, 100);
        Assert.False(m.CapReached(Day, 100));
        m.Sample(Day, 349 * DataMeter.MB);
        Assert.False(m.CapReached(Day, 100));
        m.Sample(Day, 351 * DataMeter.MB);
        Assert.True(m.CapReached(Day, 100));
    }

    [Fact]
    public void Cap_notification_fires_once_per_day()
    {
        var m = new DataMeter(new DataUsageState());
        m.Sample(Day, 0);
        m.Sample(Day, 200 * DataMeter.MB);
        Assert.True(m.ShouldNotifyCap(Day, 100));
        Assert.False(m.ShouldNotifyCap(Day, 100));
    }

    [Fact]
    public void No_cap_means_never_reached()
    {
        var m = new DataMeter(new DataUsageState());
        m.Sample(Day, 0);
        m.Sample(Day, long.MaxValue / 2);
        Assert.False(m.CapReached(Day, 0));
    }
}

public class NotifierTests
{
    private sealed class Secrets : ISecretStore
    {
        public string Protect(string p) => p;
        public string? Unprotect(string? s) => s;
    }

    [Fact]
    public async Task Queues_while_offline_and_delivers_later_with_the_original_time()
    {
        var clock = new FakeClock();
        var online = false;
        var handler = new StubHandler(_ => online ? StubHandler.Text("{}") : throw new HttpRequestException("offline"));
        var settings = new NotificationSettings { Enabled = true, Topic = "my-laptop", TokenSecret = "tk_123" };
        var n = new NtfyNotifier(() => settings, new Secrets(), clock, handler: handler);

        n.Enqueue("Switched to Hostel-WiFi", "Pixel Hotspot has no internet.");
        await n.FlushAsync(CancellationToken.None);
        Assert.Equal(1, n.Pending);

        clock.Advance(TimeSpan.FromMinutes(5));
        online = true;
        await n.FlushAsync(CancellationToken.None);

        Assert.Equal(0, n.Pending);
        var (req, body) = handler.Requests.Last();
        Assert.Equal("https://ntfy.sh/my-laptop", req.RequestUri!.ToString());
        Assert.Equal("Switched to Hostel-WiFi", req.Headers.GetValues("Title").Single());
        Assert.Equal("Bearer tk_123", req.Headers.Authorization!.ToString());
        Assert.Contains("sent at 10:00", body);
    }

    [Fact]
    public void Disabled_notifications_queue_nothing()
    {
        var n = new NtfyNotifier(() => new NotificationSettings { Enabled = false, Topic = "x" }, new Secrets(), new FakeClock());
        n.Enqueue("a", "b");
        Assert.Equal(0, n.Pending);
    }

    [Fact]
    public async Task Non_ascii_titles_are_encoded()
    {
        var handler = new StubHandler(_ => StubHandler.Text("{}"));
        var n = new NtfyNotifier(() => new NotificationSettings { Enabled = true, Topic = "t" }, new Secrets(), new FakeClock(), handler: handler);
        n.Enqueue("Switched to Café Wi-Fi", "x");
        await n.FlushAsync(CancellationToken.None);
        Assert.StartsWith("=?UTF-8?B?", handler.Requests.Single().Request.Headers.GetValues("Title").Single());
    }
}

public class ProfileXmlTests
{
    [Fact]
    public void Personal_network_profile_has_hex_ssid_and_escaped_values()
    {
        var xml = WlanProfileXml.Build("Yash's <Phone>", WifiSecurity.Wpa2Personal, "p&ss");
        Assert.Contains("<name>Yash&apos;s &lt;Phone&gt;</name>", xml);
        Assert.Contains($"<hex>{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("Yash's <Phone>"))}</hex>", xml);
        Assert.Contains("<authentication>WPA2PSK</authentication><encryption>AES</encryption>", xml);
        Assert.Contains("<keyMaterial>p&amp;ss</keyMaterial>", xml);
        System.Xml.Linq.XDocument.Parse(xml); // well-formed
    }

    [Fact]
    public void Open_network_has_no_key()
    {
        var xml = WlanProfileXml.Build("Hostel-WiFi", WifiSecurity.Open, null);
        Assert.Contains("<authentication>open</authentication><encryption>none</encryption>", xml);
        Assert.DoesNotContain("sharedKey", xml);
    }

    [Fact]
    public void Wpa3_uses_sae()
    {
        Assert.Contains("WPA3SAE", WlanProfileXml.Build("x", WifiSecurity.Wpa3Personal, "12345678"));
    }

    [Fact]
    public void Enterprise_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => WlanProfileXml.Build("eduroam", WifiSecurity.Enterprise, "x"));
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swf-{Guid.NewGuid():N}.json");
        try
        {
            var s = new AppSettings();
            s.Networks.Add(new NetworkEntry { Ssid = "A", IsHotspot = true, Portal = PortalLogin.Defaults(PortalPreset.FortiGate) });
            s.General.HotspotDailyCapMB = 2048;
            Storage.JsonFile.Save(path, s);

            var back = Storage.JsonFile.Load<AppSettings>(path);
            Assert.Equal("A", back.Networks[0].Ssid);
            Assert.Equal(PortalPreset.FortiGate, back.Networks[0].Portal.Preset);
            Assert.Equal(4, back.Networks[0].Portal.Fields.Count);
            Assert.Equal(2048, back.General.HotspotDailyCapMB);
            Assert.Contains("\"preset\": \"FortiGate\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Broken_file_falls_back_to_defaults_and_is_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swf-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var s = Storage.JsonFile.Load<AppSettings>(path);
            Assert.Empty(s.Networks);
            Assert.True(File.Exists(path + ".broken"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".broken");
        }
    }
}
