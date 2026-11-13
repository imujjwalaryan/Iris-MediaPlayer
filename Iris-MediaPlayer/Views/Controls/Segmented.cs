using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Iris_MediaPlayer.Views.Controls;

/// <summary>
/// A row of mutually exclusive options with a light pill that slides to the selected one
/// (333 ms, decelerate). <see cref="SegmentWidth"/> fixes each segment's width (Library tabs);
/// left at NaN the segments share the available width (player settings).
/// </summary>
public sealed partial class Segmented : UserControl
{
    private readonly Grid _segments = new();
    private readonly Border _pill;
    private readonly Border _track;
    private readonly List<Button> _buttons = [];
    private IReadOnlyList<string> _items = [];
    private int _selectedIndex = -1;

    public Segmented()
    {
        _pill = new Border
        {
            Background = (Brush)Application.Current.Resources["IrisLightBrush"],
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false
        };
        _track = new Border
        {
            Child = new Grid { Children = { _pill, _segments } }
        };
        Content = _track;
        IsTabStop = false;
        SizeChanged += (_, _) => PlacePill(animate: false);
        ApplyLook();
    }

    public event EventHandler<int>? SelectionChanged;

    /// <summary>Pill look (Library tabs) when true; the squarer sheet look when false.</summary>
    public bool IsPill
    {
        get => _isPill;
        set
        {
            _isPill = value;
            ApplyLook();
        }
    }
    private bool _isPill = true;

    public double SegmentWidth { get; set; } = double.NaN;

    public double SegmentHeight { get; set; } = 36;

    public IReadOnlyList<string> Items
    {
        get => _items;
        set
        {
            _items = value;
            Build();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => Select(value, raise: false);
    }

    private void ApplyLook()
    {
        if (_isPill)
        {
            _track.Padding = new Thickness(4);
            _track.CornerRadius = new CornerRadius(999);
            _track.Background = (Brush)Application.Current.Resources["IrisFieldBrush"];
            _track.BorderBrush = (Brush)Application.Current.Resources["IrisStrokeSubtleBrush"];
            _track.BorderThickness = new Thickness(1);
            _pill.CornerRadius = new CornerRadius(999);
        }
        else
        {
            _track.Padding = new Thickness(3);
            _track.CornerRadius = new CornerRadius(12);
            _track.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
            _track.BorderThickness = new Thickness(0);
            _pill.CornerRadius = new CornerRadius(9);
        }
        Build();
    }

    private void Build()
    {
        _segments.Children.Clear();
        _segments.ColumnDefinitions.Clear();
        _buttons.Clear();

        for (var index = 0; index < _items.Count; index++)
        {
            _segments.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = double.IsNaN(SegmentWidth) ? new GridLength(1, GridUnitType.Star) : new GridLength(SegmentWidth)
            });

            var button = new Button
            {
                Content = _items[index],
                Style = (Style)Application.Current.Resources["IrisButtonBaseStyle"],
                Height = _isPill ? SegmentHeight : 34,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = _pill.CornerRadius,
                FontSize = _isPill ? 14 : 13,
                FontWeight = _isPill ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium,
                Padding = new Thickness(6, 0, 6, 0),
                Tag = index
            };
            AutomationProperties.SetName(button, _items[index]);
            button.Click += (sender, _) => Select((int)((Button)sender).Tag, raise: true);
            Grid.SetColumn(button, index);
            _segments.Children.Add(button);
            _buttons.Add(button);
        }

        _pill.Height = _isPill ? SegmentHeight : 34;
        UpdateForegrounds();
        PlacePill(animate: false);
    }

    private void Select(int index, bool raise)
    {
        if (index < 0 || index >= _items.Count)
        {
            _selectedIndex = index;
            _pill.Visibility = Visibility.Collapsed;
            UpdateForegrounds();
            return;
        }

        var changed = index != _selectedIndex;
        _selectedIndex = index;
        _pill.Visibility = Visibility.Visible;
        UpdateForegrounds();
        PlacePill(animate: true);
        if (changed && raise)
            SelectionChanged?.Invoke(this, index);
    }

    private void UpdateForegrounds()
    {
        var selected = (Brush)Application.Current.Resources["IrisOnLightBrush"];
        var normal = _isPill
            ? (Brush)Application.Current.Resources["IrisTextSecondaryBrush"]
            : (Brush)Application.Current.Resources["IrisTextMutedBrush"];
        for (var index = 0; index < _buttons.Count; index++)
        {
            _buttons[index].Foreground = index == _selectedIndex ? selected : normal;
            AutomationProperties.SetItemStatus(_buttons[index], index == _selectedIndex ? "Selected" : string.Empty);
        }
    }

    private void PlacePill(bool animate)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _buttons.Count || _segments.ActualWidth <= 0)
            return;

        var segmentWidth = double.IsNaN(SegmentWidth) ? _segments.ActualWidth / _buttons.Count : SegmentWidth;
        _pill.Width = segmentWidth;
        var offset = new Vector3((float)(segmentWidth * _selectedIndex), 0, 0);
        if (animate)
            Motion.Translate(_pill, offset, Motion.Slow);
        else
            Motion.SetTranslation(_pill, offset);
    }
}
