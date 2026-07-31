using Avalonia.Controls;

namespace FocusFlow.App.Views;

/// <summary>
/// A small modal-style alert. Used for things the user must actually see rather than a
/// notification they might miss — a session interrupted by sleep, or a settings write
/// that failed and needs their intervention.
/// </summary>
public partial class AlertWindow : Window
{
    public AlertWindow()
    {
        InitializeComponent();
    }

    public AlertWindow(string heading, string body, string? confirmLabel = null) : this()
    {
        HeadingText.Text = heading;
        BodyText.Text = body;

        if (confirmLabel is not null)
        {
            // Two-button form: the primary action is something the user is opting into,
            // so it needs an explicit decline alongside it.
            PrimaryButton.Content = confirmLabel;
            SecondaryButton.IsVisible = true;
            SecondaryButton.Click += (_, _) => Close(false);
        }

        PrimaryButton.Click += (_, _) => Close(confirmLabel is not null);
    }
}
