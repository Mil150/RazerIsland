using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RazerIsland.Battery;
using RazerIsland.Core;

namespace RazerIsland.UI;

/// <summary>Рядок пристрою в розкритому віджеті + міні-елемент для смужки острівця.</summary>
public sealed class DeviceRow
{
    public Grid Root { get; }
    public StackPanel Mini { get; }

    private readonly TextBlock _icon, _name, _status, _pct, _bolt, _mIcon, _mPct, _mBolt;
    private readonly Border _fill, _mFill;
    private readonly ColumnDefinition _c1, _c2, _m1, _m2;

    public DeviceRow(RazerDevice dev)
    {
        Root = new Grid { Margin = new Thickness(0, 8, 0, 6) };
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition());
        Root.RowDefinitions.Add(new RowDefinition());

        _icon = new TextBlock
        {
            FontFamily = Glyphs.IconFont, FontSize = 22, Foreground = Palette.Brush(Palette.DeviceIconColor(dev.Brand)),
            Text = Glyphs.ForCategory(dev.Category), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(_icon, 0);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _name = new TextBlock
        {
            Text = dev.Name, Foreground = Palette.Brush("#F2F3F5"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _status = new TextBlock { Foreground = Palette.Brush("#8A8F98"), FontSize = 11 };
        texts.Children.Add(_name);
        texts.Children.Add(_status);
        Grid.SetColumn(texts, 1);

        _pct = new TextBlock { FontSize = 24, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        _bolt = Bolt(18, new Thickness(0, 0, 4, 0));
        var pctPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        pctPanel.Children.Add(_bolt);
        pctPanel.Children.Add(_pct);
        Grid.SetColumn(pctPanel, 2);

        // смужка заряду
        var track = MakeBar(6, 3, new Thickness(0, 8, 0, 0), out _fill, out _c1, out _c2);
        Grid.SetRow(track, 1);
        Grid.SetColumnSpan(track, 3);

        Root.Children.Add(_icon);
        Root.Children.Add(texts);
        Root.Children.Add(pctPanel);
        Root.Children.Add(track);

        // міні-елемент для смужки
        Mini = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0) };
        _mIcon = new TextBlock
        {
            FontFamily = Glyphs.IconFont, FontSize = 13, Foreground = Palette.Brush(Palette.DeviceIconColor(dev.Brand)),
            Text = Glyphs.ForCategory(dev.Category), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };
        _mPct = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        var mTrack = MakeBar(4, 2, new Thickness(6, 1, 0, 0), out _mFill, out _m1, out _m2);
        mTrack.Width = 26;
        mTrack.VerticalAlignment = VerticalAlignment.Center;
        _mBolt = Bolt(11, new Thickness(0, 0, 2, 0));
        Mini.Children.Add(_mIcon);
        Mini.Children.Add(_mBolt);
        Mini.Children.Add(_mPct);
        Mini.Children.Add(mTrack);
    }

    // блискавка «заряджається / на кабелі»
    private static TextBlock Bolt(double size, Thickness margin) => new()
    {
        FontFamily = Glyphs.IconFont, FontSize = size, Text = Glyphs.Charging, Foreground = Palette.Brush(Palette.Green),
        VerticalAlignment = VerticalAlignment.Center, Margin = margin, Visibility = Visibility.Collapsed,
    };

    private static Border MakeBar(double height, double radius, Thickness margin, out Border fill, out ColumnDefinition c1, out ColumnDefinition c2)
    {
        var track = new Border
        {
            Height = height, CornerRadius = new CornerRadius(radius), Background = Palette.Brush("#24FFFFFF"), Margin = margin,
        };
        var grid = new Grid();
        c1 = new ColumnDefinition();
        c2 = new ColumnDefinition();
        grid.ColumnDefinitions.Add(c1);
        grid.ColumnDefinitions.Add(c2);
        fill = new Border { CornerRadius = new CornerRadius(radius) };
        grid.Children.Add(fill);
        track.Child = grid;
        return track;
    }

    public void Update(RazerDevice dev, bool present)
    {
        var state = present ? dev.State : PowerState.Off;
        var color = Palette.Brush(Palette.LevelColor(dev.Level, state));
        var level = new GridLength(dev.Level, GridUnitType.Star);
        var rest = new GridLength(100 - dev.Level, GridUnitType.Star);
        double opacity = state == PowerState.Off ? 0.45 : 1.0;

        _name.Text = dev.Name;
        _pct.Text = $"{dev.Level}%";
        _pct.Foreground = color;
        _fill.Background = color;
        _c1.Width = level;
        _c2.Width = rest;
        _icon.Opacity = opacity;

        _mPct.Text = $"{dev.Level}%";
        _mPct.Foreground = color;
        _mFill.Background = color;
        _m1.Width = level;
        _m2.Width = rest;
        _mIcon.Opacity = opacity;
        _bolt.Visibility = _mBolt.Visibility = state == PowerState.Charging ? Visibility.Visible : Visibility.Collapsed;

        _status.Text = state switch
        {
            PowerState.Charging => L.T("Заряджається", "Charging"),
            PowerState.Off => L.T("Вимкнено / сон · останнє значення", "Off / asleep · last known value"),
            _ => dev.Level <= 20 ? L.T("Низький заряд — підзаряди", "Low battery — charge it") : L.T("Працює від батареї", "On battery"),
        };
    }
}
