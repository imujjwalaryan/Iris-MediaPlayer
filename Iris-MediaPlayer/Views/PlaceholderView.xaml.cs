using Microsoft.UI.Xaml.Controls;

namespace Iris_MediaPlayer.Views;

/// <summary>Stand-in page for sections that are not built yet (Settings, Jellyfin).</summary>
public sealed partial class PlaceholderView : UserControl
{
    public PlaceholderView()
    {
        InitializeComponent();
    }

    public void SetContent(string glyph, string title, string description)
        => Message.Show(glyph, title, description);
}
