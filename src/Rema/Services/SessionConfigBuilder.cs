using System.Collections.Generic;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace Rema.Services;

public sealed class LightweightSessionOptions
{
    public required string SystemPrompt { get; init; }
    public string? Model { get; init; }
    public bool Streaming { get; init; }
    public List<AIFunction>? Tools { get; init; }
}

public static class SessionConfigBuilder
{
    private const string ClientName = "rema";

    public static SessionConfig Build(
        string? systemPrompt,
        string? model,
        string? reasoningEffort,
        List<AIFunction>? tools,
        Dictionary<string, object>? mcpServers,
        PermissionRequestHandler? onPermission,
        SessionHooks? hooks)
    {
        var config = new SessionConfig
        {
            ClientName = ClientName,
            Model = model,
            Streaming = true,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            OnPermissionRequest = onPermission ?? PermissionHandler.ApproveAll,
        };

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Append
            };
        }

        if (!string.IsNullOrEmpty(reasoningEffort))
            config.ReasoningEffort = reasoningEffort;

        if (tools is { Count: > 0 })
            config.Tools = tools;

        if (mcpServers is { Count: > 0 })
            config.McpServers = mcpServers;

        if (hooks is not null)
            config.Hooks = hooks;

        return config;
    }

    public static ResumeSessionConfig BuildForResume(
        string? systemPrompt,
        string? model,
        string? reasoningEffort,
        List<AIFunction>? tools,
        Dictionary<string, object>? mcpServers,
        PermissionRequestHandler? onPermission,
        SessionHooks? hooks)
    {
        var config = new ResumeSessionConfig
        {
            ClientName = ClientName,
            Model = model,
            Streaming = true,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            OnPermissionRequest = onPermission ?? PermissionHandler.ApproveAll,
        };

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Append
            };
        }

        if (!string.IsNullOrEmpty(reasoningEffort))
            config.ReasoningEffort = reasoningEffort;

        if (tools is { Count: > 0 })
            config.Tools = tools;

        if (mcpServers is { Count: > 0 })
            config.McpServers = mcpServers;

        if (hooks is not null)
            config.Hooks = hooks;

        return config;
    }

    public static SessionConfig BuildLightweight(LightweightSessionOptions options)
    {
        var config = new SessionConfig
        {
            ClientName = ClientName,
            Model = options.Model,
            Streaming = options.Streaming,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Content = options.SystemPrompt,
                Mode = SystemMessageMode.Replace
            }
        };

        if (options.Tools is { Count: > 0 })
            config.Tools = options.Tools;

        return config;
    }
}
