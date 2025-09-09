using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Playwright;
using Rates.Core;

namespace Rates.Scraping;

public sealed class SpecialRateProvider : IRateProvider
{
    // Use the same URL you tried before. Playwright will load it like a real browser.
    private const string Url =
        "https://www.bankofgreece.gr/statistika/xrhmatopistwtikes-agores/ekswtrapezika-epitokia";

    public async Task<IReadOnlyList<RatePeriod>> GetPeriodsAsync()
    {
        // 1) Prepare output folder and file name under the app's directory
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dataDir);
        var htmlPath = Path.Combine(dataDir, "bog_rates.html");

        // 2) Remove any previous file
        if (File.Exists(htmlPath))
            File.Delete(htmlPath);

        // 3) Use Playwright (headless Chromium) to fetch the page
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        // Optional: locale + UA help match a real visitor
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                        "(KHTML, like Gecko) Chrome/122.0 Safari/537.36",
            Locale = "el-GR"
        });

        var page = await context.NewPageAsync();
        // Wait for network to settle; increase timeout if your connection is slow
        await page.GotoAsync(Url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        // 4) Get the full rendered HTML and save it locally
        var content = await page.ContentAsync();
        await File.WriteAllTextAsync(htmlPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // 5) Parse the saved HTML just like the Local provider
        var doc = new HtmlDocument();
        doc.LoadHtml(content);
        var periods = ParseDocumentToPeriods(doc);

        if (periods.Count == 0)
            throw new InvalidOperationException("No rate periods parsed from downloaded HTML (page structure may have changed).");

        return periods;
    }

    // ---------- parser (mirrors LocalHtmlRateProvider) ----------

    private static IReadOnlyList<RatePeriod> ParseDocumentToPeriods(HtmlDocument doc)
    {
        // Try to find the correct table by header keywords (Greek or English)
        var table = doc.DocumentNode.SelectSingleNode(
            "//table[.//th[contains(.,'Αρχική') or contains(.,'Valid From')]]")
            ?? doc.DocumentNode.SelectSingleNode("//table[.//th[contains(.,'Δικαιοπρακτικός') or contains(.,'Contractual')]]");

        var rows = table?.SelectNodes(".//tr[td]") ?? doc.DocumentNode.SelectNodes("//tr[td]");
        if (rows is null) return Array.Empty<RatePeriod>();

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

        list = list.OrderBy(p => p.Start).ThenBy(p => p.End).ToList();
        return MergeAdjacentSameRate(list);
    }

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
