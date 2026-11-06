using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Finance.Pull;

// Usage:
//   Finance.Pull auth                 one-off OAuth sign-in to Monzo (you approve in the Monzo app)
//   Finance.Pull pull [--out DIR]     pull today's + yesterday's transactions, write daily CSVs
//   Finance.Pull selftest             write sample files to a temp folder (no network)
// Env: LIFE_INBOX (default C:\Projects\Life\inbox), LIFE_FINANCE_STATUS (default C:\Projects\Life\finance\status.json)

var cmd = args.FirstOrDefault() ?? "pull";
string Opt(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
var outDir = Opt("--out", Environment.GetEnvironmentVariable("LIFE_INBOX") ?? @"C:\Projects\Life\inbox");
var statusPath = Environment.GetEnvironmentVariable("LIFE_FINANCE_STATUS") ?? @"C:\Projects\Life\finance\status.json";

try
{
    return cmd switch
    {
        "auth" => await AuthAsync(),
        "pull" => await PullAsync(),
        "selftest" => SelfTest(),
        _ => Fail($"Unknown command '{cmd}'. Use: auth | pull | selftest"),
    };
}
catch (MonzoAuthException e) { WriteStatus(false, e.Message, []); return Fail(e.Message); }
catch (Exception e) { WriteStatus(false, e.Message, []); return Fail(e.ToString()); }

static int Fail(string m) { Console.Error.WriteLine(m); return 1; }

async Task<int> PullAsync()
{
    var secrets = TokenStore.Load() ?? throw new MonzoAuthException("Not signed in. Run `auth` first.");
    var client = new MonzoClient(secrets);

    // Pull from the start of yesterday (local) so late-settling transactions get corrected, and rewrite both days.
    var today = DailyCsvWriter.LocalDate(DateTimeOffset.UtcNow);
    var days = new[] { today.AddDays(-1), today };
    var startLocal = days[0].ToDateTime(TimeOnly.MinValue);
    var since = new DateTimeOffset(startLocal, DailyCsvWriter.London.GetUtcOffset(startLocal));

    var report = new List<object>();
    foreach (var acct in await client.GetAccountsAsync())
    {
        var pots = await client.GetPotNamesAsync(acct.Id);
        var txs = await client.GetTransactionsAsync(acct.Id, since, pots);
        var files = DailyCsvWriter.Write(outDir, acct.Label, txs, days);
        Console.WriteLine($"{acct.Label}: {txs.Count} transactions, wrote {files.Count} file(s)");
        report.Add(new { account = acct.Label, transactions = txs.Count, files = files.Select(Path.GetFileName) });
    }
    WriteStatus(true, "ok", report);
    return 0;
}

async Task<int> AuthAsync()
{
    const string redirect = "http://localhost:8765/callback";
    Console.WriteLine("Create a CONFIDENTIAL client at https://developers.monzo.com/ with redirect URL: " + redirect);
    Console.Write("Client ID: "); var id = Console.ReadLine()!.Trim();
    Console.Write("Client secret: "); var secret = ReadSecret();
    var state = Guid.NewGuid().ToString("N");

    using var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:8765/");
    listener.Start();
    Process.Start(new ProcessStartInfo(MonzoClient.AuthUrl(id, redirect, state)) { UseShellExecute = true });
    Console.WriteLine("Browser opened. Sign in with the email link, then approve access in the Monzo app...");

    var ctx = await listener.GetContextAsync();
    var q = ctx.Request.QueryString;
    var ok = q["state"] == state && !string.IsNullOrEmpty(q["code"]);
    var msg = System.Text.Encoding.UTF8.GetBytes(ok ? "Done. You can close this tab." : "State mismatch or no code.");
    await ctx.Response.OutputStream.WriteAsync(msg); ctx.Response.Close();
    if (!ok) return Fail("Sign-in failed: state mismatch or no code returned.");

    var secrets = await MonzoClient.ExchangeCodeAsync(id, secret, redirect, q["code"]!);
    TokenStore.Save(secrets);
    Console.WriteLine(string.IsNullOrEmpty(secrets.RefreshToken)
        ? "Signed in, but NO refresh token was issued: the client must be Confidential."
        : "Signed in. Now approve API access in the Monzo app, then run `pull` within 5 minutes to get the fullest history.");
    return 0;
}

static string ReadSecret()
{
    var sb = new System.Text.StringBuilder();
    while (Console.ReadKey(true) is var k && k.Key != ConsoleKey.Enter)
    {
        if (k.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Length--;
        else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

void WriteStatus(bool ok, string message, List<object> report)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        // Keep lastSuccess from the previous file when this run failed.
        string? lastSuccess = null;
        if (!ok && File.Exists(statusPath))
            lastSuccess = JsonDocument.Parse(File.ReadAllText(statusPath)).RootElement.TryGetProperty("lastSuccess", out var p) ? p.GetString() : null;
        var now = DateTimeOffset.Now.ToString("o");
        File.WriteAllText(statusPath, JsonSerializer.Serialize(new
        {
            source = "monzo", lastRun = now, lastSuccess = ok ? now : lastSuccess, ok, message, accounts = report,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch { /* status is best-effort */ }
}

int SelfTest()
{
    var dir = Path.Combine(Path.GetTempPath(), "finance-selftest");
    var t = (string id, string iso, long amt, string name, string scheme, string cat) =>
        new MonzoTx(id, DateTimeOffset.Parse(iso), name, "", cat, scheme, amt, "GBP", amt, "GBP", "", "", "desc, with \"quote\"", true);
    var txs = new[]
    {
        t("tx_1", "2026-11-06T23:30:00Z", -2740, "Transport for London", "mastercard", "transport"),  // 23:30 UTC = same day GMT
        t("tx_2", "2026-07-01T23:30:00Z", -1200, "Savings Pot", "uk_retail_pot", "savings"),          // 00:30 on 02/07 BST
        t("tx_3", "2026-07-02T08:00:00Z", 800000, "Mercuria", "faster_payments", "income"),
    };
    var written = DailyCsvWriter.Write(dir, "personal", txs, [new(2026, 11, 6), new(2026, 7, 1), new(2026, 7, 2)]);
    foreach (var w in written) Console.WriteLine(w);
    return 0;
}
