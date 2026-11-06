using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Finance.Pull;

public sealed record LedgerRow(
    string Id, string Account, string Date, string Time, string Type, string Name, string Emoji, string Category,
    long AmountMinor, string Currency, long LocalAmountMinor, string LocalCurrency,
    string Notes, string Address, string Description, bool Settled, string Source);

/// <summary>SQLite ledger of every transaction, keyed on Monzo's transaction ID.</summary>
public sealed class Ledger : IDisposable
{
    private readonly SqliteConnection _db;

    public Ledger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("""
            CREATE TABLE IF NOT EXISTS tx (
              id TEXT PRIMARY KEY, account TEXT NOT NULL,
              date TEXT NOT NULL,            -- yyyy-MM-dd, Europe/London
              time TEXT NOT NULL, type TEXT, name TEXT, emoji TEXT, category TEXT,
              amount_minor INTEGER NOT NULL, currency TEXT,
              local_amount_minor INTEGER, local_currency TEXT,
              notes TEXT, address TEXT, description TEXT,
              settled INTEGER NOT NULL,      -- 1 settled, 0 pending
              source TEXT NOT NULL,          -- 'export' (Monzo CSV export) or 'daily' (API pull)
              loaded_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_tx_account_date ON tx(account, date);
            """);
    }

    /// <summary>Re-applies Claude's category rules to every transaction (cheap: a few thousand rows).</summary>
    public int ApplyCategories(Categoriser cat)
    {
        foreach (var col in new[] { "my_cat", "my_rule" })
        {
            try { Exec($"ALTER TABLE tx ADD COLUMN {col} TEXT"); } catch (SqliteException) { /* already there */ }
        }
        var rows = new List<(string id, string name, string type, string account)>();
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT id, COALESCE(name,''), COALESCE(type,''), account FROM tx";
            using var r = c.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        using var tr = _db.BeginTransaction();
        foreach (var (id, name, type, account) in rows)
        {
            var (category, rule) = cat.Classify(id, name, type, account);
            using var u = _db.CreateCommand();
            u.CommandText = "UPDATE tx SET my_cat=$c, my_rule=$r WHERE id=$id";
            u.Parameters.AddWithValue("$c", category); u.Parameters.AddWithValue("$r", rule); u.Parameters.AddWithValue("$id", id);
            u.ExecuteNonQuery();
        }
        tr.Commit();
        return rows.Count;
    }

    /// <summary>Last-90-day payments Claude couldn't categorise, biggest first, as markdown.</summary>
    public string UnsortedMarkdown(DateOnly today)
    {
        var sb = new StringBuilder("# Unsorted payments (last 90 days)\n\n");
        sb.Append($"Generated {DateTimeOffset.Now:dd/MM/yyyy HH:mm}. These payees have no rule in `categories.json` yet; the Morning Sweep reviews them. Excludes transfers, pots and Amex payments.\n\n");
        sb.Append("| Payee | Type | Monzo category | Payments | Total | Latest |\n|---|---|---|---|---|---|\n");
        using var c = _db.CreateCommand();
        c.CommandText = $"SELECT name, type, category, COUNT(*), SUM(-amount_minor), MAX(date) FROM tx WHERE date>=$f AND amount_minor<0 AND my_cat='Unsorted' AND {NotSpend} GROUP BY name,type,category ORDER BY 5 DESC LIMIT 60";
        c.Parameters.AddWithValue("$f", today.AddDays(-89).ToString("yyyy-MM-dd"));
        using var r = c.ExecuteReader();
        var any = false;
        while (r.Read()) { any = true; sb.Append($"| {r.GetString(0)} | {r.GetString(1)} | {r.GetString(2)} | {r.GetInt64(3)} | {Gbp(r.GetInt64(4))} | {Fmt(r.GetString(5))} |\n"); }
        if (!any) sb.Append("| none | | | | | |\n");
        return sb.ToString();
    }

    private void Exec(string sql) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }

    /// <summary>Insert or update. A settled 'export' row is never overwritten by a 'daily' row;
    /// a pending row is always replaced. Returns (inserted, updated).</summary>
    public (int Inserted, int Updated) Upsert(IEnumerable<LedgerRow> rows)
    {
        int ins = 0, upd = 0;
        using var tr = _db.BeginTransaction();
        foreach (var r in rows)
        {
            using var exists = _db.CreateCommand();
            exists.CommandText = "SELECT 1 FROM tx WHERE id=$id";
            exists.Parameters.AddWithValue("$id", r.Id);
            var had = exists.ExecuteScalar() != null;

            using var c = _db.CreateCommand();
            c.CommandText = """
                INSERT INTO tx (id,account,date,time,type,name,emoji,category,amount_minor,currency,local_amount_minor,local_currency,notes,address,description,settled,source,loaded_at)
                VALUES ($id,$account,$date,$time,$type,$name,$emoji,$category,$amt,$cur,$lamt,$lcur,$notes,$addr,$desc,$settled,$source,$now)
                ON CONFLICT(id) DO UPDATE SET
                  account=excluded.account, date=excluded.date, time=excluded.time, type=excluded.type, name=excluded.name,
                  emoji=excluded.emoji, category=excluded.category, amount_minor=excluded.amount_minor, currency=excluded.currency,
                  local_amount_minor=excluded.local_amount_minor, local_currency=excluded.local_currency, notes=excluded.notes,
                  address=excluded.address, description=excluded.description, settled=excluded.settled,
                  source=excluded.source, loaded_at=excluded.loaded_at
                WHERE tx.settled=0 OR excluded.source='export'
                """;
            c.Parameters.AddWithValue("$id", r.Id); c.Parameters.AddWithValue("$account", r.Account);
            c.Parameters.AddWithValue("$date", r.Date); c.Parameters.AddWithValue("$time", r.Time);
            c.Parameters.AddWithValue("$type", r.Type); c.Parameters.AddWithValue("$name", r.Name);
            c.Parameters.AddWithValue("$emoji", r.Emoji); c.Parameters.AddWithValue("$category", r.Category);
            c.Parameters.AddWithValue("$amt", r.AmountMinor); c.Parameters.AddWithValue("$cur", r.Currency);
            c.Parameters.AddWithValue("$lamt", r.LocalAmountMinor); c.Parameters.AddWithValue("$lcur", r.LocalCurrency);
            c.Parameters.AddWithValue("$notes", r.Notes); c.Parameters.AddWithValue("$addr", r.Address);
            c.Parameters.AddWithValue("$desc", r.Description); c.Parameters.AddWithValue("$settled", r.Settled ? 1 : 0);
            c.Parameters.AddWithValue("$source", r.Source); c.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
            var changed = c.ExecuteNonQuery();
            if (!had) ins++; else if (changed > 0) upd++;
        }
        tr.Commit();
        return (ins, upd);
    }

    public string CoverageMarkdown()
    {
        var sb = new StringBuilder("# Finance ledger coverage\n\n");
        sb.Append($"Generated {DateTimeOffset.Now:dd/MM/yyyy HH:mm}. Source: Monzo only (exports + API pulls).\n\n");
        sb.Append("| Account | First day | Last day | Transactions | Pending | Days with none (gaps over 1 day) |\n|---|---|---|---|---|---|\n");
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT account, MIN(date), MAX(date), COUNT(*), SUM(settled=0) FROM tx GROUP BY account ORDER BY account";
        using var r = c.ExecuteReader();
        var accounts = new List<(string a, string min, string max, long n, long p)>();
        while (r.Read()) accounts.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
        foreach (var (a, min, max, n, p) in accounts)
            sb.Append($"| {a} | {Fmt(min)} | {Fmt(max)} | {n} | {p} | {Gaps(a)} |\n");
        sb.Append("\nA gap can be genuine (no spending) or missing data. Gaps of more than one day are listed.\n");
        return sb.ToString();
    }

    private static readonly CultureInfo Gb = CultureInfo.GetCultureInfo("en-GB");
    private static string Gbp(long minor) => (minor / 100m).ToString("C2", Gb);

    // Money moving between your own pots/accounts is not spend; Amex payments are listed separately.
    private const string NotSpend = "type NOT IN ('Pot transfer','Monzo-to-Monzo') AND category NOT IN ('Transfers','Savings') " +
                                    "AND LOWER(name) NOT LIKE '%american exp%' AND LOWER(name) NOT LIKE '%amex%'";

    /// <summary>Rolling 30 days of payments by category (both accounts), as JSON rows for the Life dashboard.</summary>
    public string Spend30Json(DateOnly today)
    {
        var rows = new List<object>();
        foreach (var (set, col) in new[] { ("monzo", "category"), ("claude", "my_cat") })
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT COALESCE(NULLIF({col},''),'Other'), COUNT(*), ROUND(SUM(-amount_minor)/100.0,2) FROM tx " +
                            $"WHERE date>=$f AND date<=$t AND amount_minor<0 AND {NotSpend} GROUP BY 1 ORDER BY 3 DESC";
            c.Parameters.AddWithValue("$f", today.AddDays(-29).ToString("yyyy-MM-dd"));
            c.Parameters.AddWithValue("$t", today.ToString("yyyy-MM-dd"));
            using var r = c.ExecuteReader();
            while (r.Read()) rows.Add(new { set, category = r.GetString(0), payments = r.GetInt64(1), amount = r.GetDecimal(2) });
        }
        return System.Text.Json.JsonSerializer.Serialize(rows);
    }

    /// <summary>Short, human-readable status for the Morning Sweep to read.</summary>
    public string SummaryMarkdown(string statusJsonPath, DateOnly today)
    {
        var sb = new StringBuilder("# Finance summary (Monzo only)\n\n");
        sb.Append($"Generated {DateTimeOffset.Now:dd/MM/yyyy HH:mm}. Source: `finance/ledger.db` (Monzo exports + API pulls). Amex spend is not in here.\n\n");

        sb.Append("## Feed freshness\n");
        try
        {
            var st = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statusJsonPath)).RootElement;
            var last = st.GetProperty("lastSuccess").GetString();
            var ok = st.GetProperty("ok").GetBoolean();
            sb.Append($"- Last successful pull: {(last is null ? "never" : DateTimeOffset.Parse(last).ToString("dd/MM/yyyy HH:mm"))}. Last run {(ok ? "succeeded" : "FAILED: " + st.GetProperty("message").GetString())}.\n");
        }
        catch { sb.Append("- No status.json found: the puller has not run.\n"); }

        var from = today.AddDays(-6).ToString("yyyy-MM-dd");
        sb.Append($"\n## Last 7 days ({Fmt(from)} to {today:dd/MM/yyyy})\n");
        sb.Append("Out-of-pocket card/bank payments, excluding pot and own-account transfers and Amex payments. Not all of this is a household *cost* (it includes payments to family).\n\n");
        sb.Append("| Account | Payments | Total out |\n|---|---|---|\n");
        using (var c = _db.CreateCommand())
        {
            c.CommandText = $"SELECT account, COUNT(*), SUM(-amount_minor) FROM tx WHERE date>=$f AND amount_minor<0 AND {NotSpend} GROUP BY account ORDER BY account";
            c.Parameters.AddWithValue("$f", from);
            using var r = c.ExecuteReader();
            while (r.Read()) sb.Append($"| {r.GetString(0)} | {r.GetInt64(1)} | {Gbp(r.GetInt64(2))} |\n");
        }

        sb.Append("\n## Payments of £500 or more (last 7 days)\n");
        var big = 0;
        using (var c = _db.CreateCommand())
        {
            c.CommandText = $"SELECT date, account, name, -amount_minor FROM tx WHERE date>=$f AND amount_minor<=-50000 AND {NotSpend} ORDER BY date DESC";
            c.Parameters.AddWithValue("$f", from);
            using var r = c.ExecuteReader();
            while (r.Read()) { big++; sb.Append($"- {Fmt(r.GetString(0))} {r.GetString(1)}: {r.GetString(2)} {Gbp(r.GetInt64(3))}\n"); }
        }
        if (big == 0) sb.Append("- None\n");

        sb.Append("\n## Amex payments from Monzo\n");
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT date, account, -amount_minor FROM tx WHERE amount_minor<0 AND (LOWER(name) LIKE '%american exp%' OR LOWER(name) LIKE '%amex%') ORDER BY date DESC LIMIT 4";
            using var r = c.ExecuteReader();
            var any = false;
            while (r.Read()) { any = true; sb.Append($"- {Fmt(r.GetString(0))} from {r.GetString(1)}: {Gbp(r.GetInt64(2))}\n"); }
            if (!any) sb.Append("- None found by name (check how Amex appears in the exports)\n");
        }
        var due = today.Day <= 18 ? new DateOnly(today.Year, today.Month, 18) : new DateOnly(today.Year, today.Month, 18).AddMonths(1);
        sb.Append($"- Next due date (payment is due on the 18th): {due:dd/MM/yyyy}. Statement balance is not in the Monzo data.\n");

        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM tx WHERE settled=0";
            sb.Append($"\n## Pending\n- {c.ExecuteScalar()} pending transaction(s) not yet settled.\n");
        }
        return sb.ToString();
    }

    private string Gaps(string account)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT DISTINCT date FROM tx WHERE account=$a ORDER BY date";
        c.Parameters.AddWithValue("$a", account);
        using var r = c.ExecuteReader();
        DateOnly? prev = null; var gaps = new List<string>();
        while (r.Read())
        {
            var d = DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (prev is { } p && d.DayNumber - p.DayNumber > 2) gaps.Add($"{p.AddDays(1):dd/MM}–{d.AddDays(-1):dd/MM}");
            prev = d;
        }
        return gaps.Count == 0 ? "none" : string.Join(", ", gaps);
    }

    private static string Fmt(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd/MM/yyyy");

    public void Dispose() => _db.Dispose();
}

public static class CsvLoader
{
    /// <summary>Reads a Monzo CSV (export or daily file) into ledger rows.</summary>
    public static List<LedgerRow> Read(string path, string account, string source)
    {
        var rows = Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
        var header = rows[0].Select((h, i) => (h, i)).ToDictionary(x => x.h.Trim(), x => x.i);
        string F(List<string> row, string col) => header.TryGetValue(col, out var i) && i < row.Count ? row[i] : "";
        var list = new List<LedgerRow>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Count < 8 || string.IsNullOrWhiteSpace(F(row, "Transaction ID"))) continue;
            var date = DateTime.ParseExact(F(row, "Date"), "dd/MM/yyyy", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd");
            var settledCol = F(row, "Settled"); // absent in Monzo's own exports: treat as settled
            list.Add(new LedgerRow(F(row, "Transaction ID"), account, date, F(row, "Time"), F(row, "Type"), F(row, "Name"),
                F(row, "Emoji"), F(row, "Category"), Minor(F(row, "Amount")), F(row, "Currency"),
                Minor(F(row, "Local amount")), F(row, "Local currency"), F(row, "Notes and #tags"), F(row, "Address"),
                F(row, "Description"), settledCol != "pending", source));
        }
        return list;
    }

    private static long Minor(string s) =>
        string.IsNullOrWhiteSpace(s) ? 0 : (long)Math.Round(decimal.Parse(s, CultureInfo.InvariantCulture) * 100m);

    /// <summary>RFC 4180-style parser (quoted fields, doubled quotes, newlines inside quotes).</summary>
    private static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var f = new StringBuilder(); var q = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (q)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { f.Append('"'); i++; } else q = false; }
                else f.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { row.Add(f.ToString()); f.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(f.ToString()); f.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = [];
            }
            else f.Append(ch);
        }
        if (f.Length > 0 || row.Count > 0) { row.Add(f.ToString()); rows.Add(row); }
        return rows;
    }
}
