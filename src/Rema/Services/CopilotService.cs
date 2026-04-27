using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;

namespace Rema.Services;

public enum CopilotSignInResult { Success, CliNotFound, Failed }

public class CopilotService : IAsyncDisposable
{
    private CopilotClient? _client;
    public CopilotClient? Client => _client;
    private List<ModelInfo>? _models;
    private string? _fastestModelId;
    private long _connectionGeneration;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private Action? _cleanupProcessHandlers;
    private IDisposable? _lifecycleSub;

    public event Action? Reconnected;
    public event Action<long>? CliProcessExited;
    public event Action<string>? SessionDeletedRemotely;

    public bool IsConnected => _client?.State == ConnectionState.Connected;
    public ConnectionState State => _client?.State ?? ConnectionState.Disconnected;
    public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

    public async Task ConnectAsync(CancellationToken ct = default)
        => await ConnectCoreAsync(forceReconnect: false, ct);

    public async Task ForceReconnectAsync(CancellationToken ct = default)
        => await ConnectCoreAsync(forceReconnect: true, ct);

    private async Task ConnectCoreAsync(bool forceReconnect, CancellationToken ct)
    {
        CopilotClient? oldClient = null;
        var generationBeforeWait = Interlocked.Read(ref _connectionGeneration);

        await _connectGate.WaitAsync(ct);
        try
        {
            if (!forceReconnect && _client?.State == ConnectionState.Connected)
                return;

            if (forceReconnect
                && Interlocked.Read(ref _connectionGeneration) != generationBeforeWait
                && _client?.State == ConnectionState.Connected)
                return;

            oldClient = _client;
            var clientOptions = new CopilotClientOptions
            {
                CliPath = FindCliPath() ?? "copilot",
                LogLevel = "error",
            };

            ConfigureAuthentication(clientOptions);

            var newClient = new CopilotClient(clientOptions);
            await newClient.StartAsync(ct);

            _client = newClient;
            _models = null;
            _fastestModelId = null;
            Interlocked.Increment(ref _connectionGeneration);

            _cleanupProcessHandlers?.Invoke();
            _cleanupProcessHandlers = null;
            _lifecycleSub?.Dispose();
            _lifecycleSub = null;

            SubscribeToCliProcessExit(newClient);

            _lifecycleSub = newClient.On(SessionLifecycleEventTypes.Deleted, evt =>
            {
                if (!string.IsNullOrEmpty(evt.SessionId))
                    SessionDeletedRemotely?.Invoke(evt.SessionId);
            });
        }
        finally
        {
            _connectGate.Release();
        }

        if (oldClient is not null && !ReferenceEquals(oldClient, _client))
        {
            Reconnected?.Invoke();
            try { await oldClient.DisposeAsync(); }
            catch { }
        }
    }

    public async Task<bool> IsHealthyAsync(TimeSpan? timeout = null)
    {
        if (_client is null || _client.State != ConnectionState.Connected)
            return false;
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8));
            await _client.PingAsync(cancellationToken: cts.Token);
            return true;
        }
        catch { return false; }
    }

    public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        _models ??= await _client.ListModelsAsync(ct);
        return _models;
    }

    public async Task<string?> GetFastestModelIdAsync(CancellationToken ct = default)
    {
        if (_fastestModelId is not null) return _fastestModelId;
        try
        {
            var models = await GetModelsAsync(ct);
            _fastestModelId = models
                .Where(m => m.Billing is not null)
                .OrderBy(m => m.Billing!.Multiplier)
                .FirstOrDefault()?.Id;

            _fastestModelId ??= models
                .FirstOrDefault(m =>
                    m.Id.Contains("mini", StringComparison.OrdinalIgnoreCase) ||
                    m.Id.Contains("flash", StringComparison.OrdinalIgnoreCase) ||
                    m.Id.Contains("haiku", StringComparison.OrdinalIgnoreCase))?.Id;

            _fastestModelId ??= models.FirstOrDefault()?.Id;
        }
        catch { }
        return _fastestModelId;
    }

    public async Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.CreateSessionAsync(config, ct);
    }

    public async Task<CopilotSession> ResumeSessionAsync(
        string sessionId, ResumeSessionConfig config, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.ResumeSessionAsync(sessionId, config, ct);
    }

    public async Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.GetAuthStatusAsync(ct);
    }

    public async Task<CopilotSignInResult> SignInAsync(
        Action<string, string>? onDeviceCode = null,
        CancellationToken ct = default)
    {
        var cliPath = FindCliPath();
        if (cliPath is null) return CopilotSignInResult.CliNotFound;

        if (onDeviceCode is null)
        {
            var legacyPsi = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "login",
                UseShellExecute = true,
            };
            using var legacyProcess = Process.Start(legacyPsi);
            if (legacyProcess is null) return CopilotSignInResult.Failed;
            await legacyProcess.WaitForExitAsync(ct);
            if (legacyProcess.ExitCode != 0) return CopilotSignInResult.Failed;
            await ConnectAsync(ct);
            return CopilotSignInResult.Success;
        }

        // Capture stdout/stderr to extract device code
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = "login",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return CopilotSignInResult.Failed;

        string? deviceCode = null;
        string? verificationUrl = null;
        int notified = 0;
        var parseLock = new object();

        void ProcessLine(string line)
        {
            lock (parseLock)
            {
                ParseDeviceCodeLine(line, ref deviceCode, ref verificationUrl);

                if (deviceCode is not null && verificationUrl is not null
                    && Interlocked.CompareExchange(ref notified, 1, 0) == 0)
                {
                    var code = deviceCode;
                    var url = verificationUrl;
                    onDeviceCode(code, url);
                    OpenBrowser(url);
                }
            }
        }

        // Read both stdout and stderr — CLI may write to either
        var outputTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
                ProcessLine(line);
        }, ct);

        var errorTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(ct)) is not null)
                ProcessLine(line);
        }, ct);

        // Send Enter to proceed past "Press Enter to open..." prompt
        try
        {
            await Task.Delay(500, ct);
            await process.StandardInput.WriteLineAsync();
        }
        catch { /* best-effort */ }

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0) return CopilotSignInResult.Failed;

        await ConnectAsync(ct);
        return CopilotSignInResult.Success;
    }

    public async Task<bool> SignOutAsync(CancellationToken ct = default)
    {
        var cliPath = FindCliPath();
        if (cliPath is null) return false;

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = "logout",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return false;
        await process.WaitForExitAsync(ct);
        await ForceReconnectAsync(ct);
        return true;
    }

    public string? GetStoredLogin()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".copilot", "config.json");
            if (!File.Exists(configPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("last_logged_in_user", out var lastUser)
                && lastUser.TryGetProperty("login", out var login))
                return login.GetString();
        }
        catch { }
        return null;
    }

    private static void ParseDeviceCodeLine(string line, ref string? deviceCode, ref string? verificationUrl)
    {
        if (deviceCode is null)
        {
            var codeMatch = System.Text.RegularExpressions.Regex.Match(
                line, @"\b([A-Z0-9]{4,}-[A-Z0-9]{4,})\b");
            if (codeMatch.Success)
                deviceCode = codeMatch.Groups[1].Value;
        }

        if (verificationUrl is null)
        {
            var urlMatch = System.Text.RegularExpressions.Regex.Match(
                line, @"(https?://\S+)");
            if (urlMatch.Success)
                verificationUrl = urlMatch.Groups[1].Value;
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    public async Task<GitHub.Copilot.SDK.Rpc.AccountGetQuotaResult?> GetAccountQuotaAsync(CancellationToken ct = default)
    {
        if (_client is null) return null;
        return await _client.Rpc.Account.GetQuotaAsync(ct);
    }

    /// <summary>Creates, uses, and auto-disposes a lightweight session for one-shot LLM calls.</summary>
    public async Task<TResult> UseLightweightSessionAsync<TResult>(
        LightweightSessionOptions options,
        Func<CopilotSession, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(SessionConfigBuilder.BuildLightweight(options), ct).ConfigureAwait(false);
        try
        {
            return await operation(session, ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeAndDeleteSessionAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes a session to free resources.</summary>
    public async Task DisposeAndDeleteSessionAsync(CopilotSession? session)
    {
        if (session is null) return;
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort cleanup */ }
    }

    private void SubscribeToCliProcessExit(CopilotClient client)
    {
        var gen = ConnectionGeneration;
        var fired = 0;
        void FireOnce()
        {
            if (Interlocked.CompareExchange(ref fired, 1, 0) != 0) return;
            _ = AutoReconnectAndNotifyAsync(gen);
        }

        // Fall back to polling — simplest approach for now
        StartStatePollingFallback(client, gen, FireOnce);
    }

    private void StartStatePollingFallback(CopilotClient client, long gen, Action fireOnce)
    {
        var pollCts = new CancellationTokenSource();
        _cleanupProcessHandlers = () => pollCts.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                while (!pollCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(3000, pollCts.Token);
                    if (gen != ConnectionGeneration) return;
                    if (client.State is ConnectionState.Disconnected or ConnectionState.Error)
                    {
                        fireOnce();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task AutoReconnectAndNotifyAsync(long exitedGeneration)
    {
        try { await ForceReconnectAsync(); }
        catch { }
        CliProcessExited?.Invoke(exitedGeneration);
    }

    private static string? FindCliPath()
    {
        var binary = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";
        var appDir = AppContext.BaseDirectory;

        // Check runtimes/{rid}/native/ (standard SDK output location)
        var rid = RuntimeInformation.RuntimeIdentifier;
        var runtimePath = Path.Combine(appDir, "runtimes", rid, "native", binary);
        if (File.Exists(runtimePath)) return runtimePath;

        // Fallback: try portable rid
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var portablePath = Path.Combine(appDir, "runtimes", $"{os}-{arch}", "native", binary);
        if (File.Exists(portablePath)) return portablePath;

        // Fallback: check app directory directly
        var directPath = Path.Combine(appDir, binary);
        if (File.Exists(directPath)) return directPath;

        return null;
    }

    private static void ConfigureAuthentication(CopilotClientOptions options)
    {
        var token = TryReadStoredGitHubToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            options.GitHubToken = token;
            options.UseLoggedInUser = false;
            return;
        }

        options.UseLoggedInUser = true;
    }

    private static string? TryReadStoredGitHubToken()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".copilot", "config.json");
            if (!File.Exists(configPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!doc.RootElement.TryGetProperty("last_logged_in_user", out var lastUser)
                || lastUser.ValueKind != JsonValueKind.Object)
                return null;

            var login = lastUser.TryGetProperty("login", out var l) ? l.GetString() : null;
            var host = lastUser.TryGetProperty("host", out var h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(host))
                return null;

            // Try to read token from Windows Credential Manager
            // This is a simplified version — full implementation would use Win32 API
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cleanupProcessHandlers?.Invoke();
        _cleanupProcessHandlers = null;
        _lifecycleSub?.Dispose();
        _lifecycleSub = null;

        if (_client is not null)
        {
            try { await _client.DisposeAsync(); }
            catch { }
            _client = null;
        }
    }
}
