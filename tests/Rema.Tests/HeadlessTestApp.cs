using Rema;

namespace Rema.Tests;

public sealed class HeadlessTestApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        // Tests create their own windows.
    }
}
