using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;

namespace Iris_MediaPlayer.Views;

/// <summary>
/// The right-click menu for a video, shared by Home, Library and the player: play, add to a
/// collection (or a new one), show in File Explorer, details, and delete (to the Recycle Bin,
/// after asking). Built fresh for each card, because one MenuFlyout shared by many
/// template items can open empty in WinUI.
/// </summary>
public sealed class VideoMenu
{
    private readonly LibraryStore _store;
    private readonly FrameworkElement _host;

    /// <param name="host">Any element in the window; dialogs open on its XamlRoot.</param>
    public VideoMenu(LibraryStore store, FrameworkElement host)
    {
        _store = store;
        _host = host;
    }

    public event EventHandler<PlayRequest>? PlayRequested;

    /// <summary>The collection being browsed, if any; adds "Remove from this collection".</summary>
    public MediaCollection? CurrentCollection { get; set; }

    /// <summary>Opens the menu for <paramref name="media"/> at the pointer (right-click) or under the element (keyboard).</summary>
    public void Show(FrameworkElement target, MediaItem media, ContextRequestedEventArgs args)
    {
        var options = new FlyoutShowOptions();
        if (args.TryGetPosition(target, out var point))
            options.Position = point;
        Build(media).ShowAt(target, options);
        args.Handled = true;
    }

    public MenuFlyout Build(MediaItem media)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Play", "", () => PlayRequested?.Invoke(this, new PlayRequest(media))));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateCollectionMenu(media));
        if (CurrentCollection is { } collection && _store.IsInCollection(collection, media))
            menu.Items.Add(Item($"Remove from “{collection.Name}”", "", () => _store.RemoveFromCollection(collection, media)));
        menu.Items.Add(Item("Show in File Explorer", "", () => _ = ShowInExplorerAsync(media)));
        menu.Items.Add(CreateDetailsItem(media));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Delete…", "", () => _ = DeleteAsync(media)));
        return menu;
    }

    /// <summary>"Add to collection ›" with every collection (ticked where the video already is) and "New collection…".</summary>
    public MenuFlyoutSubItem CreateCollectionMenu(MediaItem media)
    {
        var submenu = new MenuFlyoutSubItem { Text = "Add to collection", Icon = new FontIcon { Glyph = "" } };
        foreach (var collection in _store.Collections)
        {
            var target = collection;
            var item = new ToggleMenuFlyoutItem { Text = collection.Name, IsChecked = _store.IsInCollection(collection, media) };
            item.Click += (_, _) =>
            {
                if (item.IsChecked)
                    _store.AddToCollection(target, media);
                else
                    _store.RemoveFromCollection(target, media);
            };
            submenu.Items.Add(item);
        }

        if (_store.Collections.Count > 0)
            submenu.Items.Add(new MenuFlyoutSeparator());
        submenu.Items.Add(Item("New collection…", "", () => _ = AddToNewCollectionAsync(media)));
        return submenu;
    }

    public MenuFlyoutItem CreateDetailsItem(MediaItem media) => Item("Details", "", () => _ = ShowDetailsAsync(media));

    // ---------- Actions ----------

    private async Task AddToNewCollectionAsync(MediaItem media)
    {
        var nameBox = new TextBox { PlaceholderText = "Collection name" };
        var dialog = CreateDialog("New collection", nameBox);
        dialog.PrimaryButtonText = "Create";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.Opened += (_, _) => nameBox.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
            return;

        var collection = new MediaCollection { Name = nameBox.Text.Trim() };
        _store.Collections.Add(collection);
        _store.AddToCollection(collection, media);
    }

    private static async Task ShowInExplorerAsync(MediaItem media)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(media.FilePath);
            var folder = await file.GetParentAsync();
            var options = new FolderLauncherOptions();
            options.ItemsToSelect.Add(file);
            if (folder is not null)
                await Launcher.LaunchFolderAsync(folder, options);
            else
                await Launcher.LaunchFolderPathAsync(media.FolderPath);
        }
        catch (Exception)
        {
            await Launcher.LaunchFolderPathAsync(media.FolderPath);
        }
    }

    /// <summary>Asks first, then moves the file to the Recycle Bin.</summary>
    private async Task DeleteAsync(MediaItem media)
    {
        var message = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 460,
            Children =
            {
                new TextBlock { Text = media.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = "The file moves to the Recycle Bin and is removed from your library, collections and Continue watching. "
                           + "You can restore it from the Recycle Bin.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["IrisTextSecondaryBrush"]
                }
            }
        };
        var dialog = CreateDialog("Delete this video?", message);
        dialog.PrimaryButtonText = "Delete";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            await _store.DeleteMediaAsync(media);
        }
        catch (Exception exception)
        {
            var error = CreateDialog("Couldn’t delete the video",
                new TextBlock { Text = $"Windows reported: {exception.Message}", TextWrapping = TextWrapping.Wrap });
            error.CloseButtonText = "OK";
            await error.ShowAsync();
        }
    }

    /// <summary>A medium dialog with everything Iris can read about the file: location, container, codecs, bitrates, tracks.</summary>
    public async Task ShowDetailsAsync(MediaItem media)
    {
        var body = new StackPanel { Spacing = 22 };
        var loading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                new ProgressRing { IsActive = true, Width = 20, Height = 20 },
                new TextBlock { Text = "Reading the file…", VerticalAlignment = VerticalAlignment.Center }
            }
        };
        body.Children.Add(loading);

        var dialog = CreateDialog("Details", new ScrollViewer
        {
            Content = body,
            MaxHeight = 560,
            Padding = new Thickness(0, 0, 16, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        dialog.PrimaryButtonText = "Show in File Explorer";
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.PrimaryButtonClick += (_, _) => _ = ShowInExplorerAsync(media);

        var showing = dialog.ShowAsync();
        try
        {
            var sections = await MediaProbe.ReadAsync(media.FilePath);
            body.Children.Clear();
            foreach (var section in sections)
                body.Children.Add(CreateSection(section));
        }
        catch (Exception exception)
        {
            body.Children.Clear();
            body.Children.Add(new TextBlock { Text = $"Iris couldn’t read this file: {exception.Message}", TextWrapping = TextWrapping.Wrap });
            body.Children.Add(CreateSection(new DetailSection("File", [new("Location", media.FilePath)])));
        }
        await showing;
    }

    private static FrameworkElement CreateSection(DetailSection section)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 600 };
        panel.Children.Add(new TextBlock
        {
            Text = section.Title.ToUpperInvariant(),
            Style = (Style)Application.Current.Resources["IrisLabelTextStyle"]
        });

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < section.Rows.Count; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = section.Rows[index].Label,
                Foreground = (Brush)Application.Current.Resources["IrisTextSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            };
            var value = new TextBlock
            {
                Text = section.Rows[index].Value,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontWeight = FontWeights.Normal
            };
            Grid.SetRow(label, index);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        panel.Children.Add(grid);
        return panel;
    }

    private ContentDialog CreateDialog(string title, object content) => new()
    {
        Title = title,
        Content = content,
        XamlRoot = _host.XamlRoot,
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        RequestedTheme = ElementTheme.Dark
    };

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }
}
