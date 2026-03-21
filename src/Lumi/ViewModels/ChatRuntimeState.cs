using System.Threading;
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

    /// <summary>True while a background task auto-resume is pending (debounce timer active).</summary>
    public bool HasPendingAutoResume { get; set; }

    /// <summary>True when the last completed turn was auto-triggered by background task completion.</summary>
    public bool LastTurnWasAutoResume { get; set; }

}
