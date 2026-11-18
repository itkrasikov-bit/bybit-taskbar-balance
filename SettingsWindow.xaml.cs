using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BybitTaskbar.Core;

namespace BybitTaskbar;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _draft;
    private bool _busy;

    public AppConfig? SavedConfig { get; private set; }

    public SettingsWindow(AppConfig draft, bool demo = false)
    {
        _draft = draft;
        InitializeComponent();
        LoadFromDraft();

        if (demo)
            SetStatus("Демо-режим: реальные данные не запрашиваются.");

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void LoadFromDraft()
    {
        ApiKeyBox.Text = _draft.ApiKey;
        SecretBox.Password = _draft.ApiSecret;
        BaseUrlBox.Text = _draft.BaseUrl;

        SelectByTag(AccountCombo, _draft.AccountType);
        SelectByTag(MetricCombo, _draft.Metric);
        SelectByTag(RefreshCombo, _draft.RefreshSeconds.ToString());

        ShowCoinsCheck.IsChecked = _draft.ShowCoinList;
        HideFullscreenCheck.IsChecked = _draft.HideOnFullscreen;
        AutoStartCheck.IsChecked = _draft.AutoStart || StartupReg.IsEnabled();
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = ci;
                return;
            }
        }

        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private AppConfig Collect()
    {
        var cfg = _draft;
        cfg.ApiKey = ApiKeyBox.Text.Trim();
        cfg.ApiSecret = SecretBox.Password.Trim();
        cfg.BaseUrl = BaseUrlBox.Text.Trim();
        cfg.AccountType = TagOf(AccountCombo);
        cfg.Metric = TagOf(MetricCombo);
        if (int.TryParse(TagOf(RefreshCombo), out var seconds)) cfg.RefreshSeconds = seconds;
        cfg.ShowCoinList = ShowCoinsCheck.IsChecked == true;
        cfg.HideOnFullscreen = HideFullscreenCheck.IsChecked == true;
        cfg.AutoStart = AutoStartCheck.IsChecked == true;
        cfg.Normalize();
        return cfg;
    }

    private void SetStatus(string text, bool error = false, bool ok = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error
            ? System.Windows.Media.Brushes.Salmon
            : ok
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x93, 0xA3));
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var cfg = Collect();
        if (!cfg.HasCredentials)
        {
            SetStatus("Сначала укажите API-ключ и секрет.", error: true);
            return;
        }

        _busy = true;
        TestBtn.IsEnabled = false;
        SetStatus("Проверяю подключение к " + cfg.BaseUrl + " …");

        try
        {
            using var client = new BybitClient(cfg.ApiKey, cfg.ApiSecret, cfg.BaseUrl);
            var snap = await client.GetWalletAsync(cfg.AccountType);
            SetStatus(
                $"Подключение работает. {snap.AccountType}: капитал {Fmt.Money(snap.Equity)}, " +
                $"кошелёк {Fmt.Money(snap.WalletBalance)}, доступно {Fmt.Money(snap.Available)}.",
                ok: true);
        }
        catch (BybitApiException ex)
        {
            SetStatus("Bybit ответил: " + Explain(ex.RetCode, ex.RetMsg), error: true);
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось связаться: " + ex.Message, error: true);
        }
        finally
        {
            _busy = false;
            TestBtn.IsEnabled = true;
        }
    }

    private static string Explain(int code, string msg) => code switch
    {
        10003 => "неверный API-ключ (10003). Проверьте ключ и адрес API.",
        10004 => "неверная подпись (10004). Проверьте секрет, часы Windows и адрес API.",
        10002 => "запрос просрочен (10002) — сбиты часы системы.",
        33004 => "срок действия ключа истёк (33004).",
        10005 => "ключу не хватает прав (10005). Нужно право чтения аккаунта.",
        401 => "ключ недействителен (HTTP 401).",
        < 0 => msg,
        _ => $"{code}: {msg}"
    };

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = Collect();

        if (cfg.ApiKey.Length > 0 && cfg.ApiSecret.Length == 0)
        {
            SetStatus("API-ключ указан, а секрет пустой — вставьте секрет ключа.", error: true);
            return;
        }

        cfg.Save();
        StartupReg.Apply(cfg.AutoStart);
        SavedConfig = cfg;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OpenApiPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.bybit.com/app/user/api-management")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Write("open api page failed: " + ex.Message);
        }
    }
}
