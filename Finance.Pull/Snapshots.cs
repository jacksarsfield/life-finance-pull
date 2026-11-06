using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Finance.Pull;

/// <summary>Reads the first table of finance/snapshots.md into JSON rows for the dashboard.</summary>
public static class Snapshots
{
    public static string ToJson(string markdownPath)
    {
        var rows = new List<object>();
        if (File.Exists(markdownPath))
        {
            foreach (var line in File.ReadAllLines(markdownPath))
            {
                if (!line.StartsWith('|')) { if (rows.Count > 0) break; else continue; }
                var cells = line.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                if (cells.Length < 6 || cells[0] == "As of" || cells[0].StartsWith("---")) continue;
                if (!DateTime.TryParseExact(cells[0], "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                var digits = Regex.Match(cells[2].Replace(",", ""), @"\d+(\.\d+)?");
                rows.Add(new
                {
                    as_of = d.ToString("yyyy-MM-dd"), account = cells[1], value = digits.Success ? decimal.Parse(digits.Value, CultureInfo.InvariantCulture) : 0m,
                    value_text = cells[2], status = cells[3], source = cells[4], notes = cells[5],
                });
            }
        }
        return JsonSerializer.Serialize(rows);
    }
}
