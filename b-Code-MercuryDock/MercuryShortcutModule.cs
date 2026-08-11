using HistoryVulcan.Core.Input;

namespace Mercury;

/// <summary>
/// Registers HistoryMercury-owned global shortcuts with the Vulcan module host.
/// Hotkeys only dispatch Mercury orchestration commands; window work stays on vulcan.*.
/// </summary>
public sealed class MercuryShortcutModule : IGlobalShortcutModule
{
    /// <inheritdoc />
    public void RegisterShortcuts(IGlobalShortcutRegistrar registrar)
    {
        registrar.Register(new GlobalShortcutDescriptor(
            "focus-console",
            [new GlobalShortcutStroke(0xBF), new GlobalShortcutStroke(0xBF)],
            "vulcan.app.focusconsole"));
    }
}
