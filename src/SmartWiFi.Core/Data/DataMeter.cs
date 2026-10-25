namespace SmartWiFi.Core.Data;

public sealed class DataUsageState
{
    public string Day { get; set; } = "";
    public long HotspotBytes { get; set; }
    public long ExtraAllowanceBytes { get; set; }
    public bool CapNotified { get; set; }
}

/// <summary>
/// Counts bytes used on the hotspot today. It is fed the interface's running byte counter and adds the
/// difference since the last reading, so counter resets (a reconnect, sleep) never produce negative usage.
/// </summary>
public sealed class DataMeter
{
    public const long MB = 1024L * 1024L;

    private readonly DataUsageState _state;
    private readonly Action<DataUsageState>? _save;
    private long? _lastCounter;
    private DateTime _lastSave;

    public DataMeter(DataUsageState state, Action<DataUsageState>? save = null)
    {
        _state = state;
        _save = save;
    }

    public long TodayBytes => _state.HotspotBytes;
    public long ExtraAllowanceBytes => _state.ExtraAllowanceBytes;

    /// <summary>Call on every check. Pass the counter only while on the hotspot; pass null otherwise.</summary>
    public void Sample(DateTime now, long? counter)
    {
        Rollover(now);

        if (counter is null)
        {
            _lastCounter = null;
            return;
        }

        if (_lastCounter is { } last)
        {
            var delta = counter.Value - last;
            if (delta < 0) delta = counter.Value; // counter restarted
            _state.HotspotBytes += delta;
        }
        _lastCounter = counter;

        if (now - _lastSave > TimeSpan.FromMinutes(1)) Save(now);
    }

    public bool CapReached(DateTime now, long capMB)
    {
        Rollover(now);
        return capMB > 0 && _state.HotspotBytes >= capMB * MB + _state.ExtraAllowanceBytes;
    }

    /// <summary>Returns true the first time the cap is hit today.</summary>
    public bool ShouldNotifyCap(DateTime now, long capMB)
    {
        if (!CapReached(now, capMB) || _state.CapNotified) return false;
        _state.CapNotified = true;
        Save(now);
        return true;
    }

    /// <summary>Allows N more MB today, counted from whichever is later: the cap or what's been used.</summary>
    public void AllowMore(DateTime now, long megabytes, long capMB)
    {
        Rollover(now);
        var limit = capMB * MB + _state.ExtraAllowanceBytes;
        var from = Math.Max(limit, _state.HotspotBytes);
        _state.ExtraAllowanceBytes = from + megabytes * MB - capMB * MB;
        _state.CapNotified = false;
        Save(now);
    }

    public void Flush(DateTime now) => Save(now);

    private void Rollover(DateTime now)
    {
        var day = now.ToString("yyyy-MM-dd");
        if (_state.Day == day) return;
        _state.Day = day;
        _state.HotspotBytes = 0;
        _state.ExtraAllowanceBytes = 0;
        _state.CapNotified = false;
        Save(now);
    }

    private void Save(DateTime now)
    {
        _lastSave = now;
        _save?.Invoke(_state);
    }
}
