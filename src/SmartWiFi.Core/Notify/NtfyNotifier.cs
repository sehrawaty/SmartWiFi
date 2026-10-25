using System.Net.Http.Headers;
using System.Text;
using SmartWiFi.Core.Models;

namespace SmartWiFi.Core.Notify;

public sealed class PendingMessage
{
    public DateTime Created { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Tags { get; set; }
    public int Priority { get; set; } = 3;
}

public sealed class NotificationQueue
{
    public List<PendingMessage> Items { get; set; } = new();
}

/// <summary>
/// Push notifications through ntfy (https://ntfy.sh or a self-hosted server). Messages are queued and only
/// leave when a send succeeds, so anything raised while offline arrives later, stamped with its real time.
/// </summary>
public sealed class NtfyNotifier : INotifier, IDisposable
{
    private const int MaxQueued = 50;
    private readonly Func<NotificationSettings> _settings;
    private readonly ISecretStore _secrets;
    private readonly IClock _clock;
    private readonly NotificationQueue _queue;
    private readonly Action<NotificationQueue>? _save;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NtfyNotifier(Func<NotificationSettings> settings, ISecretStore secrets, IClock clock,
        NotificationQueue? queue = null, Action<NotificationQueue>? save = null, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _secrets = secrets;
        _clock = clock;
        _queue = queue ?? new NotificationQueue();
        _save = save;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public int Pending { get { lock (_queue) return _queue.Items.Count; } }

    public void Enqueue(string title, string message, string? tags = null, int priority = 3)
    {
        var s = _settings();
        if (!s.Enabled || string.IsNullOrWhiteSpace(s.Topic)) return;
        lock (_queue)
        {
            _queue.Items.Add(new PendingMessage { Created = _clock.Now, Title = title, Message = message, Tags = tags, Priority = priority });
            if (_queue.Items.Count > MaxQueued) _queue.Items.RemoveRange(0, _queue.Items.Count - MaxQueued);
            _save?.Invoke(_queue);
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var s = _settings();
        if (!s.Enabled || string.IsNullOrWhiteSpace(s.Topic)) return;
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            while (true)
            {
                PendingMessage? next;
                lock (_queue) next = _queue.Items.FirstOrDefault();
                if (next is null) return;

                if (!await SendAsync(s, next, ct).ConfigureAwait(false)) return;

                lock (_queue)
                {
                    _queue.Items.Remove(next);
                    _save?.Invoke(_queue);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends one message right now, bypassing the queue (for the "Send test" button).</summary>
    public Task<bool> SendTestAsync(NotificationSettings s, CancellationToken ct) =>
        SendAsync(s, new PendingMessage { Created = _clock.Now, Title = "SmartWiFi test", Message = "Notifications are working.", Tags = "white_check_mark" }, ct);

    private async Task<bool> SendAsync(NotificationSettings s, PendingMessage m, CancellationToken ct)
    {
        var url = $"{s.Server.TrimEnd('/')}/{Uri.EscapeDataString(s.Topic.Trim())}";
        var body = m.Message;
        var age = _clock.Now - m.Created;
        if (age > TimeSpan.FromMinutes(1))
            body = $"{body}\n(sent at {m.Created:HH:mm}, delivered late because the laptop was offline)";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        // ntfy reads these headers; non-ASCII titles must be RFC 2047 encoded.
        req.Headers.TryAddWithoutValidation("Title", HeaderSafe(m.Title));
        req.Headers.TryAddWithoutValidation("Priority", m.Priority.ToString());
        if (!string.IsNullOrEmpty(m.Tags)) req.Headers.TryAddWithoutValidation("Tags", m.Tags);
        var token = _secrets.Unprotect(s.TokenSecret);
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private static string HeaderSafe(string s) =>
        s.All(c => c < 128) ? s : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(s))}?=";

    public void Dispose() => _http.Dispose();
}
