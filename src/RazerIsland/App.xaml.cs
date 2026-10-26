using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using RazerIsland.Battery;
using RazerIsland.Calendar;
using RazerIsland.Core;
using RazerIsland.Discord;
using RazerIsland.UI;
using RazerIsland.Weather;

namespace RazerIsland;

/// <summary>Точка входу та спільний стан віджета: дані, таймери, трей, мікрофон.</summary>
public partial class App : Application
{
    private const int RefreshSec = 3;

    private SingleInstance? _instance;
    private SynapseLogReader _reader = null!;
    private DiscordMicService _discord = null!;
    private TrayManager _tray = null!;
    private MicWindow _micWin = null!;
    private SettingsWindow? _settingsWin;

    private readonly Dictionary<string, RazerDevice> _known = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(RefreshSec) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _netTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _micTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromMilliseconds(2600) };
    private int _netTick;
    private int _refreshTick;
    private List<RazerDevice> _wireless = [];
    private bool _wirelessBusy;
    private readonly HashSet<string> _loggedSources = [];
    private readonly Dictionary<string, PowerState> _lastState = new();
    // пристрої, щойно підключені кабелем: ключ — Serial, значення — коли й через який USB-вузол
    private readonly Dictionary<string, (DateTime At, string UsbId)> _cabled = new();
    private static readonly TimeSpan CableGrace = TimeSpan.FromSeconds(90);
    private UsbCableWatcher? _cable;
    private IntPtr _focusBeforeNotice;

    // мікрофон
    private bool _micMuted, _micKnown, _rawMute, _rawDeaf, _reportedError;
    private int _micVersion = -1;

    public Settings Settings { get; private set; } = null!;
    public CalendarService Calendar { get; } = new();
    public MainWindow Widget { get; private set; } = null!;
    public bool Exiting { get; private set; }
    public IReadOnlyCollection<RazerDevice> KnownDevices => _known.Values;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Write($"Старт {AppInfo.Name} {AppInfo.Version}. .NET {Environment.Version}, {IntPtr.Size * 8}-bit");
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("Помилка під час роботи: " + ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("Критична помилка: " + ex.ExceptionObject);

        try { Start(e.Args); }
        catch (Exception ex)
        {
            Log.Write("ПОМИЛКА ЗАПУСКУ: " + ex);
            Shutdown();
        }
    }

    private void Start(string[] args)
    {
        _instance = SingleInstance.Acquire();
        if (_instance is null) { Shutdown(); return; }
        _instance.QuitRequested += () => Dispatcher.BeginInvoke(() => { if (!Exiting) ExitApp(); });

        LegacyImport.Run();
        Settings = Settings.Load();
        L.Init(Settings.Language!);
        _reader = new SynapseLogReader(AppInfo.SynapseLogDir);
        System.Windows.Forms.Application.EnableVisualStyles();

        Widget = new MainWindow(this);
        Widget.PeekChanged += UpdateTopmost;
        _micWin = new MicWindow();
        _tray = new TrayManager(() => OpenSettings(2), ToggleWidget, () => ExitApp());
        _discord = new DiscordMicService(Settings);
        _discord.Start();
        try
        {
            _cable = new UsbCableWatcher();
            _cable.Connected += OnCableConnected;
            _cable.Disconnected += OnCableDisconnected;
            Widget.DeviceTreeChanged += _cable.Check;
        }
        catch (Exception ex) { Log.Write("Стеження за USB: " + ex.Message); }

        if (!AppInfo.IsCustomDataDir)
        {
            Autostart.Ensure(Settings);
            Shortcuts.EnsureDesktop(Settings);
        }

        Calendar.Changed += () => Widget.RenderCalendar();
        _clockTimer.Tick += (_, _) => Widget.UpdateClock();
        _timer.Tick += (_, _) => RefreshAll();
        // фонові оновлення: погода кожні 15 хв, календар кожні 10 хв
        _netTimer.Tick += (_, _) =>
        {
            _netTimer.Interval = TimeSpan.FromMinutes(1);
            if (_netTick % 15 == 0) RefreshWeather();
            if (_netTick % 10 == 0) Calendar.Refresh();
            _netTick++;
        };
        _micTimer.Tick += (_, _) => PollMic();
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            StopGlow();
            Widget.NoticeActive = false;
            UpdateTopmost();
            if (UseIntegrated && !Widget.Expanded) Widget.ApplyVisual(false, true);
            UpdateMicWindow();
            RestoreFocusAfterNotice();
        };

        Widget.SyncMenu();
        Widget.RenderCalendar();
        Widget.UpdateClock();
        _clockTimer.Start();

        RefreshAll();
        SetMode(Settings.IsIsland ? "strip" : "full", false);
        Widget.LayoutReady = true;

        _timer.Start();
        _netTimer.Start();
        _micTimer.Start();

        Widget.Left = -10000;
        Widget.Top = -10000;
        Widget.Show();
        if (!Settings.Visible) Widget.Hide();

        try
        {
            bool openSettings = args.Any(a => a.Equals("-OpenSettings", StringComparison.OrdinalIgnoreCase)
                                           || a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
            if (!Settings.FirstRunDone) OpenSettings(0, wizard: true);
            else if (openSettings) OpenSettings(2);
        }
        catch (Exception ex) { Log.Write("Вікно налаштувань: " + ex); }
        Log.Write("Віджет запущено.");
    }

    // ================== ДАНІ ==================

    public void RefreshAll()
    {
        DeviceSnapshot? data = null;
        try { data = _reader.Read(); }
        catch (Exception e) { Log.Write("Читання Synapse: " + e.Message); }

        if (_refreshTick++ % 10 == 0) _ = RefreshWirelessAsync();

        // показуємо лише активні: вимкнені, сплячі й відключені пристрої зникають, а з'явившись знову — повертаються
        _known.Clear();
        foreach (var raw in (data?.Devices ?? []).Concat(_wireless))
        {
            // поки Synapse не записав «Charging» (≈30 с після підключення кабелю), вірити кабелю
            var d = _cabled.ContainsKey(raw.Serial) && DateTime.Now - _cabled[raw.Serial].At < CableGrace ? raw with { State = PowerState.Charging } : raw;
            // сповіщення лише про перехід на заряджання; пристрій, побачений уперше (запуск віджета), не сповіщає
            if (d.State == PowerState.Charging && _lastState.TryGetValue(d.Serial, out var prev) && prev != PowerState.Charging)
                ShowChargingNotice(d);
            _lastState[d.Serial] = d.State;
            if (d.State != PowerState.Off) _known[d.Serial] = d;
        }

        foreach (var d in _known.Values) _tray.Update(d, d.State, Settings.TrayStyle);
        _tray.RemoveExcept(_known.Keys);
        Widget.ShowDevices(data, _known);
        DevicesChanged?.Invoke();
    }

    // кабель підключили до вже активного бездротового пристрою Razer — сповіщаємо одразу, не чекаючи Synapse.
    // Сам приймач теж з'являється як USB-пристрій Razer, але тоді пристрій ще не активний — збігу немає.
    private void OnCableConnected(string usbId, string usbName)
    {
        var d = _known.Values.FirstOrDefault(k => k.Brand == "Razer" && usbName.Contains(k.Name, StringComparison.OrdinalIgnoreCase));
        // один кабель дає кілька USB-вузлів з тією ж назвою — реагуємо на перший
        if (d is null || (_cabled.TryGetValue(d.Serial, out var c) && DateTime.Now - c.At < CableGrace)) return;
        _cabled[d.Serial] = (DateTime.Now, usbId);
        RefreshAll();
    }

    private void OnCableDisconnected(string usbId)
    {
        foreach (var serial in _cabled.Where(c => c.Value.UsbId == usbId).Select(c => c.Key).ToList())
        {
            _cabled.Remove(serial);
            RefreshAll();
        }
    }

    // Bluetooth, Logitech, SteelSeries, Corsair: заряд змінюється повільно, тож досить кожні 30 с і у фоні
    private async Task RefreshWirelessAsync()
    {
        if (_wirelessBusy) return;
        _wirelessBusy = true;
        try
        {
            var all = new List<RazerDevice>();
            all.AddRange(await Safe("Bluetooth", BluetoothReader.ReadAsync));
            all.AddRange(await Safe("Logitech", () => Task.Run(LogitechReader.Read)));
            all.AddRange(await Safe("SteelSeries", () => Task.Run(SteelSeriesReader.Read)));
            all.AddRange(await Safe("Corsair", () => Task.Run(CorsairReader.Read)));
            _wireless = all;
        }
        finally { _wirelessBusy = false; }
        if (!Exiting) RefreshAll();
    }

    // одне джерело не має ламати інші; помилку кожного пишемо в журнал лише раз
    private async Task<List<RazerDevice>> Safe(string source, Func<Task<List<RazerDevice>>> read)
    {
        try { return await read(); }
        catch (Exception e)
        {
            if (_loggedSources.Add(source)) Log.Write($"{source}: {e.Message}");
            return [];
        }
    }

    /// <summary>Скинути кеш логів і перечитати (кнопка «Перевірити знову»).</summary>
    public void RescanDevices()
    {
        _reader.ClearCache();
        RefreshAll();
    }

    public event Action? DevicesChanged;

    public async void RefreshWeather()
    {
        try { Widget.ShowWeather(await WeatherService.GetAsync(Settings.WeatherLat, Settings.WeatherLon)); }
        catch (Exception e)
        {
            Log.Write("Погода: " + e.Message);
            Widget.ShowWeatherError();
        }
    }

    // ================== КОМАНДИ ==================

    public void ToggleWidget()
    {
        if (Widget.IsVisible) { Widget.Hide(); Settings.Visible = false; }
        else { Widget.Show(); Widget.Activate(); Settings.Visible = true; }
        Settings.Save();
        UpdateTopmost();
        UpdateMicWindow();
    }

    public void SetPinned(bool on)
    {
        Settings.Pinned = on;
        Settings.Save();
        Widget.SyncMenu();
        SettingsChanged?.Invoke();
    }

    public void SetTopmost(bool on)
    {
        Settings.Topmost = on;
        Settings.Save();
        Widget.SyncMenu();
        UpdateTopmost();
        SettingsChanged?.Invoke();
    }

    public void SetMode(string mode, bool animate)
    {
        Settings.Mode = mode;
        Settings.Save();
        Widget.SyncMenu();
        UpdateTopmost();
        UpdateMicWindow();
        if (Settings.IsIsland) Widget.ApplyVisual(Widget.IsCardMouseOver, animate);
        else
        {
            Widget.ApplyVisual(true, animate);
            Widget.PositionCard();
        }
        SettingsChanged?.Invoke();
    }

    public void SetTrayStyle(string style)
    {
        Settings.TrayStyle = style;
        Settings.Save();
        _tray.Invalidate();
        RefreshAll();
    }

    public void SetBgOpacity(double value)
    {
        Settings.BgOpacity = value;
        Settings.Save();
        Widget.ApplyCardBackground();
    }

    public async Task<GeoPlace?> SetWeatherCityAsync(string name)
    {
        var place = await WeatherService.FindCityAsync(name);
        if (place is null) return null;
        Settings.WeatherLat = place.Latitude;
        Settings.WeatherLon = place.Longitude;
        Settings.WeatherCity = place.Name;
        Settings.Save();
        RefreshWeather();
        return place;
    }

    /// <summary>Зміни налаштувань із меню віджета — щоб відкрите вікно налаштувань оновилося.</summary>
    public event Action? SettingsChanged;

    public void OpenSettings(int page, bool wizard = false)
    {
        _settingsWin ??= new SettingsWindow(this);
        _settingsWin.Open(page, wizard);
    }

    /// <summary>Мова застосовується перезапуском (тексти задаються під час створення вікон).</summary>
    public void SetLanguage(string lang)
    {
        if (lang == L.Code) return;
        Settings.Language = lang;
        Settings.Save();
        ExitApp(restart: true, args: "-OpenSettings");
    }

    public void ExitApp(bool restart = false, string args = "")
    {
        Exiting = true;
        foreach (var t in new[] { _timer, _netTimer, _micTimer, _clockTimer, _noticeTimer }) t.Stop();
        try { _discord?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        if (restart)
        {
            _instance?.Dispose();
            _instance = null;
            try { Process.Start(new ProcessStartInfo(AppInfo.ExePath, args) { UseShellExecute = false }); }
            catch (Exception e) { Log.Write("Перезапуск: " + e.Message); }
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    // ================== DISCORD: СТАН МІКРОФОНА ==================

    public DiscordMicService.State DiscordState => _discord.Snapshot();

    public void ConnectDiscord(string id, string secret)
    {
        Settings.DiscordClientId = id;
        Settings.DiscordClientSecret = secret;
        Settings.DiscordToken = "";
        Settings.DiscordRefresh = "";
        Settings.Save();
        _reportedError = true; // помилки показуються у вікні налаштувань
        _discord.Connect(id, secret);
    }

    public void DisconnectDiscord()
    {
        Settings.DiscordClientId = Settings.DiscordClientSecret = Settings.DiscordToken = Settings.DiscordRefresh = "";
        Settings.Save();
        _discord.Disconnect();
    }

    private bool UseIntegrated => Settings.IsIsland && Widget.IsVisible;

    private void UpdateTopmost()
    {
        bool want = Settings.Topmost || (UseIntegrated && (_micMuted || Widget.NoticeActive || Widget.Peeking));
        if (Widget.Topmost == want) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(Widget).Handle;
        // після сповіщення / наведення повертаємо вікно на те місце в z-порядку, де воно було:
        // зняття «поверх усіх» інакше лишає його над усіма вікнами
        if (want) _windowAbove = Native.WindowAbove(hwnd);
        Widget.Topmost = want;
        if (!want && _windowAbove != IntPtr.Zero && Native.IsWindow(_windowAbove)) Native.PlaceBelow(hwnd, _windowAbove);
    }

    private IntPtr _windowAbove;

    private void UpdateMicBadge() => Widget.SetMicOnly(_micMuted && _micKnown);

    private void UpdateMicWindow() =>
        _micWin.Update(!UseIntegrated && (Widget.NoticeActive || (_micMuted && _micKnown)), Widget.NoticeActive);

    private void ShowMicNotice(bool mute, bool deaf)
    {
        bool off = mute || deaf;
        var text = deaf ? L.T("Звук вимкнено", "Sound off") : mute ? L.T("Мікрофон вимкнено", "Microphone muted") : L.T("Мікрофон увімкнено", "Microphone on");
        var color = off ? Palette.Red : Palette.Green;
        ShowNotice(text, color, size => Glyphs.MicIcon(off, size, color), glow: false);
    }

    private void ShowChargingNotice(RazerDevice d) =>
        ShowNotice(L.T($"{d.Name} заряджається", $"{d.Name} is charging"), Palette.Green, Glyphs.ChargingIcon, glow: true);

    /// <summary>Сповіщення в острівці (або в окремому острівці, якщо віджет у режимі картки чи схований) на ~2.6 с.</summary>
    private void ShowNotice(string text, string color, Func<double, FrameworkElement> icon, bool glow)
    {
        if (!Widget.NoticeActive) _focusBeforeNotice = Native.GetForegroundWindow();
        Widget.NoticeActive = true;
        Widget.SetNotice(text, color, icon(16));
        _micWin.SetNotice(text, color, icon(15));
        UpdateMicBadge();
        UpdateTopmost();
        if (UseIntegrated && !Widget.Expanded) Widget.ApplyVisual(false, true);
        UpdateMicWindow();
        StopGlow();
        if (glow) EdgeGlow.Start(UseIntegrated ? Widget.CardBorder : _micWin.CardBorder, color);
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // інколи після сповіщення активне вікно втрачало фокус (причину не відтворено) —
    // повертаємо його, якщо фокус опинився у віджета чи «ніде», і записуємо, куди він пішов
    private void RestoreFocusAfterNotice()
    {
        var before = _focusBeforeNotice;
        _focusBeforeNotice = IntPtr.Zero;
        var now = Native.GetForegroundWindow();
        if (before == IntPtr.Zero || now == before || !Native.IsWindow(before) || !Native.IsLostFocus(now)) return;
        Log.Write($"Фокус після сповіщення перейшов до «{Native.ClassName(now)}» — повертаю попередньому вікну «{Native.ClassName(before)}».");
        Native.SetForegroundWindow(before);
    }

    private void StopGlow()
    {
        EdgeGlow.Stop(Widget.CardBorder);
        EdgeGlow.Stop(_micWin.CardBorder);
    }

    private void PollMic()
    {
        if (_discord.TakeChangedTokens() is { } tokens)
        {
            Settings.DiscordToken = tokens.Token;
            Settings.DiscordRefresh = tokens.Refresh;
            Settings.Save();
        }

        var s = _discord.Snapshot();
        if (!s.Connected)
        {
            if (_micKnown)
            {
                _micKnown = false;
                _micMuted = false;
                UpdateMicBadge(); UpdateTopmost(); UpdateMicWindow();
            }
        }
        else if (s.Version != _micVersion)
        {
            _micVersion = s.Version;
            bool changed = _micKnown && (s.Mute != _rawMute || s.Deaf != _rawDeaf);
            _rawMute = s.Mute;
            _rawDeaf = s.Deaf;
            _micMuted = s.Mute || s.Deaf;
            _micKnown = true;
            if (changed) ShowMicNotice(s.Mute, s.Deaf);
            else { UpdateMicBadge(); UpdateTopmost(); UpdateMicWindow(); }
        }

        if (s.Status is "denied" or "autherror" && !_reportedError)
        {
            _reportedError = true;
            MessageBox.Show(L.T("Не вдалося підключитися до Discord.\n", "Could not connect to Discord.\n") + s.LastError, L.T("Discord — мікрофон", "Discord microphone"));
        }
    }
}
