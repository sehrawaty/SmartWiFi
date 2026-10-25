using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SmartWiFi.Core;

namespace SmartWiFi.Platform;

public static class AppPaths
{
    /// <summary>%APPDATA%\SmartWiFi — settings (roams with the user).</summary>
    public static string Config { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartWiFi");

    /// <summary>%LOCALAPPDATA%\SmartWiFi — logs, data usage, notification queue.</summary>
    public static string Data { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartWiFi");

    public static string Settings => Path.Combine(Config, "settings.json");
    public static string Usage => Path.Combine(Data, "usage.json");
    public static string Queue => Path.Combine(Data, "notification-queue.json");
    public static string Logs => Path.Combine(Data, "logs");
}

/// <summary>Passwords are encrypted with DPAPI for the current Windows user, so settings.json never holds them in clear.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SmartWiFi/v1");

    public string Protect(string plain) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        if (!secret.StartsWith(Prefix, StringComparison.Ordinal)) return secret; // hand-edited plain value
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(secret[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { return null; } // made by another user or machine
        catch (FormatException) { return null; }
    }
}

/// <summary>Stops idle sleep while plugged in (the display can still turn off).</summary>
public static partial class KeepAwake
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        AwayModeRequired = 0x00000040,
        Continuous = 0x80000000,
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(ExecutionState state);

    public static bool Active { get; private set; }

    /// <summary>Call from a thread that lives as long as the app (the UI thread).</summary>
    public static void Apply(bool enabled)
    {
        var onAC = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        var want = enabled && onAC;
        if (want == Active) return;
        SetThreadExecutionState(want ? ExecutionState.Continuous | ExecutionState.SystemRequired : ExecutionState.Continuous);
        Active = want;
    }
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "SmartWiFi";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
            else if (key.GetValue(Name) is not null) key.DeleteValue(Name);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Not fatal: the user can still start it by hand.
        }
    }
}

/// <summary>Drives the Tailscale command-line client that ships with Tailscale for Windows.</summary>
public sealed class TailscaleCli : ITailscaleControl
{
    private readonly Func<Core.Models.TailscaleSettings> _settings;

    public TailscaleCli(Func<Core.Models.TailscaleSettings> settings) => _settings = settings;

    public string? ExePath
    {
        get
        {
            var configured = _settings().CliPath;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

            var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
            if (File.Exists(programFiles)) return programFiles;

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var p = Path.Combine(dir.Trim('"'), "tailscale.exe");
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }
    }

    public bool IsInstalled => ExePath is not null;

    public async Task<bool?> IsUpAsync(CancellationToken ct)
    {
        var (code, output) = await RunAsync("status --json", ct).ConfigureAwait(false);
        if (code is null || string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.TryGetProperty("BackendState", out var s) ? s.GetString() == "Running" : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<bool> SetUpAsync(bool up, CancellationToken ct)
    {
        var args = up ? (string.IsNullOrWhiteSpace(_settings().UpArguments) ? "up" : _settings().UpArguments) : "down";
        var (code, _) = await RunAsync(args, ct).ConfigureAwait(false);
        return code == 0;
    }

    private async Task<(int? Code, string Output)> RunAsync(string args, CancellationToken ct)
    {
        if (ExePath is not { } exe) return (null, "");
        var psi = new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return (null, "");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var read = p.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (p.ExitCode, await read.ConfigureAwait(false));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (null, "");
        }
        finally
        {
            p?.Dispose();
        }
    }
}
