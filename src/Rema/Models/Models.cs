using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Rema.Models;

// ── Chat (reused from Lumi pattern) ──

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string? Author { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public string? ParentToolCallId { get; set; }
    public string? ToolStatus { get; set; }
    public string? ToolOutput { get; set; }
    public bool IsStreaming { get; set; }
    public string? Model { get; set; }
    public List<string> Attachments { get; set; } = [];
}

public class Chat : INotifyPropertyChanged
{
    private bool _isRunning;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New Chat";
    public Guid? ServiceProjectId { get; set; }
    public string? CopilotSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public List<ChatMessage> Messages { get; set; } = [];
    public string? LastModelUsed { get; set; }
    public string? LastReasoningEffortUsed { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning == value) return; _isRunning = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// ── Service Projects ──

public class ServiceProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string AdoOrgUrl { get; set; } = "";
    public string AdoProjectName { get; set; } = "";
    public string? KustoCluster { get; set; }
    public string? KustoDatabase { get; set; }
    public string? DiscoveredAgentPath { get; set; }
    public string? Instructions { get; set; }
    public List<PipelineConfig> PipelineConfigs { get; set; } = [];
    public List<HealthQuery> HealthQueries { get; set; } = [];
    public McpServerConfig? McpServer { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public class PipelineConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ServiceProjectId { get; set; }
    public int AdoPipelineId { get; set; }
    public string PipelineType { get; set; } = "yaml"; // "yaml" or "classic"
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string AdoUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> DeploymentStages { get; set; } = [];
    public Dictionary<string, bool> ApprovalRequired { get; set; } = [];
    public bool HealthCheckEnabled { get; set; }
}

public class McpServerConfig
{
    public string Name { get; set; } = "";
    public string ServerType { get; set; } = "local";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = [];
    public bool IsEnabled { get; set; } = true;
}

// ── Shifts ──

public class Shift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public string Notes { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class TrackedItem : INotifyPropertyChanged
{
    private string _status = "Waiting";
    private bool _requiresAction;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ShiftId { get; set; }
    public Guid ServiceProjectId { get; set; }
    public Guid PipelineConfigId { get; set; }
    public int? AdoRunId { get; set; }
    public int? AdoReleaseId { get; set; }

    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }

    public string? CurrentStage { get; set; }
    public List<StageCompletion> CompletedStages { get; set; } = [];
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastPolledAt { get; set; }
    public DateTimeOffset? LastStatusChange { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset? EtaCompletion { get; set; }

    public bool RequiresAction
    {
        get => _requiresAction;
        set { if (_requiresAction == value) return; _requiresAction = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequiresAction))); }
    }

    public string? ActionReason { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class StageCompletion
{
    public string StageName { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public class ShiftEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ShiftId { get; set; }
    public Guid? TrackedItemId { get; set; }
    public string EventType { get; set; } = "Note"; // StatusChange, Alert, UserAction, Note
    public string Message { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Severity { get; set; } = "Info"; // Info, Warning, Critical
}

// ── Connections ──

public class AdoConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrgUrl { get; set; } = "";
    public string? TenantId { get; set; }
    public bool IsDefault { get; set; }
}

public class KustoConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ClusterUrl { get; set; } = "";
    public string? Database { get; set; }
    public string? TenantId { get; set; }
}

// ── Health ──

public class HealthQuery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ServiceProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public string ThresholdType { get; set; } = "GreaterThan"; // GreaterThan, LessThan, Equals
    public double ThresholdValue { get; set; }
    public string Severity { get; set; } = "Warning";
}

// ── Scripts ──

public class ScriptTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ScriptType { get; set; } = "PowerShell"; // PowerShell, Python
    public string Content { get; set; } = "";
    public List<string> Parameters { get; set; } = [];
    public bool IsBuiltIn { get; set; }
}

// ── Memories ──

public class Memory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "General";
    public string Source { get; set; } = "chat";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

// ── Settings ──

public class RemaSettings
{
    // ── General ──
    public string? UserName { get; set; }
    public bool IsOnboarded { get; set; }
    public string Language { get; set; } = "en";

    // ── Appearance ──
    public bool IsDarkTheme { get; set; } = true;
    public bool IsCompactDensity { get; set; }
    public int FontSize { get; set; } = 14;

    // ── Chat ──
    public bool SendWithEnter { get; set; } = true;
    public bool ShowToolCalls { get; set; } = true;

    // ── AI & Models ──
    public string PreferredModel { get; set; } = "claude-sonnet-4";
    public string ReasoningEffort { get; set; } = "medium";

    // ── Polling ──
    public int PollingIntervalSeconds { get; set; } = 60;
    public bool IsPollingEnabled { get; set; } = true;

    // ── Notifications ──
    public bool NotificationsEnabled { get; set; } = true;

    // ── Window ──
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool IsMaximized { get; set; }
}

// ── Root Container ──

public class RemaAppData
{
    public RemaSettings Settings { get; set; } = new();
    public List<Chat> Chats { get; set; } = [];
    public List<ServiceProject> ServiceProjects { get; set; } = [];
    public List<Shift> Shifts { get; set; } = [];
    public List<TrackedItem> TrackedItems { get; set; } = [];
    public List<ShiftEvent> ShiftEvents { get; set; } = [];
    public List<AdoConnection> AdoConnections { get; set; } = [];
    public List<KustoConnection> KustoConnections { get; set; } = [];
    public List<ScriptTemplate> ScriptTemplates { get; set; } = [];
    public List<Memory> Memories { get; set; } = [];
}
