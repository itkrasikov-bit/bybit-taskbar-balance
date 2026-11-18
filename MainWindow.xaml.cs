using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BybitTaskbar.Core;

namespace BybitTaskbar;

public partial class MainWindow : Window
{
    private readonly bool _demo;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _watch = new();

    private BybitClient? _client;
    private string _secret = "";
    private WalletSnapshot? _last;
    private string? _error;
    private bool _errorIsNetwork;
    private bool _loading;
    private bool _refreshing;
    private bool _noCreds;
    private bool _manualHidden;
    private bool _firstRunShown;
    private DateTime _lastSuccessUtc = DateTime.MinValue;
    private DateTime _popupClosedUtc = DateTime.MinValue;

    private bool _dragging;
    private bool _moved;
    private Point _lastScreen;

    private bool _docked = true;
    private int? _dockedRightPx;
    private int? _freeRightPx;
    private int _freeTopPx;
    private bool _positioned;
    private bool _exiting;

    private static AppConfig C => App.Config;

    public MainWindow(bool demo)
    {
        _demo = demo;
        InitializeComponent();

        _timer.Tick += async (_, _) => await RefreshAsync();
        _watch.Interval = TimeSpan.FromMilliseconds(800);
        _watch.Tick += (_, _) => Watch();

        Loaded += OnLoaded;
        SizeChanged += (_, _) => RepositionNow();
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyConfig();
        RestorePosition();

        if (_demo)
        {
            _last = DemoSnapshot();
            _lastSuccessUtc = DateTime.Now;
        }

        _ = RefreshAsync();
        _timer.Start();
        _watch.Start();
        _positioned = true;
        UpdateUi();

        if (!_demo && !C.HasCredentials && !_firstRunShown)
        {
            _firstRunShown = true;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                OpenSettings();
            };
            t.Start();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 не должен убивать виджет: просто прячем его,
        // вернуть можно повторным запуском ярлыка.
        if (!_exiting)
        {
            e.Cancel = true;
            _manualHidden = true;
            DetailsPopup.IsOpen = false;
            MenuPopup.IsOpen = false;
            Hide();
            return;
        }

        _timer.Stop();
        _watch.Stop();
        try
        {
            C.Save();
        }
        catch
        {
            // ignore
        }
    }

    // ───────────────────────────── конфиг / клиент ─────────────────────────────

    private void ApplyConfig()
    {
        C.Normalize();
        _secret = C.ApiSecret;
        _client?.Dispose();
        _client = null;

        if (!_demo && C.HasCredentials)
            _client = new BybitClient(C.ApiKey, _secret, C.BaseUrl);

        // Автозапуск переписываем при каждом старте: путь к exe мог измениться.
        if (C.AutoStart) StartupReg.Apply(true);

        _timer.Interval = TimeSpan.FromSeconds(C.RefreshSeconds);
    }

    // ───────────────────────────── загрузка данных ─────────────────────────────

    private async Task RefreshAsync()
    {
        if (_refreshing) return;

        if (_demo)
        {
            _last = DemoSnapshot();
            _lastSuccessUtc = DateTime.Now;
            _error = null;
            UpdateUi();
            return;
        }

        if (!C.HasCredentials)
        {
            _noCreds = true;
            _error = null;
            _last = null;
            UpdateUi();
            return;
        }

        _noCreds = false;
        _refreshing = true;
        _loading = true;
        UpdateUi();

        try
        {
            _client ??= new BybitClient(C.ApiKey, _secret, C.BaseUrl);
            var snap = await _client.GetWalletAsync(C.AccountType);
            _last = snap;
            _error = null;
            _errorIsNetwork = false;
            _lastSuccessUtc = DateTime.Now;
        }
        catch (BybitApiException ex)
        {
            _error = ex.RetMsg;
            _errorIsNetwork = false;
            AppLog.Write("bybit error: " + ex);
        }
        catch (Exception ex)
        {
            _errorIsNetwork = true;
            _error = ex is HttpRequestException
                ? "нет связи с Bybit"
                : ex.Message;
            AppLog.Write("refresh failed: " + ex);
        }
        finally
        {
            _refreshing = false;
            _loading = false;
        }

        UpdateUi();
    }

    // ───────────────────────────── отрисовка ─────────────────────────────

    private void UpdateUi()
    {
        string text;
        var dotColor = System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99);
        string tip;

        if (_noCreds)
        {
            text = "настроить";
            dotColor = System.Windows.Media.Color.FromRgb(0xF5, 0xA6, 0x23);
            tip = "Bybit: API-ключ не задан.\nНажмите, чтобы настроить.";
        }
        else if (_last is null && _loading)
        {
            text = "…";
            dotColor = System.Windows.Media.Color.FromRgb(0x5B, 0x8D, 0xEF);
            tip = "Bybit: загрузка…";
        }
        else if (_last is null && _error is not null)
        {
            text = _errorIsNetwork ? "нет связи" : "ошибка";
            dotColor = System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71);
            tip = "Bybit: " + _error;
        }
        else if (_last is not null)
        {
            text = Fmt.MoneyCompact(_last.ForMetric(C.Metric));
            if (_error is not null)
            {
                dotColor = System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71);
                tip = $"Bybit · {C.AccountType}\n{text}\nОбновление не удалось: {_error}\nДанные от {_last.UpdatedAt:HH:mm}";
            }
            else
            {
                tip = $"Bybit · {C.AccountType}\n{text}\nОбновлено {_last.UpdatedAt:HH:mm:ss}";
            }
        }
        else
        {
            text = "—";
            tip = "Bybit";
        }

        if (ChipText.Text != text)
            ChipText.Text = text;

        Dot.Fill = new SolidColorBrush(dotColor);
        Chip.ToolTip = tip;

        UpdateDetails();
    }

    private void UpdateDetails()
    {
        HdrAccount.Text = "BYBIT · " + C.AccountType;
        HdrUpdated.Text = _last is null
            ? (_error is null ? "—" : "ошибка")
            : "обновлено " + _last.UpdatedAt.ToString("HH:mm");

        if (_last is null)
        {
            BigValue.Text = _error is null ? "—" : "нет данных";
            WalletValue.Text = "—";
            AvailValue.Text = "—";
            PnlValue.Text = "—";
            PnlValue.Foreground = (Brush)FindResource("TextDim");
            CoinsPanel.Children.Clear();
            CoinsSep.Visibility = Visibility.Collapsed;
        }
        else
        {
            BigValue.Text = Fmt.Money(_last.ForMetric(C.Metric));
            WalletValue.Text = Fmt.Money(_last.WalletBalance);
            AvailValue.Text = Fmt.Money(_last.Available);
            PnlValue.Text = (_last.UnrealisedPnl > 0 ? "+" : "") + Fmt.Money(_last.UnrealisedPnl);
            PnlValue.Foreground = _last.UnrealisedPnl switch
            {
                > 0 => (Brush)FindResource("Good"),
                < 0 => (Brush)FindResource("Bad"),
                _ => (Brush)FindResource("TextDim")
            };

            CoinsPanel.Children.Clear();
            if (C.ShowCoinList && _last.Coins.Count > 0)
            {
                CoinsSep.Visibility = Visibility.Visible;
                foreach (var c in _last.Coins.Take(4))
                {
                    var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var name = new TextBlock
                    {
                        Text = c.Coin,
                        FontSize = 11,
                        Foreground = (Brush)FindResource("TextDim"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var value = new TextBlock
                    {
                        Text = Fmt.Money(c.UsdValue),
                        FontSize = 11,
                        Foreground = (Brush)FindResource("TextPrimary"),
                        HorizontalAlignment = HorizontalAlignment.Right
                    };
                    Grid.SetColumn(name, 0);
                    Grid.SetColumn(value, 1);
                    grid.Children.Add(name);
                    grid.Children.Add(value);
                    CoinsPanel.Children.Add(grid);
                }
            }
            else
            {
                CoinsSep.Visibility = Visibility.Collapsed;
            }
        }

        if (_error is null)
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorText.Text = "⚠ " + _error;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    // ───────────────────────────── позиционирование ─────────────────────────────

    private (double x, double y) Dpi()
    {
        var d = VisualTreeHelper.GetDpi(this);
        return (d.DpiScaleX <= 0 ? 1 : d.DpiScaleX, d.DpiScaleY <= 0 ? 1 : d.DpiScaleY);
    }

    private void SetPhysical(int x, int y)
    {
        var (sx, sy) = Dpi();
        Left = x / sx;
        Top = y / sy;
    }

    private System.Drawing.Rectangle GetRectPhysical()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!Interop.GetWindowRect(hwnd, out var r)) return System.Drawing.Rectangle.Empty;
        return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private void RestorePosition()
    {
        UpdateLayout();

        if (C.WinX.HasValue && C.WinY.HasValue && OnScreen(C.WinX.Value, C.WinY.Value))
        {
            _docked = false;
            SetPhysical(C.WinX.Value, C.WinY.Value);
            var (sx, _) = Dpi();
            _freeRightPx = C.WinX.Value + (int)Math.Round(ActualWidth * sx);
            _freeTopPx = C.WinY.Value;
            return;
        }

        _docked = true;
        PlaceDocked();
    }

    private bool OnScreen(int x, int y)
    {
        var screen = TaskbarGeometry.GetVirtualScreen();
        return x > screen.Left - 50 && y > screen.Top - 50 &&
               x < screen.Right - 20 && y < screen.Bottom - 10;
    }

    private void PlaceDocked()
    {
        UpdateLayout();
        var (sx, sy) = Dpi();
        var w = (int)Math.Round(ActualWidth * sx);
        var h = (int)Math.Round(ActualHeight * sy);
        var screen = TaskbarGeometry.GetVirtualScreen();
        var tb = TaskbarGeometry.GetTaskbarRect();
        var edge = TaskbarGeometry.GetEdge();

        int x, y;

        if (tb is null || edge == TaskbarEdge.Unknown || tb.Value.Height < 10 || tb.Value.Width < 10)
        {
            // Панель задач скрыта/автоскрытие — встаём у нижнего правого угла.
            x = screen.Right - w - 12;
            y = screen.Bottom - h - 8;
        }
        else
        {
            var t = tb.Value;
            var tray = TaskbarGeometry.GetTrayRect();
            switch (edge)
            {
                case TaskbarEdge.Bottom:
                case TaskbarEdge.Top:
                    y = t.Top + (t.Height - h) / 2;
                    x = _dockedRightPx.HasValue
                        ? _dockedRightPx.Value - w
                        : (tray?.Left ?? screen.Right - 260) - w - 10;
                    break;
                default:
                    x = t.Left + (t.Width - w) / 2;
                    y = _dockedRightPx.HasValue
                        ? _dockedRightPx.Value - h
                        : (tray?.Top ?? screen.Bottom - 200) - h - 10;
                    break;
            }
        }

        _dockedRightPx ??= x + w;
        SetPhysical(x, y);
    }

    private void RepositionNow()
    {
        if (!_positioned) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            UpdateLayout();
            var (sx, sy) = Dpi();
            var w = (int)Math.Round(ActualWidth * sx);
            var h = (int)Math.Round(ActualHeight * sy);

            if (_docked)
            {
                PlaceDocked();
            }
            else if (_freeRightPx.HasValue)
            {
                SetPhysical(_freeRightPx.Value - w, _freeTopPx);
            }
        }));
    }

    private void SavePosition()
    {
        var r = GetRectPhysical();

        if (_docked)
        {
            C.WinX = null;
            C.WinY = null;
            _dockedRightPx = r.Right;
        }
        else
        {
            C.WinX = r.Left;
            C.WinY = r.Top;
            _freeRightPx = r.Right;
            _freeTopPx = r.Top;
        }

        C.Save();
    }

    // ───────────────────────────── мышь ─────────────────────────────

    private void Chip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        _dragging = true;
        _moved = false;
        _lastScreen = PointToScreen(e.GetPosition(this));
        Chip.CaptureMouse();
        e.Handled = true;
    }

    private void Chip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var cur = PointToScreen(e.GetPosition(this));
        var dx = cur.X - _lastScreen.X;
        var dy = cur.Y - _lastScreen.Y;

        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 3) return;

        _moved = true;
        _docked = false;

        var (sx, sy) = Dpi();
        Left += dx / sx;
        Top += dy / sy;
        _lastScreen = cur;
    }

    private void Chip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();

        if (_moved)
        {
            var rect = GetRectPhysical();
            var tb = TaskbarGeometry.GetTaskbarRect();

            if (tb.HasValue && rect.IntersectsWith(tb.Value) && rect.Height > 0)
            {
                _docked = true;
                RepositionNow();
            }
            else
            {
                _docked = false;
            }

            SavePosition();
        }
        else
        {
            ToggleDetails();
        }

        e.Handled = true;
    }

    private void Chip_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        DetailsPopup.IsOpen = false;
        MenuPopup.IsOpen = true;
        e.Handled = true;
    }

    private void ToggleDetails()
    {
        if (DetailsPopup.IsOpen)
        {
            DetailsPopup.IsOpen = false;
            return;
        }

        if ((DateTime.UtcNow - _popupClosedUtc).TotalMilliseconds < 250)
            return; // попап только что закрылся этим же кликом

        MenuPopup.IsOpen = false;
        UpdateDetails();

        // Открываем на следующем проходе диспетчера: иначе popup получает захват мыши
        // ещё внутри обработки клика и тут же закрывается от этого же клика.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            UpdateDetails();
            DetailsPopup.IsOpen = true;
        }));

        if (DateTime.Now - _lastSuccessUtc > TimeSpan.FromSeconds(20))
            _ = RefreshAsync();
    }

    private void DetailsPopup_Closed(object? sender, EventArgs e) => _popupClosedUtc = DateTime.UtcNow;

    // ───────────────────────────── полноэкранный режим ─────────────────────────────

    private void Watch()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();

        if (C.HideOnFullscreen)
        {
            if (TaskbarGeometry.ForegroundIsFullscreen(hwnd))
            {
                if (IsVisible)
                {
                    DetailsPopup.IsOpen = false;
                    MenuPopup.IsOpen = false;
                    Hide();
                }

                return;
            }

            if (!_manualHidden && !IsVisible)
            {
                Show();
                Topmost = true;
            }
        }

        // Панель задач — тоже topmost: при клике по ней она поднимается выше нашего
        // окна и закрывает чип. Проверяем, не перекрыт ли центр чипа, и возвращаем
        // себя наверх (без активации).
        if (IsVisible && !_manualHidden)
        {
            var r = GetRectPhysical();
            if (r.Width > 0 &&
                TaskbarGeometry.IsCoveredAt(hwnd, r.Left + r.Width / 2, r.Top + r.Height / 2))
                RaiseAboveTaskbar();
        }
    }

    private void RaiseAboveTaskbar()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Interop.SetWindowPos(hwnd, Interop.HWND_TOPMOST, 0, 0, 0, 0,
            Interop.SWP_NOMOVE | Interop.SWP_NOSIZE | Interop.SWP_NOACTIVATE);
    }

    public void Reveal()
    {
        _manualHidden = false;

        if (!IsVisible) Show();
        UpdateUi();

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Interop.SetWindowPos(hwnd, Interop.HWND_TOPMOST, 0, 0, 0, 0,
            Interop.SWP_NOMOVE | Interop.SWP_NOSIZE | Interop.SWP_NOACTIVATE | Interop.SWP_SHOWWINDOW);

        if (!C.HasCredentials && !_demo)
            OpenSettings();
    }

    // ───────────────────────────── меню и настройки ─────────────────────────────

    private void MenuDetails_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        DetailsPopup.IsOpen = true;
        UpdateDetails();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await RefreshAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        DetailsPopup.IsOpen = false;
        OpenSettings();
    }

    private void MenuOpenBybit_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        try
        {
            Process.Start(new ProcessStartInfo("https://www.bybit.com/user/assets/home/overview")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Write("open bybit failed: " + ex.Message);
        }
    }

    private void MenuResetPosition_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _docked = true;
        _dockedRightPx = null;
        _freeRightPx = null;
        C.WinX = null;
        C.WinY = null;
        PlaceDocked();
        SavePosition();
    }

    private void MenuHide_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _manualHidden = true;
        DetailsPopup.IsOpen = false;
        Hide();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        DetailsPopup.IsOpen = false;
        _exiting = true;
        C.Save();
        Application.Current.Shutdown();
    }

    private void OpenSettings()
    {
        var draft = C.Clone();
        var dlg = new SettingsWindow(draft, _demo)
        {
            Owner = this
        };

        if (dlg.ShowDialog() == true && dlg.SavedConfig is not null)
        {
            App.Config = dlg.SavedConfig;
            ApplyConfig();
            _last = null;
            _error = null;
            UpdateUi();
            _ = RefreshAsync();
        }
    }

    // ───────────────────────────── демо-данные ─────────────────────────────

    private static WalletSnapshot DemoSnapshot() => new()
    {
        AccountType = "UNIFIED",
        Equity = 12483.57m,
        WalletBalance = 11800.42m,
        MarginBalance = 12483.57m,
        Available = 9200.10m,
        UnrealisedPnl = 683.15m,
        UpdatedAt = DateTime.Now,
        Coins = new List<CoinBalance>
        {
            new() { Coin = "USDT", WalletBalance = 9800.12m, UsdValue = 9800.12m, Equity = 9800.12m },
            new() { Coin = "BTC", WalletBalance = 0.0211m, UsdValue = 2103.30m, Equity = 2103.30m },
            new() { Coin = "ETH", WalletBalance = 0.2m, UsdValue = 580.15m, Equity = 580.15m }
        }
    };
}
