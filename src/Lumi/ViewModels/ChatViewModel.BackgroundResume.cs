namespace Lumi.ViewModels;

/// <summary>
/// Background task handling is fully automatic via the Copilot SDK:
/// - <c>SessionIdleEvent.Data.BackgroundTasks</c> reports pending shells/agents
/// - When pending, <c>HasPendingBackgroundWork</c> is set on the runtime state
/// - <c>IsChatRuntimeActive</c> prevents session cleanup while background work is pending
/// - The SDK auto-triggers follow-up turns when background tasks complete
/// - No custom tools or tracking needed
/// </summary>
public partial class ChatViewModel;
