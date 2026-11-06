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
