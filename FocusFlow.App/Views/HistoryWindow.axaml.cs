using Avalonia.Controls;
using Avalonia.Input;

namespace FocusFlow.App.Views;

public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();

        // Hides rather than closes, matching how App.axaml.cs otherwise dismisses this
        // window (Closing is cancelled and it's hidden instead) — this is a report you
        // reopen later, not a dialog you're finished with for good.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
            }
        };
    }
}
