using Avalonia;

namespace FocusFlow.App.Services;

/// <summary>
/// FR-106. Reports where the mouse pointer is, in Avalonia's screen-pixel space.
/// </summary>
/// <remarks>
/// Avalonia has no cross-platform global cursor API — pointer position is only available
/// inside a pointer event, and restoring from the tray isn't one. Each platform therefore
/// supplies it natively.
/// </remarks>
public interface IPointerLocator
{
    /// <summary>False when the platform cannot report it; callers fall back to the primary screen.</summary>
    bool TryGetPosition(out PixelPoint position);
}
