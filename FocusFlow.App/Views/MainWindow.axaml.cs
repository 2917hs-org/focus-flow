using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FocusFlow.App.ViewModels;

namespace FocusFlow.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Tunnel, not the plain KeyDown += bubble subscription: a focused TextBox/ComboBox
        // can mark KeyDown handled during its own bubble-phase handling (arrow keys, Enter,
        // etc.), which would stop a capture attempt from ever reaching here. Tunnelling
        // fires before any child gets that chance. handledEventsToo is cheap insurance
        // against an earlier tunnel-phase handler doing the same.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.CapturingHotkey == HotkeyCaptureTarget.None)
        {
            return;
        }

        if (IsModifierOnly(e.Key))
        {
            // Still waiting for the actual key.
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            vm.CancelHotkeyCapture();
            return;
        }

        vm.CompleteHotkeyCapture(e.Key, e.KeyModifiers);
    }

    private static bool IsModifierOnly(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin;
}
