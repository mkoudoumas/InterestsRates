using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HtmlAgilityPack;
using Rates.Core;

namespace Rates.Scraping;

public sealed class LocalHtmlRateProvider : IRateProvider
{
    private readonly string _htmlPath;
    public LocalHtmlRateProvider(string htmlPath) => _htmlPath = htmlPath;

    public Task<IReadOnlyList<RatePeriod>> GetPeriodsAsync()
    {
        if (!File.Exists(_htmlPath))
            throw new FileNotFoundException("Local HTML file was not found.", _htmlPath);

        var html = File.ReadAllText(_htmlPath);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var periods = ParseDocumentToPeriods(doc);
        return Task.FromResult(periods);
    }

    // Reuse the same parser as before, matching either English or Greek headers.
    private static IReadOnlyList<RatePeriod> ParseDocumentToPeriods(HtmlDocument doc)
    {
        // Try to find the table by header keywords
        var table = doc.DocumentNode.SelectSingleNode(
            "//table[.//th[contains(.,'Valid From') or contains(.,'Αρχική')]]")
            ?? doc.DocumentNode.SelectSingleNode("//table[.//th[contains(.,'Contractual') or contains(.,'Δικαιοπρακτικός')]]");

        var rows = table?.SelectNodes(".//tr[td]") ?? doc.DocumentNode.SelectNodes("//tr[td]");
        if (rows is null) throw new InvalidOperationException("Could not locate data rows in the saved HTML.");

        var gr = new CultureInfo("el-GR");
        var list = new List<RatePeriod>();

        foreach (var tr in rows)
        {
            var tds = tr.SelectNodes("./td");
            if (tds is null || tds.Count < 6) continue;

            if (!TryParseDate(tds[0].InnerText, gr, out var start)) continue;
            if (!TryParseDate(tds[1].InnerText, gr, out var end)) continue;
            if (!TryParsePercent(tds[4].InnerText, out var contractual)) continue;
            if (!TryParsePercent(tds[5].InnerText, out var overdue)) continue;

            if (end < start) (start, end) = (end, start);
            list.Add(new RatePeriod(start, end, contractual, overdue));
        }

        if (list.Count == 0)
            throw new InvalidOperationException("No rate periods parsed from the saved HTML.");

        list = list.OrderBy(p => p.Start).ThenBy(p => p.End).ToList();
        return MergeAdjacentSameRate(list);
    }

    // Helpers (same as before)
    private static bool TryParseDate(string raw, CultureInfo gr, out DateTime dt)
    {
        raw = HtmlEntity.DeEntitize(raw).Trim();
        string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "dd-MM-yyyy", "d-M-yyyy" };
        return DateTime.TryParseExact(raw, formats, gr, DateTimeStyles.None, out dt);
    }

    private static bool TryParsePercent(string raw, out decimal value)
    {
        raw = HtmlEntity.DeEntitize(raw).Replace("%", "").Replace("\u00A0", " ").Trim();
        raw = raw.Replace(',', '.');
        var num = new string(raw.TakeWhile(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
        if (string.IsNullOrWhiteSpace(num)) { value = 0; return false; }
        return decimal.TryParse(num, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                                CultureInfo.InvariantCulture, out value);
    }

    private static List<RatePeriod> MergeAdjacentSameRate(List<RatePeriod> periods)
    {
        if (periods.Count == 0) return periods;
        var merged = new List<RatePeriod>();
        var cur = periods[0];

        for (int i = 1; i < periods.Count; i++)
        {
            var next = periods[i];
            bool contiguous = cur.End.AddDays(1) == next.Start;
            bool sameRates = cur.Contractual == next.Contractual && cur.Overdue == next.Overdue;
            if (contiguous && sameRates) cur = cur with { End = next.End };
            else { merged.Add(cur); cur = next; }
        }
        merged.Add(cur);
        return merged;
    }
}
