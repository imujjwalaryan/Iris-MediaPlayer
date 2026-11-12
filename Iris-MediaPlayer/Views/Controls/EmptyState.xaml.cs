using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace Iris_MediaPlayer.Views.Controls;

/// <summary>Consistent empty / placeholder message with an icon and optional buttons.</summary>
public sealed partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
    }

    public event EventHandler? ActionClick;
    public event EventHandler? SecondaryActionClick;

    public string Glyph
    {
        get => IconGlyph.Glyph;
        set => IconGlyph.Glyph = value;
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Description
    {
        get => DescriptionText.Text;
        set => DescriptionText.Text = value;
    }

    /// <summary>Text of the accent button; empty hides it.</summary>
    public string ActionText
    {
        get => ActionButton.Content as string ?? string.Empty;
        set
        {
            ActionButton.Content = value;
            ActionButton.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Text of the secondary button; empty hides it.</summary>
    public string SecondaryActionText
    {
        get => SecondaryActionButton.Content as string ?? string.Empty;
        set
        {
            SecondaryActionButton.Content = value;
            SecondaryActionButton.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Sets everything at once, hiding buttons whose text is null.</summary>
    public void Show(string glyph, string title, string description, string? actionText = null, string? secondaryActionText = null)
    {
        Glyph = glyph;
        Title = title;
        Description = description;
        ActionText = actionText ?? string.Empty;
        SecondaryActionText = secondaryActionText ?? string.Empty;
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e) => ActionClick?.Invoke(this, EventArgs.Empty);

    private void SecondaryActionButton_Click(object sender, RoutedEventArgs e) => SecondaryActionClick?.Invoke(this, EventArgs.Empty);
}
