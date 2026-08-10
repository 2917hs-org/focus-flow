using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Interfaces;

/// <summary>A fully-resolved hotkey combination, ready to hand to the OS.</summary>
public readonly record struct HotkeyCombo(HotkeyModifiers Modifiers, string Key);

/// <summary>
/// Per-action outcome of the most recent <see cref="IGlobalHotkeys.Apply"/> call. A false
/// entry usually means the combination is already claimed by another app.
/// </summary>
public sealed record HotkeyApplyResult(bool StartPauseOk, bool StopOk, bool SkipOk)
{
    public bool AllOk => StartPauseOk && StopOk && SkipOk;
}

/// <summary>
/// Fixed, OS-registered keyboard shortcuts that reach FocusFlow's session controls even
/// when the app isn't focused. See the README for the default combinations; the actual
/// combination per action is user-configurable and lives in TimerConfig.
/// </summary>
/// <remarks>
/// Deliberately knows nothing about TimerConfig, HotkeyBinding, or platform defaults — it
/// is told exactly which combinations to register and reports what happened, the same
/// division of responsibility as IAppBlockingMonitor.Intervene not knowing why a given app
/// is blocked. Resolving a HotkeyBinding (which may mean "use the platform default" or
/// "disabled") down to the HotkeyCombo? this interface expects is the caller's job — see
/// HotkeyDefaults in the App project.
/// </remarks>
public interface IGlobalHotkeys : IDisposable
{
    /// <summary>The start/pause/resume combination was pressed.</summary>
    event EventHandler? StartPauseRequested;

    /// <summary>The stop combination was pressed.</summary>
    event EventHandler? StopRequested;

    /// <summary>The skip combination was pressed.</summary>
    event EventHandler? SkipRequested;

    /// <summary>
    /// Unregisters whatever combinations are currently active and registers the three
    /// supplied combinations in their place; a null slot means "no hotkey for that action."
    /// Safe to call repeatedly — once at startup and again every time the user changes a
    /// binding in Settings.
    /// </summary>
    HotkeyApplyResult Apply(HotkeyCombo? startPause, HotkeyCombo? stop, HotkeyCombo? skip);
}
