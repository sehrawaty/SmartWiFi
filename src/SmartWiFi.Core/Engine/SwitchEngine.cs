using SmartWiFi.Core.Data;
using SmartWiFi.Core.Logging;
using SmartWiFi.Core.Models;
using SmartWiFi.Core.Portal;

namespace SmartWiFi.Core.Engine;

/// <summary>
/// The whole decision loop, free of any Windows API so it can be tested with fakes.
/// Every check: scan, probe the internet, log in to a portal if needed, meter hotspot data,
/// then decide whether to stay, switch up to a more preferred network, or fall back.
/// </summary>
public sealed class SwitchEngine
{
    private static readonly TimeSpan AfterSwitch = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FailedJoinSkip = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WatchdogCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TailscalePoll = TimeSpan.FromSeconds(30);

    private readonly Func<AppSettings> _settings;
    private readonly IWifiAdapter _wifi;
    private readonly IConnectivityProbe _probe;
    private readonly IPortalLogin _portal;
    private readonly INotifier _notifier;
    private readonly ITailscaleControl? _tailscale;
    private readonly DataMeter _meter;
    private readonly ISecretStore _secrets;
    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    private readonly Dictionary<string, int> _streak = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _skippedUntil = new(StringComparer.Ordinal);

    private string? _lastSsid;
    private DateTime? _connectedSince;
    private DateTime? _notWorkingSince;
    private DateTime? _offlineSince;
    private DateTime _lastOnline;
    private DateTime _lastWatchdog = DateTime.MinValue;
    private Connectivity _lastState = Connectivity.Disconnected;

    private (string Ssid, DateTime Until)? _hold;
    private DateTime? _pausedUntil;

    private string? _portalSsid;
    private string? _portalUrl;
    private int _portalFailures;
    private DateTime _portalNextAttempt = DateTime.MinValue;
    private bool _portalFailureNotified;

    private bool _tailscaleDownByUs;
    private bool _tailscaleUserOverride;
    private bool? _tailscaleUp;
    private DateTime _tailscaleCheckedAt = DateTime.MinValue;

    public SwitchEngine(
        Func<AppSettings> settings,
        IWifiAdapter wifi,
        IConnectivityProbe probe,
        IPortalLogin portal,
        INotifier notifier,
        ITailscaleControl? tailscale,
        DataMeter meter,
        ISecretStore secrets,
        ILog log,
        IClock clock,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _settings = settings;
        _wifi = wifi;
        _probe = probe;
        _portal = portal;
        _notifier = notifier;
        _tailscale = tailscale;
        _meter = meter;
        _secrets = secrets;
        _log = log;
        _clock = clock;
        _delay = delay ?? Task.Delay;
        _lastOnline = clock.Now;
    }

    public EngineStatus Status { get; private set; } = EngineStatus.Starting;
    public event Action<EngineStatus>? StatusChanged;

    public bool TailscaleDownByUs => _tailscaleDownByUs;

    // ───────────────────────────── loop ─────────────────────────────

    /// <summary>Runs checks until cancelled. Call <see cref="Nudge"/> to check right away.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _log.Info("SmartWiFi started");
        while (!ct.IsCancellationRequested)
        {
            TimeSpan next;
            try
            {
                next = await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _log.Error($"check failed: {e.GetType().Name}: {e.Message}");
                next = Interval();
            }

            try
            {
                await _wake.WaitAsync(next, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Ask for a check now (e.g. Windows reported a network change).</summary>
    public void Nudge()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await TickCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private TimeSpan Interval() => TimeSpan.FromSeconds(Math.Clamp(_settings().General.CheckIntervalSeconds, 5, 300));

    private async Task<TimeSpan> TickCoreAsync(CancellationToken ct)
    {
        var s = _settings();
        var g = s.General;
        var now = _clock.Now;

        if (!_wifi.IsAvailable)
        {
            Publish(new EngineStatus { WifiAvailable = false });
            return Interval();
        }

        // Scanning while connected costs a little latency, so skip it when we're already
        // happily on the most preferred network: there is nothing better to look for.
        var before = _wifi.GetCurrent();
        var scan = !(before is not null && s.RankOf(before.Ssid) == 0 && _lastState == Connectivity.Online);
        if (scan)
        {
            try { await _wifi.ScanAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.Warn($"scan failed: {e.Message}"); scan = false; }
        }

        var visible = _wifi.GetVisibleNetworks();
        var current = _wifi.GetCurrent();
        TrackNetworkChange(current, now);
        if (scan) UpdateStreaks(s, visible);

        var entry = s.Find(current?.Ssid);
        var probe = current is null
            ? new ProbeResult(Connectivity.Disconnected)
            : await _probe.ProbeAsync(ct).ConfigureAwait(false);

        if (current is not null && entry is { Portal.Enabled: true } &&
            probe.State is Connectivity.CaptivePortal or Connectivity.NoInternet)
        {
            probe = await MaybeLoginAsync(entry, probe, force: false, ct).ConfigureAwait(false);
            now = _clock.Now;
        }

        ApplyConnectivity(s, current, probe.State, now);

        // Hotspot data.
        _meter.Sample(now, entry?.IsHotspot == true ? _wifi.GetTotalBytes() : null);
        var capReached = _meter.CapReached(now, g.HotspotDailyCapMB);
        if (entry?.IsHotspot == true && _meter.ShouldNotifyCap(now, g.HotspotDailyCapMB))
        {
            _log.Warn($"hotspot data cap of {g.HotspotDailyCapMB} MB reached, moving off the hotspot until midnight");
            if (s.Notifications.OnDataCap)
                _notifier.Enqueue("Hotspot data cap reached", $"Used {Mb(_meter.TodayBytes)} today. Staying off {entry.Ssid} until midnight.", "bar_chart", 4);
        }

        await ApplyTailscaleAsync(s, entry, probe.State, now, ct).ConfigureAwait(false);

        var next = Interval();
        var switched = false;

        if (_pausedUntil is { } p && p <= now)
        {
            _pausedUntil = null;
            _log.Info("auto-switching resumed");
        }

        if (_pausedUntil is null)
        {
            var decision = Decide(s, visible, current, probe.State, capReached, now);
            if (decision is { } d)
            {
                switched = await JoinAsync(s, d.Entry, d.Network, d.Reason, ct).ConfigureAwait(false);
                if (switched) next = AfterSwitch;
            }
        }

        if (!switched)
            await WatchdogAsync(s, current, now, ct).ConfigureAwait(false);

        if (probe.State == Connectivity.Online)
        {
            try { await _notifier.FlushAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.Warn($"notification failed: {e.Message}"); }
        }

        PublishFrom(s, switched ? _wifi.GetCurrent() : current, switched ? Connectivity.NoInternet : probe.State, visible, switched);
        return next;
    }

    // ───────────────────────────── decisions ─────────────────────────────

    internal sealed record Decision(NetworkEntry Entry, VisibleNetwork Network, string Reason);

    internal Decision? Decide(AppSettings s, IReadOnlyList<VisibleNetwork> visible, CurrentLink? current,
        Connectivity state, bool capReached, DateTime now)
    {
        var g = s.General;
        var seen = visible.GroupBy(v => v.Ssid, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(v => v.ProfileName is not null).ThenByDescending(v => v.SignalQuality).First(), StringComparer.Ordinal);

        var candidates = s.Networks
            .Select((n, i) => (Entry: n, Rank: i))
            .Where(c => !string.IsNullOrWhiteSpace(c.Entry.Ssid) && seen.ContainsKey(c.Entry.Ssid))
            .Where(c => !(c.Entry.IsHotspot && capReached && g.HotspotDailyCapMB > 0))
            .ToList();

        var currentEntry = s.Find(current?.Ssid);
        var currentRank = s.RankOf(current?.Ssid);
        var currentCapped = currentEntry?.IsHotspot == true && capReached && g.HotspotDailyCapMB > 0;
        var currentWorking = current is not null && !currentCapped &&
                             (state == Connectivity.Online || !IsSkipped(current.Ssid, now));

        // A network the user picked from the menu stays for a while, as long as it works.
        if (_hold is { } hold)
        {
            if (hold.Until <= now || current?.Ssid != hold.Ssid || !currentWorking) _hold = null;
            else return null;
        }

        if (currentWorking)
        {
            // Not one of ours (joined by hand in Windows): leave it alone while it has internet.
            if (currentEntry is null)
                return state == Connectivity.Online ? null : FallbackFrom(current, candidates, seen, now, $"{current!.Ssid} has no internet");

            var better = candidates.FirstOrDefault(c =>
                c.Rank < currentRank && !IsSkipped(c.Entry.Ssid, now) && Streak(c.Entry.Ssid) >= Math.Max(1, g.SwitchUpAfterScans));
            return better.Entry is null ? null : new Decision(better.Entry, seen[better.Entry.Ssid], $"{better.Entry.Ssid} is back in range");
        }

        var reason = current is null ? "not connected"
            : currentCapped ? "hotspot data cap reached"
            : $"{current.Ssid} has no internet";
        return FallbackFrom(current, candidates, seen, now, reason);
    }

    private Decision? FallbackFrom(CurrentLink? current, List<(NetworkEntry Entry, int Rank)> candidates,
        Dictionary<string, VisibleNetwork> seen, DateTime now, string reason)
    {
        var target = candidates.FirstOrDefault(c => c.Entry.Ssid != current?.Ssid && !IsSkipped(c.Entry.Ssid, now));

        // Everything is marked bad. When disconnected, trying something beats sitting offline.
        if (target.Entry is null && current is null)
            target = candidates.FirstOrDefault();

        return target.Entry is null ? null : new Decision(target.Entry, seen[target.Entry.Ssid], reason);
    }

    private bool IsSkipped(string ssid, DateTime now) =>
        _skippedUntil.TryGetValue(ssid, out var until) && until > now;

    private int Streak(string ssid) => _streak.TryGetValue(ssid, out var n) ? n : 0;

    private void UpdateStreaks(AppSettings s, IReadOnlyList<VisibleNetwork> visible)
    {
        var seen = visible.Select(v => v.Ssid).ToHashSet(StringComparer.Ordinal);
        foreach (var n in s.Networks)
            _streak[n.Ssid] = seen.Contains(n.Ssid) ? Streak(n.Ssid) + 1 : 0;
    }

    private void TrackNetworkChange(CurrentLink? current, DateTime now)
    {
        if (current?.Ssid == _lastSsid) return;
        _log.Info($"network: {_lastSsid ?? "none"} → {current?.Ssid ?? "none"}");
        _lastSsid = current?.Ssid;
        _connectedSince = current is null ? null : now;
        _notWorkingSince = null;
        _tailscaleUserOverride = false;
    }

    private void ApplyConnectivity(AppSettings s, CurrentLink? current, Connectivity state, DateTime now)
    {
        var g = s.General;
        if (state == Connectivity.Online)
        {
            if (_offlineSince is { } since && now - since >= TimeSpan.FromMinutes(1))
            {
                _log.Ok($"back online on {current?.Ssid} after {Human(now - since)}");
                if (s.Notifications.OnBackOnline)
                    _notifier.Enqueue("Back online", $"Online on {current?.Ssid} after {Human(now - since)} without internet.", "signal_strength");
            }
            _offlineSince = null;
            _notWorkingSince = null;
            _lastOnline = now;
        }
        else
        {
            _offlineSince ??= now;
            if (current is not null)
            {
                _notWorkingSince ??= now;
                if (now - _notWorkingSince >= TimeSpan.FromSeconds(Math.Max(10, g.OfflineSkipSeconds)) && !IsSkipped(current.Ssid, now))
                {
                    _skippedUntil[current.Ssid] = now.AddMinutes(Math.Max(1, g.SkipMinutes));
                    _log.Warn($"{current.Ssid} has had no internet for {g.OfflineSkipSeconds} s, skipping it for {g.SkipMinutes} min");
                }
            }
        }
        _lastState = state;
    }

    // ───────────────────────────── actions ─────────────────────────────

    private async Task<bool> JoinAsync(AppSettings s, NetworkEntry entry, VisibleNetwork network, string reason, CancellationToken ct)
    {
        var password = _secrets.Unprotect(entry.PasswordSecret);
        var profile = _wifi.EnsureProfile(network, password);
        if (!profile.Ok)
        {
            _log.Error($"can't join {entry.Ssid}: {profile.Error}");
            _skippedUntil[entry.Ssid] = _clock.Now.AddMinutes(Math.Max(1, s.General.SkipMinutes));
            return false;
        }

        _log.Info($"{reason}, switching to {entry.Ssid}");
        var ok = await _wifi.ConnectAsync(profile.ProfileName, ct).ConfigureAwait(false);
        var now = _clock.Now;
        if (!ok)
        {
            _log.Warn($"couldn't join {entry.Ssid}, will retry in {FailedJoinSkip.TotalMinutes:0} min");
            _skippedUntil[entry.Ssid] = now + FailedJoinSkip;
            return false;
        }

        _log.Ok($"joined {entry.Ssid}");
        if (s.Notifications.OnSwitch)
            _notifier.Enqueue($"Switched to {entry.Ssid}", Capitalize(reason) + ".", "arrows_counterclockwise", 2);

        TrackNetworkChange(_wifi.GetCurrent() ?? new CurrentLink(entry.Ssid, profile.ProfileName, network.SignalQuality, null), now);
        return true;
    }

    private async Task<ProbeResult> MaybeLoginAsync(NetworkEntry entry, ProbeResult probe, bool force, CancellationToken ct)
    {
        var s = _settings();
        if (_portalSsid != entry.Ssid)
        {
            _portalSsid = entry.Ssid;
            _portalUrl = null;
            _portalFailures = 0;
            _portalNextAttempt = DateTime.MinValue;
            _portalFailureNotified = false;
        }

        if (probe.PortalUrl is not null) _portalUrl = probe.PortalUrl;
        if (!force && _clock.Now < _portalNextAttempt) return probe;

        _log.Info($"on {entry.Ssid} without internet, logging in to the portal");
        var attempt = await _portal.LoginAsync(entry.Portal, _secrets.Unprotect(entry.Portal.PasswordSecret), _portalUrl, ct).ConfigureAwait(false);

        var after = probe;
        if (attempt.RequestSent)
        {
            after = await _probe.ProbeAsync(ct).ConfigureAwait(false);
            if (after.State != Connectivity.Online)
            {
                // Some portals take a moment to open the gate.
                await _delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                after = await _probe.ProbeAsync(ct).ConfigureAwait(false);
            }
        }

        if (after.State == Connectivity.Online)
        {
            _log.Ok("portal: logged in, online");
            _portalFailures = 0;
            _portalNextAttempt = DateTime.MinValue;
            _portalFailureNotified = false;
            return after;
        }

        _portalFailures++;
        var wait = PortalBackoff.After(_portalFailures);
        _portalNextAttempt = _clock.Now + wait;
        _log.Warn($"portal login didn't work ({attempt.Detail}), next try in {Human(wait)}");

        if (_portalFailures >= 3 && !_portalFailureNotified && s.Notifications.OnPortalFailure)
        {
            _portalFailureNotified = true;
            _notifier.Enqueue("Portal login failing", $"Tried {_portalFailures} times on {entry.Ssid}: {attempt.Detail}.", "warning", 4);
        }
        return after;
    }

    private async Task WatchdogAsync(AppSettings s, CurrentLink? current, DateTime now, CancellationToken ct)
    {
        var g = s.General;
        if (!g.Watchdog || current is null) return;
        if (now - _lastOnline < TimeSpan.FromMinutes(Math.Max(1, g.WatchdogMinutes))) return;
        if (now - _lastWatchdog < WatchdogCooldown) return;

        _lastWatchdog = now;
        _log.Warn($"no internet for {g.WatchdogMinutes} min, reconnecting {current.Ssid} to unstick the adapter");
        await _wifi.DisconnectAsync(ct).ConfigureAwait(false);
        await _delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        await _wifi.ConnectAsync(current.ProfileName ?? current.Ssid, ct).ConfigureAwait(false);
    }

    private async Task ApplyTailscaleAsync(AppSettings s, NetworkEntry? entry, Connectivity state, DateTime now, CancellationToken ct)
    {
        if (_tailscale is null || !_tailscale.IsInstalled) return;

        if (now - _tailscaleCheckedAt >= TailscalePoll || _tailscaleUp is null)
        {
            _tailscaleUp = await _tailscale.IsUpAsync(ct).ConfigureAwait(false);
            _tailscaleCheckedAt = now;
        }

        var onHotspot = entry?.IsHotspot == true;
        if (s.Tailscale.DownOnHotspot && onHotspot && state == Connectivity.Online && !_tailscaleDownByUs && !_tailscaleUserOverride && _tailscaleUp == true)
        {
            if (await _tailscale.SetUpAsync(false, ct).ConfigureAwait(false))
            {
                _tailscaleDownByUs = true;
                _tailscaleUp = false;
                _log.Info("Tailscale switched off while on the hotspot");
            }
        }
        else if (_tailscaleDownByUs && (!onHotspot || !s.Tailscale.DownOnHotspot) && entry is not null)
        {
            if (await _tailscale.SetUpAsync(true, ct).ConfigureAwait(false))
            {
                _tailscaleDownByUs = false;
                _tailscaleUp = true;
                _log.Info("Tailscale switched back on");
            }
        }
    }

    // ───────────────────────────── commands from the menu ─────────────────────────────

    public async Task<bool> SwitchToAsync(string ssid, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = _settings();
            var entry = s.Find(ssid);
            if (entry is null) return false;

            try { await _wifi.ScanAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.Warn($"scan failed: {e.Message}"); }

            var network = _wifi.GetVisibleNetworks().Where(v => v.Ssid == ssid).OrderByDescending(v => v.SignalQuality).FirstOrDefault();
            if (network is null)
            {
                _log.Warn($"{ssid} isn't in range");
                return false;
            }

            var now = _clock.Now;
            _skippedUntil.Remove(ssid);
            _hold = (ssid, now.AddMinutes(Math.Max(1, s.General.ManualHoldMinutes)));
            var ok = await JoinAsync(s, entry, network, "picked from the menu", ct).ConfigureAwait(false);
            if (!ok) _hold = null;
            else _log.Info($"keeping {ssid} for {s.General.ManualHoldMinutes} min while it works");
            PublishFrom(s, _wifi.GetCurrent(), Connectivity.NoInternet, _wifi.GetVisibleNetworks(), checking: ok);
            Nudge();
            return ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LoginToPortalNowAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = _settings();
            var current = _wifi.GetCurrent();
            var entry = s.Find(current?.Ssid);
            if (entry is not { Portal.Enabled: true })
            {
                _log.Warn($"no portal login set up for {current?.Ssid ?? "this network"}");
                return;
            }
            var probe = await _probe.ProbeAsync(ct).ConfigureAwait(false);
            if (probe.State == Connectivity.Online)
            {
                _log.Info("already online, no portal login needed");
            }
            else
            {
                probe = await MaybeLoginAsync(entry, probe, force: true, ct).ConfigureAwait(false);
            }
            ApplyConnectivity(s, current, probe.State, _clock.Now);
            PublishFrom(s, current, probe.State, _wifi.GetVisibleNetworks(), checking: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Pause(TimeSpan duration)
    {
        _pausedUntil = _clock.Now + duration;
        _log.Info($"auto-switching paused until {_pausedUntil:HH:mm}");
        Publish(Status with { PausedUntil = _pausedUntil });
    }

    public void Resume()
    {
        _pausedUntil = null;
        _log.Info("auto-switching resumed");
        Publish(Status with { PausedUntil = null });
        Nudge();
    }

    public void AllowMoreData(long megabytes)
    {
        var s = _settings();
        _meter.AllowMore(_clock.Now, megabytes, s.General.HotspotDailyCapMB);
        _log.Info($"allowed {megabytes} MB more hotspot data today");
        Nudge();
    }

    /// <summary>The user flipped Tailscale from the menu: respect that until the network changes.</summary>
    public async Task<bool> SetTailscaleAsync(bool up, CancellationToken ct)
    {
        if (_tailscale is null || !_tailscale.IsInstalled) return false;
        var ok = await _tailscale.SetUpAsync(up, ct).ConfigureAwait(false);
        if (ok)
        {
            _tailscaleUp = up;
            _tailscaleUserOverride = true;
            _tailscaleDownByUs = false;
            _log.Info($"Tailscale turned {(up ? "on" : "off")} from the menu");
            Publish(Status with { TailscaleUp = up });
        }
        return ok;
    }

    /// <summary>Undo anything we changed: turn Tailscale back on if we turned it off.</summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        _meter.Flush(_clock.Now);
        if (_tailscaleDownByUs && _tailscale is not null)
        {
            await _tailscale.SetUpAsync(true, ct).ConfigureAwait(false);
            _tailscaleDownByUs = false;
        }
        _log.Info("SmartWiFi stopped");
    }

    // ───────────────────────────── status ─────────────────────────────

    private void PublishFrom(AppSettings s, CurrentLink? current, Connectivity state, IReadOnlyList<VisibleNetwork> visible, bool checking)
    {
        var now = _clock.Now;
        var entry = s.Find(current?.Ssid);
        var cap = s.General.HotspotDailyCapMB;
        Publish(new EngineStatus
        {
            WifiAvailable = true,
            Ssid = current?.Ssid,
            SignalQuality = current?.SignalQuality,
            Rssi = current?.Rssi,
            State = current is null ? Connectivity.Disconnected : state,
            Checking = checking,
            ConnectedSince = _connectedSince,
            IsHotspot = entry?.IsHotspot == true,
            DataTodayBytes = _meter.TodayBytes,
            DataLimitBytes = cap > 0 ? cap * DataMeter.MB + _meter.ExtraAllowanceBytes : null,
            DataCapReached = _meter.CapReached(now, cap),
            PausedUntil = _pausedUntil,
            HoldSsid = _hold?.Ssid,
            HoldUntil = _hold?.Until,
            TailscaleInstalled = _tailscale?.IsInstalled == true,
            TailscaleUp = _tailscaleUp,
            Visible = visible,
        });
    }

    private void Publish(EngineStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    internal static string Human(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{(int)Math.Round(t.TotalSeconds)} s"
        : t.TotalMinutes < 90 ? $"{(int)Math.Round(t.TotalMinutes)} min"
        : $"{t.TotalHours:0.#} h";

    internal static string Mb(long bytes) => $"{bytes / (double)DataMeter.MB:0} MB";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
