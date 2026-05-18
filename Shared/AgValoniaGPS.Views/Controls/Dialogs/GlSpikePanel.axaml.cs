using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AgValoniaGPS.Views.Controls.Dialogs;

public partial class GlSpikePanel : UserControl
{
    public GlSpikePanel() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
