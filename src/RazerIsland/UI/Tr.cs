using System.Windows;
using System.Windows.Markup;
using RazerIsland.Core;

namespace RazerIsland.UI;

/// <summary>XAML: <c>Text="{l:Tr 'Українською', 'In English'}"</c>.</summary>
public sealed class TrExtension(string uk, string en) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(uk, en);
}

/// <summary>XAML: <c>Visibility="{l:Only en}"</c> — для блоків із посиланнями, які не перекласти одним рядком.</summary>
public sealed class OnlyExtension(string lang) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        lang == L.Code ? Visibility.Visible : Visibility.Collapsed;
}
