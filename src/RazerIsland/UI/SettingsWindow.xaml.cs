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
    private static readonly string[] PageTitles = ["Вітання", "Батарея", "Вигляд", "Погода", "Календар", "Мікрофон Discord", "Система", "Готово"];
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
        VersionText.Text = "версія " + AppInfo.Version;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }

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
            if (!SaveCalendar()) _app.Calendar.Reload();
            UpdateCalStatus();
        };

        BtnDiscord.Click += (_, _) =>
        {
            var id = TbDiscordId.Text.Trim();
            var sec = PbDiscordSecret.Password.Trim();
            if (id.Length == 0 || sec.Length == 0) { DiscordStatus.Text = "Встав і Client ID, і Client Secret."; return; }
            _app.ConnectDiscord(id, sec);
            DiscordStatus.Text = "Підключаюся… Зараз у Discord з'явиться вікно — натисни «Авторизувати».";
        };
        BtnDiscordOff.Click += (_, _) =>
        {
            _app.DisconnectDiscord();
            PbDiscordSecret.Password = "";
            DiscordStatus.Text = "Не підключено.";
        };

        CbAuto.Click += (_, _) => { Autostart.Set(CbAuto.IsChecked == true); CbAuto.IsChecked = Autostart.IsEnabled; };
        CbDesktop.Click += (_, _) => Shortcuts.SetDesktop(S, CbDesktop.IsChecked == true);

        // статус Discord і календаря у відкритому вікні
        _statusTimer.Tick += (_, _) =>
        {
            if (!IsVisible) return;
            DiscordStatus.Text = DiscordStatusText();
            if (_page == 4) UpdateCalStatus();
        };
        _statusTimer.Start();
        _app.DevicesChanged += () => { if (IsVisible && _page == 1) UpdateBatteryStatus(); };
        _app.SettingsChanged += () => { if (IsVisible) LoadBehavior(); };
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
        Title = wizard ? $"{AppInfo.Name} — перше налаштування" : $"{AppInfo.Name} — налаштування";
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
            StepText.Text = $"КРОК {i + 1} З 8 · {PageTitles[i].ToUpper()}";
            StepFill.Width = Math.Max(6, StepBar.ActualWidth * (i + 1) / 8);
            BtnBack.IsEnabled = i > 0;
            BtnNext.Content = i == LastPage ? "Почати" : i == 0 ? "Почнімо" : "Далі";
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
            var op = S.SafeBgOpacity;
            SlOpacity.Value = Math.Round(op * 100);
            LblOpacity.Text = $"{Math.Round(op * 100)}%";
            bool num = S.TrayStyle == "number";
            RbTrayIcon.IsChecked = !num;
            RbTrayNum.IsChecked = num;
            TbCity.Text = S.WeatherCity;
            CityStatus.Text = "Зараз: " + S.WeatherCity;
            TbCal.Text = string.Join("\r\n", CalendarService.GetUrls());
            TbDiscordId.Text = S.DiscordClientId;
            PbDiscordSecret.Password = S.DiscordClientSecret;
            DiscordStatus.Text = DiscordStatusText();
            CbAuto.IsChecked = Autostart.IsEnabled;
            CbDesktop.IsChecked = Shortcuts.DesktopExists;
            UpdateBatteryStatus();
            UpdateSysInfo();
            UpdateCalStatus();
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
            BatStatus.Text = "Поки не бачу даних від Synapse.\nПереконайся, що Razer Synapse 4 запущений, а мишка й навушники ввімкнені, і натисни «Перевірити знову».";
            BatStatus.Foreground = Palette.Brush("#FFB347");
        }
        else
        {
            BatStatus.Text = string.Join("\n", lines);
            BatStatus.Foreground = Palette.Brush("#D6D9DE");
        }
    }

    private string DiscordStatusText()
    {
        var s = _app.DiscordState;
        return s.Status switch
        {
            "ok" => "\u2713 Підключено. Стан мікрофона відстежується.",
            "nodiscord" => "Discord не запущений — підключення відбудеться автоматично, коли він запуститься.",
            "authorizing" => "Чекаю підтвердження: відкрий Discord і натисни «Авторизувати».",
            "needauth" => "Потрібно підключити знову: натисни «Підключити».",
            "denied" => "Доступ не надано: " + s.LastError,
            "autherror" => "Помилка: " + s.LastError,
            "notconfigured" => "Не підключено.",
            _ => s.HasClient ? "Підключаюся…" : "Не підключено.",
        };
    }

    private void UpdateSysInfo()
    {
        SysInfo.Text = $"Версія: {AppInfo.Version}\nПрограма: {AppInfo.ExePath}\nНалаштування й журнал помилок: {AppInfo.DataDir}\n"
                     + $"Ярлики «{AppInfo.Name}» і «{AppInfo.Name} — налаштування» є в меню «Пуск».";
    }

    private bool SaveCalendar() => _app.Calendar.SaveUrls(TbCal.Text.Split('\n'));

    private void SaveCalendarQuietly()
    {
        try { SaveCalendar(); }
        catch (Exception e) { Log.Write("Збереження календарів: " + e.Message); }
    }

    private void UpdateCalStatus()
    {
        var urls = CalendarService.GetUrls();
        var cal = _app.Calendar;
        if (urls.Count == 0)
        {
            CalStatus.Text = "Календар ще не підключено.";
            CalStatus.Foreground = Palette.Brush("#8A8F98");
            return;
        }
        if (cal.LoadedCount == 0)
        {
            if (cal.LastError)
            {
                CalStatus.Text = "Не вдалося завантажити. Перевір посилання та інтернет.";
                CalStatus.Foreground = Palette.Brush(Palette.Red);
            }
            else
            {
                CalStatus.Text = "Завантажую…";
                CalStatus.Foreground = Palette.Brush("#8A8F98");
            }
            return;
        }
        CalStatus.Text = $"\u2713 Підключено календарів: {cal.LoadedCount} з {urls.Count}. Подій на найближчий тиждень: {cal.AllEvents.Count()}.";
        CalStatus.Foreground = Palette.Brush(Palette.Green);
    }

    private async void FindCity()
    {
        var name = TbCity.Text.Trim();
        if (name.Length == 0) return;
        CityStatus.Text = "Шукаю…";
        try
        {
            var place = await _app.SetWeatherCityAsync(name);
            if (place is null)
            {
                CityStatus.Text = "Місто не знайдено. Спробуй латиницею, наприклад: Riihimaki.";
                return;
            }
            CityStatus.Text = $"\u2713 Збережено: {place.Name}{(place.Country.Length > 0 ? ", " + place.Country : "")}";
            TbCity.Text = place.Name;
        }
        catch { CityStatus.Text = "Не вдалося знайти місто (немає інтернету?)."; }
    }
}
