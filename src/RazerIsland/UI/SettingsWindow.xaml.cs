using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using RazerIsland.Calendar;
using RazerIsland.Core;

namespace RazerIsland.UI;

/// <summary>Вікно налаштувань і майстер першого запуску.</summary>
public partial class SettingsWindow : Window
{
    private static string[] PageTitles => L.En
        ? ["Welcome", "Battery", "Appearance", "Weather", "Calendar", "Discord microphone", "System", "Done"]
        : ["Вітання", "Батарея", "Вигляд", "Погода", "Календар", "Мікрофон Discord", "Система", "Готово"];
    private const int LastPage = 7;

    private readonly App _app;
    private readonly StackPanel[] _pages;
    private readonly Button[] _nav;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _wizard;
    private int _page;
    private bool _loading;

    private Settings S => _app.Settings;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        _pages = [P0, P1, P2, P3, P4, P5, P6, P7];
        _nav = [N1, N2, N3, N4, N5, N6];
        VersionText.Text = L.T("версія ", "version ") + AppInfo.Version;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        // посилання відкриваються в браузері
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler((_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            e.Handled = true;
        }));

        foreach (var n in _nav) n.Click += (s, _) => ShowPage(int.Parse((string)((Button)s).Tag));
        NWizard.Click += (_, _) => Open(0, wizard: true);
        BtnNext.Click += (_, _) => { if (_page >= LastPage) FinishWizard(); else ShowPage(_page + 1); };
        BtnBack.Click += (_, _) => { if (_page > 0) ShowPage(_page - 1); };
        BtnSkip.Click += (_, _) => FinishWizard();
        BtnClose.Click += (_, _) => { SaveCalendarQuietly(); Hide(); };

        BtnBatRefresh.Click += (_, _) => { _app.RescanDevices(); UpdateBatteryStatus(); };

        foreach (var (rb, lang) in new[] { (RbLangUk, "uk"), (RbLangEn, "en"), (RbLangUk0, "uk"), (RbLangEn0, "en") })
            rb.Checked += (_, _) => { if (!_loading) { SaveCalendarQuietly(); _app.SetLanguage(lang); } };
        RbIsland.Checked += (_, _) => { if (!_loading) { _app.SetMode("strip", true); CbPin.IsEnabled = false; } };
        RbCard.Checked += (_, _) => { if (!_loading) { _app.SetMode("full", true); CbPin.IsEnabled = true; } };
        CbTop.Click += (_, _) => _app.SetTopmost(CbTop.IsChecked == true);
        CbPin.Click += (_, _) => _app.SetPinned(CbPin.IsChecked == true);
        SlOpacity.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            var v = Math.Round(SlOpacity.Value);
            LblOpacity.Text = $"{v}%";
            _app.SetBgOpacity(v / 100.0);
        };
        RbTrayIcon.Checked += (_, _) => { if (!_loading) _app.SetTrayStyle("icon"); };
        RbTrayNum.Checked += (_, _) => { if (!_loading) _app.SetTrayStyle("number"); };

        BtnCity.Click += (_, _) => FindCity();
        TbCity.KeyDown += (_, e) => { if (e.Key == Key.Return) FindCity(); };

        BtnCalTest.Click += (_, _) =>
        {
            // Refresh, а не Reload: кеш лишається, а календар, що вже вантажиться, не запускається вдруге
            if (!SaveCalendar()) _app.Calendar.Refresh();
            UpdateCalStatus();
            BuildCalColors();
        };

        BtnDiscord.Click += (_, _) =>
        {
            var id = TbDiscordId.Text.Trim();
            var sec = PbDiscordSecret.Password.Trim();
            if (id.Length == 0 || sec.Length == 0)
            {
                DiscordStatus.Text = L.T("Встав і Client ID, і Client Secret.", "Paste both the Client ID and the Client Secret.");
                return;
            }
            _app.ConnectDiscord(id, sec);
            DiscordStatus.Text = L.T("Підключаюся… Зараз у Discord з'явиться вікно — натисни «Авторизувати».",
                                     "Connecting… A window will appear in Discord — click “Authorize”.");
        };
        BtnDiscordOff.Click += (_, _) =>
        {
            _app.DisconnectDiscord();
            PbDiscordSecret.Password = "";
            DiscordStatus.Text = L.T("Не підключено.", "Not connected.");
        };

        CbAuto.Click += (_, _) => { Autostart.Set(CbAuto.IsChecked == true); CbAuto.IsChecked = Autostart.IsEnabled; };
        CbDesktop.Click += (_, _) => Shortcuts.SetDesktop(S, CbDesktop.IsChecked == true);

        // статус Discord і календаря у відкритому вікні
        _statusTimer.Tick += (_, _) =>
        {
            if (!IsVisible) return;
            ShowDiscordStatus();
            if (_page == 4) UpdateCalStatus();
        };
        _statusTimer.Start();
        _app.DevicesChanged += () => { if (IsVisible && _page == 1) UpdateBatteryStatus(); };
        _app.SettingsChanged += () => { if (IsVisible) LoadBehavior(); };
        _app.Calendar.Changed += () => { if (IsVisible) { BuildCalColors(); if (_page == 4) UpdateCalStatus(); } };
    }

    // закриття вікна = сховати (у майстрі — вважати, що налаштування завершено)
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_app.Exiting) { _statusTimer.Stop(); return; }
        e.Cancel = true;
        if (_wizard) FinishWizard();
        else { SaveCalendarQuietly(); Hide(); }
    }

    public void Open(int page, bool wizard = false)
    {
        _wizard = wizard;
        var wiz = wizard ? Visibility.Visible : Visibility.Collapsed;
        var notWiz = wizard ? Visibility.Collapsed : Visibility.Visible;
        Sidebar.Visibility = notWiz;
        StepText.Visibility = wiz;
        StepBar.Visibility = wiz;
        BtnBack.Visibility = BtnNext.Visibility = BtnSkip.Visibility = wiz;
        BtnClose.Visibility = notWiz;
        Title = AppInfo.Name + (wizard ? L.T(" — перше налаштування", " — first-time setup") : L.T(" — налаштування", " — settings"));
        LoadValues();
        _page = -1;
        if (!IsVisible) Show();
        Activate();
        UpdateLayout();
        ShowPage(page);
    }

    private void ShowPage(int i)
    {
        if (_page == 4 && i != 4) SaveCalendarQuietly();
        for (int k = 0; k <= LastPage; k++) _pages[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
        _page = i;
        for (int k = 1; k <= 6; k++) _nav[k - 1].Background = Palette.Brush(k == i ? "#2A2D33" : "#00000000");
        if (_wizard)
        {
            StepText.Text = $"{L.T("КРОК", "STEP")} {i + 1} {L.T("З", "OF")} 8 · {PageTitles[i].ToUpper()}";
            StepFill.Width = Math.Max(6, StepBar.ActualWidth * (i + 1) / 8);
            BtnBack.IsEnabled = i > 0;
            BtnNext.Content = i == LastPage ? L.T("Почати", "Start") : i == 0 ? L.T("Почнімо", "Let’s go") : L.T("Далі", "Next");
            BtnSkip.Visibility = i < LastPage ? Visibility.Visible : Visibility.Collapsed;
        }
        if (i == 1) UpdateBatteryStatus();
        if (i == 6) UpdateSysInfo();
    }

    private void FinishWizard()
    {
        SaveCalendarQuietly();
        S.FirstRunDone = true;
        S.Save();
        Hide();
        if (!_app.Widget.IsVisible) _app.ToggleWidget();
    }

    private void LoadValues()
    {
        _loading = true;
        try
        {
            LoadBehavior();
            RbLangUk.IsChecked = RbLangUk0.IsChecked = !L.En;
            RbLangEn.IsChecked = RbLangEn0.IsChecked = L.En;
            var op = S.SafeBgOpacity;
            SlOpacity.Value = Math.Round(op * 100);
            LblOpacity.Text = $"{Math.Round(op * 100)}%";
            bool num = S.TrayStyle == "number";
            RbTrayIcon.IsChecked = !num;
            RbTrayNum.IsChecked = num;
            TbCity.Text = S.WeatherCity;
            CityStatus.Text = L.T("Зараз: ", "Current: ") + S.WeatherCity;
            TbCal.Text = string.Join("\r\n", CalendarService.GetUrls());
            TbDiscordId.Text = S.DiscordClientId;
            PbDiscordSecret.Password = S.DiscordClientSecret;
            ShowDiscordStatus();
            CbAuto.IsChecked = Autostart.IsEnabled;
            CbDesktop.IsChecked = Shortcuts.DesktopExists;
            UpdateBatteryStatus();
            UpdateSysInfo();
            UpdateCalStatus();
            BuildCalColors();
        }
        finally { _loading = false; }
    }

    // форма й поведінка — їх можна змінити й з меню віджета, поки вікно відкрите
    private void LoadBehavior()
    {
        bool wasLoading = _loading;
        _loading = true;
        bool island = S.IsIsland;
        RbIsland.IsChecked = island;
        RbCard.IsChecked = !island;
        CbTop.IsChecked = S.Topmost;
        CbPin.IsChecked = S.Pinned;
        CbPin.IsEnabled = !island;
        _loading = wasLoading;
    }

    private void UpdateBatteryStatus()
    {
        var lines = _app.KnownDevices.OrderBy(d => d.Name).Select(d => $"\u2713  {d.Name} — {d.Level}%").ToList();
        if (lines.Count == 0)
        {
            BatStatus.Text = L.T("Поки не бачу даних від Synapse.\nПереконайся, що Razer Synapse 4 запущений, а мишка й навушники ввімкнені, і натисни «Перевірити знову».",
                                 "No data from Synapse yet.\nMake sure Razer Synapse 4 is running and your mouse and headset are on, then click “Check again”.");
            BatStatus.Foreground = Palette.Brush("#FFB347");
        }
        else
        {
            BatStatus.Text = string.Join("\n", lines);
            BatStatus.Foreground = Palette.Brush("#D6D9DE");
        }
    }

    // жовте попередження, якщо збережені секрети не розшифрувалися й Discord ще не підключено знову
    private void ShowDiscordStatus()
    {
        bool locked = S.SecretsLocked && _app.DiscordState.Status != "ok";
        DiscordStatus.Text = locked
            ? L.T("Збережені дані не вдалося розшифрувати — увійдіть у Discord знову", "Saved data could not be decrypted — sign in to Discord again")
            : DiscordStatusText();
        DiscordStatus.Foreground = Palette.Brush(locked ? Palette.Yellow : "#8A8F98");
    }

    private string DiscordStatusText()
    {
        var s = _app.DiscordState;
        return s.Status switch
        {
            "ok" => "\u2713 " + L.T("Підключено. Стан мікрофона відстежується.", "Connected. Microphone state is being tracked."),
            "nodiscord" => L.T("Discord не запущений — підключення відбудеться автоматично, коли він запуститься.",
                               "Discord is not running — it will connect automatically when Discord starts."),
            "authorizing" => L.T("Чекаю підтвердження: відкрий Discord і натисни «Авторизувати».", "Waiting for approval: open Discord and click “Authorize”."),
            "needauth" => L.T("Потрібно підключити знову: натисни «Підключити».", "Needs reconnecting: click “Connect”."),
            "denied" => L.T("Доступ не надано: ", "Access denied: ") + s.LastError,
            "autherror" => L.T("Помилка: ", "Error: ") + s.LastError,
            "notconfigured" => L.T("Не підключено.", "Not connected."),
            _ => s.HasClient ? L.T("Підключаюся…", "Connecting…") : L.T("Не підключено.", "Not connected."),
        };
    }

    private void UpdateSysInfo()
    {
        SysInfo.Text = L.En
            ? $"Version: {AppInfo.Version}\nProgram: {AppInfo.ExePath}\nSettings and error log: {AppInfo.DataDir}\n"
              + $"The “{AppInfo.Name}” and “{AppInfo.Name} — Settings” shortcuts are in the Start menu."
            : $"Версія: {AppInfo.Version}\nПрограма: {AppInfo.ExePath}\nНалаштування й журнал помилок: {AppInfo.DataDir}\n"
              + $"Ярлики «{AppInfo.Name}» і «{AppInfo.Name} — налаштування» є в меню «Пуск».";
    }

    private bool SaveCalendar() => _app.Calendar.SaveUrls(TbCal.Text.Split('\n'));

    // рядок кольорів для кожного календаря; назва — з самого календаря, коли він завантажиться
    private void BuildCalColors()
    {
        CalColors.Children.Clear();
        var urls = CalendarService.GetUrls();
        var sources = _app.Calendar.Sources;
        CalColorsTitle.Visibility = CalColorsHint.Visibility = urls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            var current = S.CalendarColors.GetValueOrDefault(Settings.ColorKey(url), Palette.Accent);
            var src = sources.FirstOrDefault(s => s.Url == url);
            var name = src?.Name ?? $"{L.T("Календар", "Calendar")} {i + 1}";
            var row = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(new TextBlock { Text = name, Foreground = Palette.Brush("#E6E8EB") });
            var (status, color) = src?.Status switch
            {
                RazerIsland.Calendar.CalStatus.Ok => ("✓ " + L.T("Оновлено о ", "Updated at ") + src.LastSuccess?.ToString("HH:mm"), "#8A8F98"),
                RazerIsland.Calendar.CalStatus.Error => ("⚠ " + CalendarSource.Describe(src.Error), Palette.Red),
                _ => (L.T("Завантажую…", "Loading…"), "#8A8F98"),
            };
            row.Children.Add(new TextBlock { Text = status, Foreground = Palette.Brush(color), FontSize = 11, Margin = new Thickness(0, 2, 0, 6) });
            var swatches = new WrapPanel();
            foreach (var (hex, uk, en) in Palette.CalendarColors)
            {
                var b = new Button
                {
                    Style = (Style)FindResource("Swatch"), Background = Palette.Brush(hex), ToolTip = L.T(uk, en),
                    Tag = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase) ? "sel" : null,
                };
                System.Windows.Automation.AutomationProperties.SetName(b, $"{name}: {L.T(uk, en)}");
                b.Click += (_, _) =>
                {
                    S.CalendarColors[Settings.ColorKey(url)] = hex;
                    S.Save();
                    _app.Widget.RenderCalendar();
                    BuildCalColors();
                };
                swatches.Children.Add(b);
            }
            row.Children.Add(swatches);
            CalColors.Children.Add(row);
        }
    }

    private void SaveCalendarQuietly()
    {
        try { SaveCalendar(); }
        catch (Exception e) { Log.Write("Збереження календарів: " + e.Message); }
    }

    private void UpdateCalStatus()
    {
        var urls = CalendarService.GetUrls();
        var cal = _app.Calendar;
        if (CalendarService.CalendarsLocked)
        {
            CalStatus.Text = L.T("Збережені посилання не вдалося розшифрувати — вставте посилання знову", "Saved links could not be decrypted — paste the links again");
            CalStatus.Foreground = Palette.Brush(Palette.Yellow);
            return;
        }
        if (urls.Count == 0)
        {
            CalStatus.Text = L.T("Календар ще не підключено.", "No calendar connected yet.");
            CalStatus.Foreground = Palette.Brush("#8A8F98");
            return;
        }
        var sources = cal.Sources;
        var failed = cal.Failed;
        if (cal.AllFailed)
        {
            var errors = failed.Select(s => s.Error).Distinct().ToList();
            CalStatus.Text = errors.Count == 1 ? CalendarSource.Describe(errors[0]) + "." : L.T("Не вдалося завантажити календарі.", "Could not load the calendars.");
            CalStatus.Foreground = Palette.Brush(Palette.Red);
            return;
        }
        if (failed.Count > 0)
        {
            var list = sources.ToList();
            var names = string.Join(", ", failed.Select(s => L.T($"«{s.Title(list.IndexOf(s))}»", $"“{s.Title(list.IndexOf(s))}”")));
            CalStatus.Text = L.T($"Підключено {cal.OkCount} з {sources.Count}. Не працює: {names}", $"Connected {cal.OkCount} of {sources.Count}. Not working: {names}");
            CalStatus.Foreground = Palette.Brush(Palette.Yellow);
            return;
        }
        if (cal.OkCount == 0)
        {
            CalStatus.Text = L.T("Завантажую…", "Loading…");
            CalStatus.Foreground = Palette.Brush("#8A8F98");
            return;
        }
        int events = cal.AllEvents.Count();
        CalStatus.Text = "✓ " + (L.En
            ? $"Calendars connected: {cal.OkCount} of {urls.Count}. Events in the coming week: {events}."
            : $"Підключено календарів: {cal.OkCount} з {urls.Count}. Подій на найближчий тиждень: {events}.");
        CalStatus.Foreground = Palette.Brush(Palette.Green);
    }

    private async void FindCity()
    {
        var name = TbCity.Text.Trim();
        if (name.Length == 0) return;
        CityStatus.Text = L.T("Шукаю…", "Searching…");
        try
        {
            var place = await _app.SetWeatherCityAsync(name);
            if (place is null)
            {
                CityStatus.Text = L.T("Місто не знайдено. Спробуй латиницею, наприклад: Kyiv.", "City not found. Try another spelling, e.g. Kyiv.");
                return;
            }
            CityStatus.Text = $"\u2713 {L.T("Збережено", "Saved")}: {place.Name}{(place.Country.Length > 0 ? ", " + place.Country : "")}";
            TbCity.Text = place.Name;
        }
        catch { CityStatus.Text = L.T("Не вдалося знайти місто (немає інтернету?).", "Could not search for the city (no internet?)."); }
    }
}
