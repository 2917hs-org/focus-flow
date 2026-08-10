namespace FocusFlow.App.ViewModels;

/// <summary>Which hotkey row, if any, is currently listening for a keypress. UI-transient only.</summary>
public enum HotkeyCaptureTarget
{
    None,
    StartPause,
    Stop,
    Skip
}
