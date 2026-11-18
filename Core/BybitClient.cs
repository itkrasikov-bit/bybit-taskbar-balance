using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BybitTaskbar.Core;

public sealed class CoinBalance
{
    public string Coin { get; init; } = "";
    public decimal WalletBalance { get; init; }
    public decimal UsdValue { get; init; }
    public decimal Equity { get; init; }
    public decimal UnrealisedPnl { get; init; }
}

public sealed class WalletSnapshot
{
    public string AccountType { get; init; } = "";
    public decimal Equity { get; init; }
    public decimal WalletBalance { get; init; }
    public decimal MarginBalance { get; init; }
    public decimal Available { get; init; }
    public decimal UnrealisedPnl { get; init; }
    public List<CoinBalance> Coins { get; init; } = new();
    public DateTime UpdatedAt { get; init; } = DateTime.Now;

    public decimal ForMetric(string metric) => metric switch
    {
        "Wallet" => WalletBalance,
        "Available" => Available,
        _ => Equity
    };
}

public sealed class BybitApiException : Exception
{
    public int RetCode { get; }
    public string RetMsg { get; }

    public BybitApiException(int retCode, string retMsg)
        : base($"Bybit API {retCode}: {retMsg}")
    {
        RetCode = retCode;
        RetMsg = retMsg;
    }
}

/// <summary>Клиент Bybit v5 (HMAC-SHA256, приватный запрос wallet-balance).</summary>
public sealed class BybitClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _apiKey;
    private readonly string _secret;
    private readonly string _baseUrl;
    private long _clockOffsetMs;

    public BybitClient(string apiKey, string secret, string baseUrl)
    {
        _apiKey = (apiKey ?? "").Trim();
        _secret = (secret ?? "").Trim();
        _baseUrl = (baseUrl ?? "https://api.bybit.com").Trim().TrimEnd('/');
    }

    public string BaseUrl => _baseUrl;

    public async Task<WalletSnapshot> GetWalletAsync(string accountType, CancellationToken ct = default)
    {
        try
        {
            return await SendWalletAsync(accountType, ct).ConfigureAwait(false);
        }
        catch (BybitApiException ex) when (ex.RetCode == 10002 || ex.RetCode == 10004)
        {
            // рассинхрон часов — подтягиваем время сервера и пробуем ещё раз
            await SyncClockAsync(ct).ConfigureAwait(false);
            return await SendWalletAsync(accountType, ct).ConfigureAwait(false);
        }
    }

    private async Task<WalletSnapshot> SendWalletAsync(string accountType, CancellationToken ct)
    {
        var qs = "accountType=" + Uri.EscapeDataString(accountType);
        var request = BuildRequest("/v5/account/wallet-balance", qs);

        using (request)
        using (var resp = await _http.SendAsync(request, ct).ConfigureAwait(false))
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;

            if (string.IsNullOrWhiteSpace(body))
                throw new BybitApiException(-status, HttpStatusText(status));

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new BybitApiException(-status, $"неожиданный ответ сервера (HTTP {status})");
            }

            using (doc)
            {
                var root = doc.RootElement;

                var code = root.TryGetProperty("retCode", out var rc) && rc.ValueKind == JsonValueKind.Number
                    ? rc.GetInt32()
                    : -1;
                var msg = root.TryGetProperty("retMsg", out var rm) && rm.ValueKind == JsonValueKind.String
                    ? rm.GetString() ?? ""
                    : "";

                if (code != 0)
                    throw new BybitApiException(code, string.IsNullOrWhiteSpace(msg) ? $"HTTP {status}" : msg);

                if (!root.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("list", out var list) ||
                    list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                    throw new BybitApiException(-1, "в ответе нет данных о кошельке");

                return ParseWallet(list[0], accountType);
            }
        }
    }

    private static string HttpStatusText(int status) => status switch
    {
        401 => "ключ недействителен (HTTP 401)",
        403 => "доступ запрещён (HTTP 403) — возможно, ограничение по региону или IP",
        404 => "адрес API не найден (HTTP 404) — проверьте домен",
        429 => "превышен лимит запросов (HTTP 429)",
        >= 500 => $"сервер Bybit недоступен (HTTP {status})",
        _ => $"HTTP {status}"
    };

    private static WalletSnapshot ParseWallet(JsonElement acc, string accountType)
    {
        var coins = new List<CoinBalance>();

        if (acc.TryGetProperty("coin", out var coinArr) && coinArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in coinArr.EnumerateArray())
            {
                var cb = new CoinBalance
                {
                    Coin = Str(c, "coin"),
                    WalletBalance = Dec(c, "walletBalance"),
                    UsdValue = Dec(c, "usdValue"),
                    Equity = Dec(c, "equity"),
                    UnrealisedPnl = Dec(c, "unrealisedPnl")
                };
                if (Math.Abs(cb.UsdValue) >= 0.5m || Math.Abs(cb.WalletBalance) > 0m)
                    coins.Add(cb);
            }
        }

        coins.Sort((a, b) => Math.Abs(b.UsdValue).CompareTo(Math.Abs(a.UsdValue)));

        var equity = Has(acc, "totalEquity") ? Dec(acc, "totalEquity") : coins.Sum(c => c.UsdValue);
        var wallet = Has(acc, "totalWalletBalance") ? Dec(acc, "totalWalletBalance") : coins.Sum(c => c.UsdValue);

        return new WalletSnapshot
        {
            AccountType = Str(acc, "accountType") is { Length: > 0 } at ? at : accountType,
            Equity = equity,
            WalletBalance = wallet,
            MarginBalance = Dec(acc, "totalMarginBalance"),
            Available = Dec(acc, "totalAvailableBalance"),
            UnrealisedPnl = Dec(acc, "totalPerpUPL"),
            Coins = coins,
            UpdatedAt = DateTime.Now
        };
    }

    private HttpRequestMessage BuildRequest(string path, string query)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _clockOffsetMs;
        const string recv = "20000";
        var tsStr = ts.ToString(CultureInfo.InvariantCulture);
        var payload = tsStr + _apiKey + recv + query;
        var sign = Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(_secret), Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();

        var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path + "?" + query);
        req.Headers.TryAddWithoutValidation("X-BAPI-API-KEY", _apiKey);
        req.Headers.TryAddWithoutValidation("X-BAPI-TIMESTAMP", tsStr);
        req.Headers.TryAddWithoutValidation("X-BAPI-RECV-WINDOW", recv);
        req.Headers.TryAddWithoutValidation("X-BAPI-SIGN", sign);
        req.Headers.TryAddWithoutValidation("X-BAPI-SIGN-TYPE", "2");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "BybitTaskbar/1.0 (Windows)");
        return req;
    }

    private async Task SyncClockAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/v5/market/time");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number)
            {
                var server = t.GetInt64();
                _clockOffsetMs = server - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                AppLog.Write($"clock offset set to {_clockOffsetMs} ms");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("clock sync failed: " + ex.Message);
        }
    }

    private static bool Has(JsonElement e, string name) => e.TryGetProperty(name, out _);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static decimal Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0m;
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                return decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
            case JsonValueKind.Number:
                return v.TryGetDecimal(out var n) ? n : 0m;
            default:
                return 0m;
        }
    }

    public void Dispose() => _http.Dispose();
}
