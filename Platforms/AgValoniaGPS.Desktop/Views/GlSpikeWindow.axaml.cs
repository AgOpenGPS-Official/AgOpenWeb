using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace AgValoniaGPS.Desktop.Views;

public partial class GlSpikeWindow : Window
{
    public GlSpikeWindow()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
