using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BybitTaskbar.Core;

/// <summary>Настройки приложения. Секрет хранится зашифрованным через DPAPI (только для текущего пользователя).</summary>
public sealed class AppConfig
{
    public string ApiKey { get; set; } = "";

    /// <summary>Base64 от DPAPI-блоба, а не сам секрет.</summary>
    public string ApiSecretProtected { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.bybit.com";

    /// <summary>UNIFIED | SPOT | CONTRACT</summary>
    public string AccountType { get; set; } = "UNIFIED";

    /// <summary>Equity | Wallet | Available</summary>
    public string Metric { get; set; } = "Equity";

    public int RefreshSeconds { get; set; } = 60;

    public bool HideOnFullscreen { get; set; } = true;

    public bool AutoStart { get; set; }

    public bool ShowCoinList { get; set; } = true;

    /// <summary>Позиция окна в физических пикселях экрана (левый верхний угол).</summary>
    public int? WinX { get; set; }
    public int? WinY { get; set; }

    [JsonIgnore]
    public string ApiSecret
    {
        get => Dpapi.TryUnprotect(ApiSecretProtected) ?? "";
        set => ApiSecretProtected = string.IsNullOrEmpty(value) ? "" : Dpapi.Protect(value);
    }

    [JsonIgnore]
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BybitTaskbar");

    private static string FilePath => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("config load failed: " + ex);
        }

        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            AppLog.Write("config save failed: " + ex);
        }
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)) BaseUrl = "https://api.bybit.com";
        BaseUrl = BaseUrl.Trim().TrimEnd('/');
        if (!BaseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            BaseUrl = "https://" + BaseUrl;

        AccountType = (AccountType ?? "UNIFIED").Trim().ToUpperInvariant();
        if (AccountType is not ("UNIFIED" or "SPOT" or "CONTRACT"))
            AccountType = "UNIFIED";

        if (Metric is not ("Wallet" or "Available")) Metric = "Equity";

        RefreshSeconds = Math.Clamp(RefreshSeconds, 15, 3600);
        ApiKey = (ApiKey ?? "").Trim();
    }

    public AppConfig Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
