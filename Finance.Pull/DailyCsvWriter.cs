using System.Globalization;
using System.Text;

namespace Finance.Pull;

/// <summary>Writes one CSV per account per local (Europe/London) day, in the same column layout as
/// Monzo's own CSV export, plus a trailing Settled column. Files are replaced atomically.</summary>
public static class DailyCsvWriter
{
    public static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private const string Header =
        "Transaction ID,Date,Time,Type,Name,Emoji,Category,Amount,Currency,Local amount,Local currency," +
        "Notes and #tags,Address,Receipt,Description,Category split,Money Out,Money In,Settled";

    public static DateOnly LocalDate(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, London).DateTime);

    public static string FileName(string account, DateOnly day) => $"monzo-{account}-{day:yyyy-MM-dd}.csv";

    /// <summary>Writes a file for each day in <paramref name="days"/> that has transactions. Returns paths written.</summary>
    public static List<string> Write(string outDir, string account, IEnumerable<MonzoTx> txs, IEnumerable<DateOnly> days)
    {
        Directory.CreateDirectory(outDir);
        var byDay = txs.GroupBy(t => LocalDate(t.Created)).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Created).ToList());
        var written = new List<string>();
        foreach (var day in days)
        {
            if (!byDay.TryGetValue(day, out var rows)) continue; // no transactions: don't create empty files
            var sb = new StringBuilder(Header).Append('\n');
            foreach (var t in rows) sb.Append(Row(t)).Append('\n');
            var path = Path.Combine(outDir, FileName(account, day));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            written.Add(path);
        }
        return written;
    }

    private static string Row(MonzoTx t)
    {
        var local = TimeZoneInfo.ConvertTime(t.Created, London);
        var amount = Money(t.AmountMinor);
        var fields = new[]
        {
            t.Id, local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            TypeOf(t.Scheme), t.Name, t.Emoji, Pretty(t.Category), amount, t.Currency, Money(t.LocalAmountMinor), t.LocalCurrency,
            t.Notes, t.Address, "", t.Description, "",
            t.AmountMinor < 0 ? amount : "", t.AmountMinor > 0 ? amount : "", t.Settled ? "yes" : "pending",
        };
        return string.Join(',', fields.Select(Escape));
    }

    private static string Money(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    // Approximate mapping to the labels used in Monzo's CSV export.
    private static string TypeOf(string scheme) => scheme switch
    {
        "mastercard" or "gps_mastercard" => "Card payment",
        "faster_payments" => "Faster payment",
        "bacs" => "Bacs",
        "p2p_payment" => "Monzo-to-Monzo",
        "uk_retail_pot" => "Pot transfer",
        "payport_faster_payments" => "Faster payment",
        "" => "Other",
        _ => scheme,
    };

    private static string Pretty(string category)
    {
        if (category.Length == 0) return "";
        var s = category.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static string Escape(string v) =>
        v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
