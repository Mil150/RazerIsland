using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RazerIsland.Battery;
using RazerIsland.Calendar;
using RazerIsland.Core;
using RazerIsland.Weather;

namespace RazerIsland.UI;

/// <summary>Віджет: острівець зверху (розкривається при наведенні) або плаваюча картка.</summary>
public partial class MainWindow : Window
{
    private const double FullW = 800;
    private const double StripH = 20;
    private const double NoticeH = 30;
    private const int AnimMs = 280;
    private static readonly Thickness PadFull = new(16, 12, 16, 12);
    private static readonly Thickness PadStrip = new(12, 5, 12, 5);

    private readonly App _app;
    private readonly QuarticEase _ease = new() { EasingMode = EasingMode.EaseOut };
    private readonly Dictionary<string, DeviceRow> _rows = new();
    private readonly DispatcherTimer _pointerTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _pointerInside;
    private DateTime? _pointerSince;
    private string _lastTargets = "";
    private double _lastBatH = -1;
    private int _lastMinute = -1;

    private Settings S => _app.Settings;

    public bool Expanded { get; private set; } = true;
    public bool LayoutReady { get; set; }
    /// <summary>Показується сповіщення про мікрофон.</summary>
    public bool NoticeActive { get; set; }
    /// <summary>Мікрофон вимкнено: у згорнутому острівці лишається лише значок.</summary>
    public bool MicOnlyMode { get; private set; }

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Topmost = S.Topmost;
        PinBtn.Text = Glyphs.Pin;

        SourceInitialized += OnSourceInitialized;
        // якщо Провідник перезапуститься, вікно зникне разом з ним — тоді віджет перезапуститься сам
        Closed += (_, _) => { if (!_app.Exiting) _app.ExitApp(restart: true); };
        Loaded += OnLoaded;
        MouseLeftButtonDown += OnDrag;
        PinBtn.MouseLeftButtonDown += (_, e) => { e.Handled = true; _app.SetPinned(!S.Pinned); };

        WSettings.Click += (_, _) => _app.OpenSettings(2);
        WModeStrip.Click += (_, _) => _app.SetMode("strip", true);
        WModeFull.Click += (_, _) => _app.SetMode("full", true);
        WPin.Click += (_, _) => _app.SetPinned(WPin.IsChecked);
        WTop.Click += (_, _) => _app.SetTopmost(WTop.IsChecked);
        WHide.Click += (_, _) => _app.ToggleWidget();
        WExit.Click += (_, _) => _app.ExitApp();

        // наведення: позицію курсора перевіряємо самі — коли острівець під іншим вікном, події миші отримує те вікно
        _pointerTimer.Tick += OnPointerTick;
        _pointerTimer.Start();
    }

    // не показувати в Alt+Tab і не ховатися при «Згорнути все» (Win+D)
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            var ex = Native.GetLong(h, Native.GWL_EXSTYLE);
            Native.SetLong(h, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
            var pm = Native.FindWindow("Progman", null);
            if (pm != IntPtr.Zero) Native.SetLong(h, Native.GWL_HWNDPARENT, pm.ToInt64());
            HwndSource.FromHwnd(h)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool _) =>
            {
                if (msg == 0x0219) DeviceTreeChanged?.Invoke(); // WM_DEVICECHANGE
                return IntPtr.Zero;
            });
        }
        catch { }
    }

    /// <summary>Windows повідомила, що набір пристроїв змінився (підключили / від'єднали щось).</summary>
    public event Action? DeviceTreeChanged;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        if (S.Left is double l && S.Top is double t &&
            l >= SystemParameters.VirtualScreenLeft - 50 &&
            l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
            t >= SystemParameters.VirtualScreenTop - 50 &&
            t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50)
        {
            Left = l; Top = t;
        }
        else
        {
            Left = wa.Right - ActualWidth - 10;
            Top = wa.Top + 10;
        }
        if (S.IsIsland) PositionIsland();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (S.Pinned || S.IsIsland) return;
        try { DragMove(); } catch { }
        S.Left = (int)Left;
        S.Top = (int)Top;
        S.Save();
    }

    /// <summary>Стан пунктів меню на віджеті та кнопки-шпильки.</summary>
    public void SyncMenu()
    {
        WModeStrip.IsChecked = S.IsIsland;
        WModeFull.IsChecked = !S.IsIsland;
        WPin.IsChecked = S.Pinned;
        WTop.IsChecked = S.Topmost;
        PinBtn.Foreground = Palette.Brush(S.Pinned ? Palette.Green : "#6B7078");
        PinBtn.Opacity = S.Pinned ? 1.0 : 0.7;
        PinBtn.ToolTip = S.Pinned ? L.T("Закріплено — натисни, щоб відкріпити", "Locked — click to unlock") : L.T("Закріпити на місці", "Lock in place");
    }

    public void PositionIsland()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top;
    }

    public void PositionCard()
    {
        if (!LayoutReady) return;
        var wa = SystemParameters.WorkArea;
        if (S.Left is double l && S.Top is double t) { Left = l; Top = t; }
        else { Left = wa.Right - Width - 10; Top = wa.Top + 10; }
    }

    // ================== ПРИСТРОЇ ==================

    public void ShowDevices(DeviceSnapshot? data, IReadOnlyDictionary<string, RazerDevice> known)
    {
        Updated.Text = data is { Devices.Count: > 0 } && data.Time > DateTime.MinValue
            ? L.T("дані Synapse о ", "Synapse data at ") + data.Time.ToString("HH:mm") : "";
        Hint.Text = L.T("Немає активних пристроїв. Увімкни пристрій і переконайся, що Razer Synapse запущений або пристрій підключено через Bluetooth.",
                        "No active devices. Turn a device on and make sure Razer Synapse is running or the device is connected via Bluetooth.");
        Hint.Visibility = known.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var serial in _rows.Keys.Where(k => !known.ContainsKey(k)).ToList())
        {
            Rows.Children.Remove(_rows[serial].Root);
            _rows.Remove(serial);
        }
        foreach (var (serial, d) in known)
        {
            if (!_rows.TryGetValue(serial, out var row))
            {
                _rows[serial] = row = new DeviceRow(d);
                Rows.Children.Add(row.Root);
            }
            row.Update(d, true);
        }

        // розкласти міні-елементи смужки: мишка зліва від годинника, навушники справа
        var ordered = known.Values
            .OrderBy(d => d.Category == "MOUSE" ? 0 : d.Category.Contains("AUDIO") || d.Category.Contains("HEADSET") ? 2 : 1)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        LeftItems.Children.Clear();
        RightItems.Children.Clear();
        int half = (int)Math.Ceiling(ordered.Count / 2.0);
        for (int i = 0; i < ordered.Count; i++)
            (i < half ? LeftItems : RightItems).Children.Add(_rows[ordered[i].Serial].Mini);
        SyncLayout();
    }

    // ================== ВИГЛЯД: СМУЖКА / РОЗКРИТИЙ ==================

    private void Anim(FrameworkElement el, DependencyProperty prop, object to, bool animate)
    {
        if (animate)
        {
            AnimationTimeline a = to is Thickness th
                ? new ThicknessAnimation { To = th, Duration = TimeSpan.FromMilliseconds(AnimMs), EasingFunction = _ease }
                : new DoubleAnimation { To = Convert.ToDouble(to), Duration = TimeSpan.FromMilliseconds(AnimMs), EasingFunction = _ease };
            el.BeginAnimation(prop, a);
        }
        else
        {
            el.BeginAnimation(prop, null);
            el.SetValue(prop, to is Thickness ? to : Convert.ToDouble(to));
        }
    }

    // висота розкритого віджета = висота блоку батареї
    private double GetBatteryHeight()
    {
        BatteryCol.Measure(new Size(230, double.PositiveInfinity));
        return Math.Max(110, Math.Ceiling(BatteryCol.DesiredSize.Height));
    }

    private double GetFullHeight()
    {
        var bh = GetBatteryHeight();
        CenterCol.Height = bh;
        CalCol.Height = bh;
        DetailsContent.Measure(new Size(FullW - 34, double.PositiveInfinity));
        return Math.Ceiling(DetailsContent.DesiredSize.Height);
    }

    private double GetStripWidth()
    {
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        LeftItems.Measure(inf); RightItems.Measure(inf); StripCenter.Measure(inf);
        var side = Math.Max(LeftItems.DesiredSize.Width, RightItems.DesiredSize.Width);
        var w = 2 * side + StripCenter.DesiredSize.Width + 24 + 2;
        double min = MicOnlyMode ? 56 : 150;
        return Math.Max(min, Math.Min(FullW, Math.Ceiling(w)));
    }

    private double GetNoticeWidth()
    {
        NoticeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(200, Math.Ceiling(NoticeText.DesiredSize.Width) + 18 + 10 + 24 + 2 + 40);
    }

    public void ApplyCardBackground() => Card.Background = Palette.Brush(Palette.CardBackground(S.SafeBgOpacity, S.IsIsland));

    public void ApplyVisual(bool expanded, bool animate)
    {
        Expanded = expanded;
        bool island = S.IsIsland;
        if (island)
        {
            // острівець: верхній край зливається з краєм екрана, як Dynamic Island
            double r = expanded ? 22 : NoticeActive ? 18 : 14;
            Card.Margin = new Thickness(14, 0, 14, 14);
            Card.BorderThickness = new Thickness(1, 0, 1, 1);
            Card.CornerRadius = new CornerRadius(0, 0, r, r);
            PinBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            Card.Margin = new Thickness(14);
            Card.BorderThickness = new Thickness(1);
            Card.CornerRadius = new CornerRadius(15);
            PinBtn.Visibility = Visibility.Visible;
        }
        ApplyCardBackground();

        var fullH = GetFullHeight();
        Height = fullH + 24 + 2 + 28 + 6;
        bool isNotice = NoticeActive && !expanded && island;
        double dH = 0, dO = 0, sH = 0, sO = 0, nH = 0, nO = 0, w;
        Thickness pad;
        if (expanded) { dH = fullH; dO = 1; w = FullW; pad = PadFull; }
        else if (isNotice) { nH = NoticeH; nO = 1; w = GetNoticeWidth(); pad = PadStrip; }
        else { sH = StripH; sO = 1; w = GetStripWidth(); pad = PadStrip; }
        _lastTargets = $"{expanded}|{isNotice}|{dH}|{w}";
        Anim(Details, HeightProperty, dH, animate);
        Anim(Details, OpacityProperty, dO, animate);
        Anim(Strip, HeightProperty, sH, animate);
        Anim(Strip, OpacityProperty, sO, animate);
        Anim(Notice, HeightProperty, nH, animate);
        Anim(Notice, OpacityProperty, nO, animate);
        Anim(Card, WidthProperty, w, animate);
        Anim(Card, Border.PaddingProperty, pad, animate);
        if (island) PositionIsland();
    }

    /// <summary>Після кожного оновлення даних: якщо розмір вмісту змінився — плавно підлаштувати.</summary>
    public void SyncLayout()
    {
        if (!LayoutReady) return;
        var bh = GetBatteryHeight();
        if (bh != _lastBatH) { _lastBatH = bh; RenderCalendar(); return; }
        var fullH = GetFullHeight();
        bool isNotice = NoticeActive && !Expanded && S.IsIsland;
        double w = Expanded ? FullW : isNotice ? GetNoticeWidth() : GetStripWidth();
        double dH = Expanded ? fullH : 0;
        if ($"{Expanded}|{isNotice}|{dH}|{w}" != _lastTargets) ApplyVisual(Expanded, true);
    }

    // коротке наведення — острівець вилазить поверх вікон (без фокусу), довше — розкривається
    private static readonly TimeSpan PeekDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ExpandDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(450);

    private void OnPointerTick(object? sender, EventArgs e)
    {
        if (!S.IsIsland || !IsVisible) { _pointerSince = null; SetPeek(false); return; }
        bool inside = Ctx.IsOpen || IsCardMouseOver;
        var now = DateTime.Now;
        if (inside != _pointerInside) { _pointerInside = inside; _pointerSince = now; }
        var held = now - (_pointerSince ?? now);
        if (inside)
        {
            if (held >= PeekDelay) SetPeek(true);
            if (held >= ExpandDelay && !Expanded) ApplyVisual(true, true);
        }
        else if (held >= LeaveDelay)
        {
            if (Expanded) ApplyVisual(false, true);
            SetPeek(false);
        }
    }

    private void SetPeek(bool on)
    {
        if (Peeking == on) return;
        Peeking = on;
        PeekChanged?.Invoke();
    }

    /// <summary>Острівець тимчасово поверх вікон, бо на нього навели мишку.</summary>
    public bool Peeking { get; private set; }
    public event Action? PeekChanged;

    /// <summary>Курсор над карткою — за екранними координатами, тож працює й коли острівець перекритий іншим вікном.</summary>
    public bool IsCardMouseOver
    {
        get
        {
            if (!Card.IsVisible || !Native.GetCursorPos(out var p)) return false;
            var a = Card.PointToScreen(new Point(0, 0));
            var b = Card.PointToScreen(new Point(Card.ActualWidth, Card.ActualHeight));
            return p.X >= a.X && p.X < b.X && p.Y >= Math.Min(a.Y, 0) && p.Y < b.Y;
        }
    }

    // ================== ГОДИННИК І ПОГОДА ==================

    public void UpdateClock()
    {
        var now = DateTime.Now;
        var t = now.ToString("HH:mm");
        StripClock.Text = t;
        HeaderClock.Text = t;
        var d = now.ToString("dddd, d MMMM", L.Culture);
        DateText.Text = char.ToUpper(d[0], L.Culture) + d[1..];
        if (now.Minute != _lastMinute)
        {
            bool first = _lastMinute == -1;
            _lastMinute = now.Minute;
            if (!first) RenderCalendar();
        }
    }

    public void ShowWeather(WeatherNow w)
    {
        WeatherIcon.Text = w.Icon;
        WeatherTemp.Text = $"{w.Temp}°";
        WeatherText.Text = $"{w.Text}, {L.T("відчув.", "feels")} {w.Feels}°";
        WeatherCity.Text = $"{S.WeatherCity} · ↑{w.High}° ↓{w.Low}°";
        // ручні Measure у SyncLayout інакше лишають рядок погоди з розмірами до появи тексту
        UpdateLayout();
        SyncLayout();
    }

    public void ShowWeatherError()
    {
        if (string.IsNullOrEmpty(WeatherTemp.Text)) WeatherText.Text = L.T("Погода недоступна (немає інтернету?)", "Weather unavailable (no internet?)");
    }

    // ================== КАЛЕНДАР ==================

    public void RenderCalendar()
    {
        var cal = _app.Calendar;
        CalRows.Children.Clear();
        if (CalendarService.GetUrls().Count == 0)
        {
            CalHint.Text = L.T("Календар ще не підключено. Права кнопка на іконці в треї → «Налаштування…» → «Календар».",
                               "No calendar connected yet. Right-click the tray icon → Settings… → Calendar.");
            CalHint.Visibility = Visibility.Visible;
            SyncLayout();
            return;
        }
        var now = DateTime.Now;
        var shown = cal.AllEvents
            .Where(e => e.End > now || (e.AllDay && e.Start.Date >= now.Date))
            .OrderBy(e => e.Start).ThenBy(e => !e.AllDay)
            .Take(7).ToList();
        if (shown.Count == 0)
        {
            CalHint.Text = cal.LastError && cal.LoadedCount == 0 ? L.T("Не вдалося завантажити календар. Перевір інтернет або посилання.", "Could not load the calendar. Check your internet connection or the link.")
                : cal.LoadedCount == 0 ? L.T("Завантаження…", "Loading…")
                : L.T("На найближчий тиждень подій немає.", "No events in the coming week.");
            CalHint.Visibility = Visibility.Visible;
            SyncLayout();
            return;
        }
        CalHint.Visibility = Visibility.Collapsed;
        DateTime? lastDay = null;
        foreach (var ev in shown)
        {
            var day = ev.Start.Date;
            if (day < now.Date) day = now.Date;
            if (day != lastDay)
            {
                lastDay = day;
                var label = day == now.Date ? L.T("Сьогодні", "Today") : day == now.Date.AddDays(1) ? L.T("Завтра", "Tomorrow") : day.ToString("dddd", L.Culture);
                CalRows.Children.Add(new TextBlock
                {
                    Text = $"{label} · {day.ToString("d MMMM", L.Culture)}",
                    Foreground = Palette.Brush("#A9ADB5"), FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 6, 0, 2),
                });
            }
            CalRows.Children.Add(MakeEventRow(ev, now, S.CalendarColors.GetValueOrDefault(ev.Calendar, Palette.Accent)));
        }
        FitCalendar();
        SyncLayout();
    }

    private static Grid MakeEventRow(CalendarEvent ev, DateTime now, string color)
    {
        bool isNow = !ev.AllDay && ev.Start <= now && ev.End > now;
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        // смужка — колір календаря; подію «зараз» позначає зелений текст під назвою
        g.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Palette.Brush(color) });

        var time = ev.AllDay ? L.T("Увесь день", "All day") : $"{ev.Start:HH:mm}–{ev.End:HH:mm}";
        if (isNow) time = L.T("Зараз · до ", "Now · until ") + ev.End.ToString("HH:mm");
        if (ev.Location.Length > 0) time += " · " + ev.Location;

        var sp = new StackPanel { Margin = new Thickness(9, 0, 0, 0) };
        Grid.SetColumn(sp, 1);
        sp.Children.Add(new TextBlock
        {
            Text = ev.Title, Foreground = Palette.Brush("#F2F3F5"), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        sp.Children.Add(new TextBlock
        {
            Text = time, Foreground = Palette.Brush(isNow ? Palette.Green : "#8A8F98"), FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        g.Children.Add(sp);
        return g;
    }

    // прибрати події, що не влазять у висоту блоку батареї
    private void FitCalendar()
    {
        var avail = GetBatteryHeight() - 18;
        var sz = new Size(260, double.PositiveInfinity);
        CalRows.Measure(sz);
        while (CalRows.Children.Count > 2 && CalRows.DesiredSize.Height > avail)
        {
            CalRows.Children.RemoveAt(CalRows.Children.Count - 1);
            if (CalRows.Children[^1] is TextBlock) CalRows.Children.RemoveAt(CalRows.Children.Count - 1);
            CalRows.Measure(sz);
        }
    }

    // ================== МІКРОФОН ==================

    public void SetMicOnly(bool only)
    {
        MicOnlyMode = only;
        if (only)
        {
            MicBadgeHost.Child = Glyphs.MicIcon(true, 14, Palette.Red);
            MicBadgeHost.Visibility = Visibility.Visible;
            MicBadgeHost.Margin = new Thickness(0);
        }
        else
        {
            MicBadgeHost.Child = null;
            MicBadgeHost.Visibility = Visibility.Collapsed;
        }
        var vis = only ? Visibility.Collapsed : Visibility.Visible;
        LeftItems.Visibility = vis; RightItems.Visibility = vis; StripClock.Visibility = vis;
        StripCenter.Margin = only ? new Thickness(0) : new Thickness(14, 0, 14, 0);
        SyncLayout();
    }

    public void SetNotice(string text, string color, FrameworkElement icon)
    {
        NoticeText.Text = text;
        NoticeText.Foreground = Palette.Brush(color);
        NoticeIconHost.Child = icon;
    }

    public Border CardBorder => Card;
}
