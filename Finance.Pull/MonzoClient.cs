using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Finance.Pull;

public sealed record MonzoAccount(string Id, string Label);

public sealed record MonzoTx(
    string Id, DateTimeOffset Created, string Name, string Emoji, string Category, string Scheme,
    long AmountMinor, string Currency, long LocalAmountMinor, string LocalCurrency,
    string Notes, string Address, string Description, bool Settled);

public sealed class MonzoAuthException(string message) : Exception(message);

public sealed class MonzoClient
{
    private const string Api = "https://api.monzo.com";
    private readonly HttpClient _http = new();
    private readonly MonzoSecrets _s;

    public MonzoClient(MonzoSecrets secrets) => _s = secrets;

    // ---- OAuth ----
    public static string AuthUrl(string clientId, string redirect, string state) =>
        $"https://auth.monzo.com/?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}&response_type=code&state={state}";

    public static async Task<MonzoSecrets> ExchangeCodeAsync(string clientId, string clientSecret, string redirect, string code)
    {
        using var http = new HttpClient();
        var res = await http.PostAsync($"{Api}/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirect, ["code"] = code,
        }));
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new MonzoAuthException($"Token exchange failed ({(int)res.StatusCode}): {body}");
        return FromTokenResponse(new MonzoSecrets { ClientId = clientId, ClientSecret = clientSecret }, JsonNode.Parse(body)!);
    }

    private static MonzoSecrets FromTokenResponse(MonzoSecrets s, JsonNode j)
    {
        s.AccessToken = (string)j["access_token"]!;
        // Refresh tokens rotate: always keep the newest one. Only confidential clients receive one.
        s.RefreshToken = (string?)j["refresh_token"] ?? s.RefreshToken;
        s.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds((int)j["expires_in"]! - 60);
        return s;
    }

    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_s.RefreshToken))
            throw new MonzoAuthException("No refresh token. Run `auth` again (the Monzo client must be Confidential).");
        var res = await _http.PostAsync($"{Api}/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = _s.ClientId, ["client_secret"] = _s.ClientSecret,
            ["refresh_token"] = _s.RefreshToken,
        }));
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new MonzoAuthException($"Refresh failed ({(int)res.StatusCode}). Run `auth` again. {body}");
        FromTokenResponse(_s, JsonNode.Parse(body)!);
        TokenStore.Save(_s); // persist the rotated refresh token immediately
    }

    // ---- API ----
    private async Task<JsonNode> GetAsync(string pathAndQuery)
    {
        if (DateTimeOffset.UtcNow >= _s.AccessTokenExpiresAt) await RefreshAsync();
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Api + pathAndQuery);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _s.AccessToken);
            var res = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode) return JsonNode.Parse(body)!;
            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { await RefreshAsync(); continue; }
            if (res.StatusCode == HttpStatusCode.Forbidden && body.Contains("insufficient_permissions"))
                throw new MonzoAuthException("Monzo needs approval in the app (Strong Customer Authentication). Open the Monzo app and approve API access, then re-run.");
            if ((int)res.StatusCode == 429 && attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1))); continue; }
            throw new HttpRequestException($"Monzo {pathAndQuery} -> {(int)res.StatusCode}: {body}");
        }
    }

    public async Task<List<MonzoAccount>> GetAccountsAsync()
    {
        var j = await GetAsync("/accounts");
        var list = new List<MonzoAccount>();
        foreach (var a in j["accounts"]!.AsArray())
        {
            if ((bool?)a!["closed"] == true) continue;
            var type = (string)a["type"]!;
            var label = type switch { "uk_retail" => "personal", "uk_retail_joint" => "joint", _ => type };
            list.Add(new MonzoAccount((string)a["id"]!, label));
        }
        return list;
    }

    public async Task<Dictionary<string, string>> GetPotNamesAsync(string accountId)
    {
        var j = await GetAsync($"/pots?current_account_id={accountId}");
        return j["pots"]!.AsArray().Where(p => (bool?)p!["deleted"] != true)
            .ToDictionary(p => (string)p!["id"]!, p => (string)p!["name"]!);
    }

    public async Task<List<MonzoTx>> GetTransactionsAsync(string accountId, DateTimeOffset since, Dictionary<string, string> pots)
    {
        var all = new List<MonzoTx>();
        var cursor = since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        while (true)
        {
            var j = await GetAsync($"/transactions?account_id={accountId}&since={Uri.EscapeDataString(cursor)}&limit=100&expand[]=merchant");
            var arr = j["transactions"]!.AsArray();
            foreach (var t in arr) { var tx = Parse(t!, pots); if (tx != null) all.Add(tx); }
            if (arr.Count < 100) break;
            cursor = (string)arr[^1]!["id"]!;
        }
        return all;
    }

    private static MonzoTx? Parse(JsonNode t, Dictionary<string, string> pots)
    {
        if (!string.IsNullOrEmpty((string?)t["decline_reason"])) return null; // declined: no money moved
        var desc = (string?)t["description"] ?? "";
        var merchant = t["merchant"] as JsonObject; // null (or a bare id string) when not expanded
        var counterparty = t["counterparty"] as JsonObject;
        var name = (string?)merchant?["name"] ?? (string?)counterparty?["name"]
                   ?? (pots.TryGetValue(desc, out var potName) ? potName : desc);
        var amount = (long)t["amount"]!;
        return new MonzoTx(
            (string)t["id"]!, DateTimeOffset.Parse((string)t["created"]!, System.Globalization.CultureInfo.InvariantCulture),
            name, (string?)merchant?["emoji"] ?? "", (string?)t["category"] ?? "", (string?)t["scheme"] ?? "",
            amount, ((string?)t["currency"] ?? "GBP"),
            (long?)t["local_amount"] ?? amount, (string?)t["local_currency"] ?? "GBP",
            (string?)t["notes"] ?? "", (string?)merchant?["address"]?["short_address"] ?? "",
            desc, !string.IsNullOrEmpty((string?)t["settled"]));
    }
}
