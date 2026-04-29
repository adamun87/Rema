using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rema.Services;

/// <summary>
/// Lightweight browser automation via Chrome DevTools Protocol.
/// Connects to user's existing Chrome/Edge browser with remote debugging enabled.
/// If no debuggable browser is found, launches one.
/// </summary>
public sealed class BrowserAutomationService : IDisposable
{
    private const int DebugPort = 9222;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private Process? _browserProcess;
    private string? _targetId;
    private int _wsMessageId;

    public async Task<string> NavigateAsync(string url)
    {
        await EnsureBrowserAsync();
        var targetId = await GetOrCreateTargetAsync();

        var result = await SendCdpCommandAsync(targetId, "Page.navigate", new { url });
        return JsonSerializer.Serialize(new { navigated = true, url, targetId });
    }

    public async Task<string> GetPageContentAsync()
    {
        await EnsureBrowserAsync();
        var targetId = await GetOrCreateTargetAsync();

        var result = await SendCdpCommandAsync(targetId, "Runtime.evaluate",
            new { expression = "document.body?.innerText?.substring(0, 8000) || ''" });

        var text = ExtractResultValue(result);
        return JsonSerializer.Serialize(new { content = text, truncated = (text?.Length ?? 0) >= 8000 });
    }

    public async Task<string> ClickElementAsync(string selector)
    {
        await EnsureBrowserAsync();
        var targetId = await GetOrCreateTargetAsync();

        var js = $"(() => {{ const el = document.querySelector({JsonSerializer.Serialize(selector)}); if (!el) return 'not_found'; el.click(); return 'clicked'; }})()";
        var result = await SendCdpCommandAsync(targetId, "Runtime.evaluate", new { expression = js });
        var value = ExtractResultValue(result);

        return JsonSerializer.Serialize(new { clicked = value == "clicked", selector });
    }

    public async Task<string> TypeTextAsync(string selector, string text)
    {
        await EnsureBrowserAsync();
        var targetId = await GetOrCreateTargetAsync();

        var escapedText = JsonSerializer.Serialize(text);
        var js = $"(() => {{ const el = document.querySelector({JsonSerializer.Serialize(selector)}); if (!el) return 'not_found'; el.focus(); el.value = {escapedText}; el.dispatchEvent(new Event('input', {{bubbles:true}})); return 'typed'; }})()";
        var result = await SendCdpCommandAsync(targetId, "Runtime.evaluate", new { expression = js });
        var value = ExtractResultValue(result);

        return JsonSerializer.Serialize(new { typed = value == "typed", selector });
    }

    public async Task<string> GetPageInfoAsync()
    {
        await EnsureBrowserAsync();
        var targetId = await GetOrCreateTargetAsync();

        var js = "JSON.stringify({ title: document.title, url: location.href, links: Array.from(document.querySelectorAll('a[href]')).slice(0,20).map(a => ({text: a.innerText?.trim()?.substring(0,60), href: a.href})), inputs: Array.from(document.querySelectorAll('input,textarea,select,button')).slice(0,20).map(el => ({tag: el.tagName, type: el.type, name: el.name, id: el.id, placeholder: el.placeholder, value: el.value?.substring(0,40)})) })";
        var result = await SendCdpCommandAsync(targetId, "Runtime.evaluate", new { expression = js });
        var value = ExtractResultValue(result);

        return value ?? "{}";
    }

    public async Task<string> ListTabsAsync()
    {
        await EnsureBrowserAsync();
        try
        {
            var json = await _http.GetStringAsync($"http://localhost:{DebugPort}/json");
            using var doc = JsonDocument.Parse(json);
            var tabs = doc.RootElement.EnumerateArray()
                .Where(t => t.GetProperty("type").GetString() == "page")
                .Select(t => new
                {
                    id = t.GetProperty("id").GetString(),
                    title = t.GetProperty("title").GetString(),
                    url = t.GetProperty("url").GetString(),
                })
                .ToList();
            return JsonSerializer.Serialize(tabs);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Internals ──

    private async Task EnsureBrowserAsync()
    {
        // Check if a debuggable browser is already running
        try
        {
            var response = await _http.GetStringAsync($"http://localhost:{DebugPort}/json/version");
            return; // Already running
        }
        catch { }

        // Launch Edge (or Chrome) with remote debugging
        var browserPath = FindBrowserPath();
        if (browserPath is null)
            throw new InvalidOperationException("No Chrome or Edge browser found.");

        _browserProcess = Process.Start(new ProcessStartInfo
        {
            FileName = browserPath,
            Arguments = $"--remote-debugging-port={DebugPort} --no-first-run --no-default-browser-check",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        // Wait for CDP to become available
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            try
            {
                await _http.GetStringAsync($"http://localhost:{DebugPort}/json/version");
                return;
            }
            catch { }
        }
        throw new InvalidOperationException("Browser launched but CDP endpoint not available.");
    }

    private async Task<string> GetOrCreateTargetAsync()
    {
        if (_targetId is not null) return _targetId;

        var json = await _http.GetStringAsync($"http://localhost:{DebugPort}/json");
        using var doc = JsonDocument.Parse(json);
        var page = doc.RootElement.EnumerateArray()
            .FirstOrDefault(t => t.GetProperty("type").GetString() == "page");

        if (page.ValueKind != JsonValueKind.Undefined)
        {
            _targetId = page.GetProperty("id").GetString();
            return _targetId!;
        }

        // Create new tab
        var newTab = await _http.GetStringAsync($"http://localhost:{DebugPort}/json/new");
        using var newDoc = JsonDocument.Parse(newTab);
        _targetId = newDoc.RootElement.GetProperty("id").GetString();
        return _targetId!;
    }

    private async Task<string?> SendCdpCommandAsync(string targetId, string method, object? parameters = null)
    {
        // Get WebSocket URL from targets list
        var json = await _http.GetStringAsync($"http://localhost:{DebugPort}/json");
        using var doc = JsonDocument.Parse(json);
        var target = doc.RootElement.EnumerateArray()
            .FirstOrDefault(t => t.GetProperty("id").GetString() == targetId);

        if (target.ValueKind == JsonValueKind.Undefined) return null;
        var wsUrl = target.GetProperty("webSocketDebuggerUrl").GetString();
        if (wsUrl is null) return null;

        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

        var id = ++_wsMessageId;
        var payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
        var sendBuffer = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(sendBuffer, WebSocketMessageType.Text, true, cts.Token);

        // Read response
        var recvBuffer = new byte[65536];
        var result = await ws.ReceiveAsync(recvBuffer, cts.Token);
        var response = Encoding.UTF8.GetString(recvBuffer, 0, result.Count);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        return response;
    }

    private static string? ExtractResultValue(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("result", out var inner)
                && inner.TryGetProperty("value", out var value))
            {
                return value.GetString();
            }
        }
        catch { }
        return null;
    }

    private static string? FindBrowserPath()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Google\Chrome\Application\chrome.exe"),
        };

        return paths.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        _http.Dispose();
        // Don't kill browser process — user might be using it
    }
}
