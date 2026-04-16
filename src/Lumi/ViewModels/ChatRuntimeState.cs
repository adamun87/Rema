using Lumi.Models;

namespace Lumi.ViewModels;

internal sealed class ChatRuntimeState
{
    private bool _isBusy;

    public Chat? Chat { get; init; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
                return;

            _isBusy = value;
            if (Chat is not null)
                Chat.IsRunning = value;
        }
    }

    public bool IsStreaming { get; set; }

    public string StatusText { get; set; } = string.Empty;

    public long TotalInputTokens { get; set; }

    public long TotalOutputTokens { get; set; }

    /// <summary>Latest turn's input tokens — best proxy for current context window usage.</summary>
    public long ContextCurrentTokens { get; set; }

    /// <summary>Context window token limit from SessionUsageInfoEvent.</summary>
    public long ContextTokenLimit { get; set; }

    public bool HasUsedBrowser { get; set; }

    public int ActiveToolCount { get; set; }

    public int PendingSessionUserMessageCount { get; set; }

    public int PendingAssistantMessageCount { get; set; }

    public long PendingTurnSequence { get; set; }

    public CancellationTokenSource? PostToolReconciliationCts { get; set; }

    /// <summary>True when the SDK reports pending background shells/agents.
    /// Keeps the session alive without blocking the UI.</summary>
    public bool HasPendingBackgroundWork { get; set; }

    /// <summary>True when the user explicitly clicked Stop for the current turn.
    /// Unexpected SDK aborts must not be mistaken for this state.</summary>
    public bool ManualStopRequested { get; set; }

}
