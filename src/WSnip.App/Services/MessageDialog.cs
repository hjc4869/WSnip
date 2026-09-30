using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace WSnip.App.Services;

/// <summary>A minimal informational dialog.</summary>
public static class MessageDialog
{
    public static void Show(Window? owner, string title, string message)
    {
        var ok = new Button { Content = "OK", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    ok,
                },
            },
        };
        window.Bind(TemplatedControl.BackgroundProperty, window.GetResourceObservable("AppPageBackgroundBrush"));
        ok.Click += (_, _) => window.Close();
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
    }
}
