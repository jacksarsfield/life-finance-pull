using System.Text.Json;

namespace Finance.Pull;

public sealed record CatRule(string Id, string[] Exact, string[] Contains, string? Type, string? Account, string Category, string Confidence, string Note);
public sealed record CatOverride(string TxId, string Category, string Reason);

/// <summary>Claude's categories: ordered rules from finance/categories.json, per-transaction overrides first.
/// Unmatched payments are "Unsorted" so they get reviewed.</summary>
public sealed class Categoriser
{
    public const string Unsorted = "Unsorted";
    private readonly List<CatRule> _rules = [];
    private readonly Dictionary<string, CatOverride> _overrides = new();

    public static Categoriser Load(string path)
    {
        var c = new Categoriser();
        if (!File.Exists(path)) return c;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("overrides", out var ov))
            foreach (var o in ov.EnumerateArray())
                c._overrides[o.GetProperty("tx_id").GetString()!] =
                    new CatOverride(o.GetProperty("tx_id").GetString()!, o.GetProperty("category").GetString()!, Str(o, "reason") ?? "override");
        foreach (var r in doc.RootElement.GetProperty("rules").EnumerateArray())
            c._rules.Add(new CatRule(Str(r, "id") ?? "?", Arr(r, "equals"), Arr(r, "contains"), Str(r, "type"), Str(r, "account"),
                r.GetProperty("category").GetString()!, Str(r, "confidence") ?? "inferred", Str(r, "note") ?? ""));
        return c;
    }

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string[] Arr(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString()!.ToLowerInvariant()).ToArray() : [];

    /// <summary>Returns (category, rule id) for a transaction.</summary>
    public (string Category, string Rule) Classify(string txId, string name, string type, string account)
    {
        if (_overrides.TryGetValue(txId, out var o)) return (o.Category, "override");
        var n = (name ?? "").Trim().ToLowerInvariant();
        foreach (var r in _rules)
        {
            if (r.Type != null && !string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.Account != null && !string.Equals(r.Account, account, StringComparison.OrdinalIgnoreCase)) continue;
            var hasName = r.Exact.Length > 0 || r.Contains.Length > 0;
            if (hasName && !(r.Exact.Contains(n) || r.Contains.Any(k => n.Contains(k)))) continue;
            if (!hasName && r.Type == null && r.Account == null) continue; // an empty rule matches nothing
            return (r.Category, r.Id);
        }
        return (Unsorted, "");
    }
}
