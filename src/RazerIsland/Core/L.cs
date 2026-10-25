using System.Globalization;

namespace RazerIsland.Core;

/// <summary>Мова інтерфейсу (uk/en). Задається на старті; зміна застосовується перезапуском.</summary>
public static class L
{
    public static bool En { get; private set; }
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("uk-UA");
    public static string Code => En ? "en" : "uk";

    public static void Init(string lang)
    {
        En = lang == "en";
        Culture = CultureInfo.GetCultureInfo(En ? "en-GB" : "uk-UA");
    }

    public static string T(string uk, string en) => En ? en : uk;

    /// <summary>Мова за замовчуванням: українська для україно- й російськомовної Windows, інакше англійська.</summary>
    public static string SystemDefault() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "uk" or "ru" ? "uk" : "en";
}
