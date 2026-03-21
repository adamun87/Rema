using System;
using System.Threading;

namespace Lumi.ViewModels;

internal sealed class DisposableGroup(params IDisposable[] disposables) : IDisposable
{
    private readonly IDisposable[] _disposables = disposables;
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var disposable in _disposables)
            disposable.Dispose();
    }
}

internal sealed class ActionDisposable(Action action) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        action();
    }
}
