using WinState.App;
using WinState.Terminal;

namespace WinState.Cli;

/// <summary>Добавляет сигнатуру System Control 0.8 поверх совместимого Forge frontend.</summary>
internal sealed class WinState08Shell
{
    private readonly CyberForgeShell _inner;

    public WinState08Shell(WinStateApplication application)
    {
        _inner = new CyberForgeShell(application);
    }

    public async Task<int> RunAsync(bool demoMode, CancellationToken cancellationToken)
    {
        var result = await _inner.RunAsync(demoMode, cancellationToken);
        if (demoMode)
        {
            Console.WriteLine("windows.system    registry / services / startup / scheduled tasks    rollback=full");
        }

        return result;
    }
}