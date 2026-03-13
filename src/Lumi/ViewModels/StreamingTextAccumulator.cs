using System;
using System.Text;

namespace Lumi.ViewModels;

internal sealed class StreamingTextAccumulator : IDisposable
{
    private readonly StringBuilder _buffer;
    private readonly object _gate = new();
    private readonly UiThrottler _uiThrottler;

    public StreamingTextAccumulator(int initialCapacity, TimeSpan minimumInterval, Action flushAction)
    {
        _buffer = new StringBuilder(initialCapacity);
        _uiThrottler = new UiThrottler(flushAction, minimumInterval);
    }

    public void Append(string? delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;

        bool flushImmediately;
        lock (_gate)
        {
            flushImmediately = _buffer.Length == 0;
            _buffer.Append(delta);
        }

        _uiThrottler.Request(flushImmediately);
    }

    public string? SnapshotOrNull()
    {
        lock (_gate)
        {
            return _buffer.Length == 0 ? null : _buffer.ToString();
        }
    }

    public void CancelPending() => _uiThrottler.CancelPending();

    public void Clear()
    {
        lock (_gate)
            _buffer.Clear();
    }

    public void Reset()
    {
        _uiThrottler.CancelPending();
        Clear();
    }

    public void Dispose() => _uiThrottler.Dispose();
}
