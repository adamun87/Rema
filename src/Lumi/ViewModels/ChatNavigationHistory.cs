using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Lumi.ViewModels;

internal readonly record struct ChatNavigationState(Guid? ChatId, Guid? ProjectFilterId);

internal sealed class ChatNavigationHistory
{
    private readonly List<ChatNavigationState> _entries = [];
    private int _currentIndex = -1;

    public bool IsRestoring { get; private set; }

    public void Record(Guid? chatId, Guid? projectFilterId)
    {
        var entry = new ChatNavigationState(chatId, projectFilterId);
        if (HasCurrentEntry && _entries[_currentIndex] == entry)
            return;

        TruncateForwardHistory();
        _entries.Add(entry);
        _currentIndex = _entries.Count - 1;
    }

    public void RemoveChat(Guid chatId)
    {
        RemoveEntries(entry => entry.ChatId == chatId);
    }

    public async Task<bool> TryNavigateAsync(
        int direction,
        IEnumerable<Guid> existingChatIds,
        Func<ChatNavigationState, Task<bool>> applyAsync)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction));

        ArgumentNullException.ThrowIfNull(existingChatIds);
        ArgumentNullException.ThrowIfNull(applyAsync);

        PruneMissingChats(existingChatIds);

        var targetIndex = _currentIndex + direction;
        if (targetIndex < 0 || targetIndex >= _entries.Count)
            return false;

        _currentIndex = targetIndex;

        using var _ = BeginRestoreScope();
        return await applyAsync(_entries[targetIndex]);
    }

    private bool HasCurrentEntry => _currentIndex >= 0 && _currentIndex < _entries.Count;

    private void TruncateForwardHistory()
    {
        if (_currentIndex < _entries.Count - 1)
        {
            _entries.RemoveRange(
                _currentIndex + 1,
                _entries.Count - _currentIndex - 1);
        }
    }

    private void PruneMissingChats(IEnumerable<Guid> existingChatIds)
    {
        var existingIds = existingChatIds.ToHashSet();
        RemoveEntries(entry => entry.ChatId.HasValue && !existingIds.Contains(entry.ChatId.Value));
    }

    private void RemoveEntries(Func<ChatNavigationState, bool> shouldRemove)
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (!shouldRemove(_entries[i]))
                continue;

            _entries.RemoveAt(i);

            if (i < _currentIndex)
                _currentIndex--;
            else if (i == _currentIndex)
                _currentIndex = Math.Min(_currentIndex, _entries.Count - 1);
        }

        if (_entries.Count == 0)
            _currentIndex = -1;
    }

    private IDisposable BeginRestoreScope()
    {
        IsRestoring = true;
        return new RestoreScope(this);
    }

    private sealed class RestoreScope(ChatNavigationHistory owner) : IDisposable
    {
        private readonly ChatNavigationHistory _owner = owner;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner.IsRestoring = false;
        }
    }
}
