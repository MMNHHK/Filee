using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>
/// The distribution's trial-over notice: <paramref name="message"/> comes from distribution.json. Closing the
/// window ends Filee - the app runs with ShutdownMode.OnExplicitShutdown, so the window alone would not.
/// </summary>
public partial class TrialExpiredWindow : Window
{
    public TrialExpiredWindow(string message)
    {
        InitializeComponent();
        Motion.Track(this);
        Message.Text = message;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
