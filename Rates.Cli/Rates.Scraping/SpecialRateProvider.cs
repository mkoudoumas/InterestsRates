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
    // You can also test the English URL if you prefer
    private const string Url =
        "https://www.bankofgreece.gr/statistika/xrhmatopistwtikes-agores/ekswtrapezika-epitokia";

    public async Task<IReadOnlyList<RatePeriod>> GetPeriodsAsync()
    {
        const string Url = "https://www.bankofgreece.gr/statistika/xrhmatopistwtikes-agores/ekswtrapezika-epitokia";

        using var playwright = await Playwright.CreateAsync();

        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BogRatesScraperProfile");
        Directory.CreateDirectory(userDataDir);

        await using var context = await playwright.Chromium.LaunchPersistentContextAsync(
            userDataDir,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Channel = "msedge",   // falls back to stock Chromium if Edge not found
                Headless = false,     // visible helps bypass WAF; switch to true later if desired
                Locale = "el-GR",
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                            "(KHTML, like Gecko) Chrome/123.0 Safari/537.36",
                IgnoreHTTPSErrors = true,
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
                BypassCSP = true
            });

        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

        // 1) Navigate
        await page.GotoAsync(Url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 90000
        });

        // 2) Try to accept cookies (if any). CountAsync() works on all versions.
        await page.WaitForTimeoutAsync(800);
        try
        {
            var accept = page.Locator("button:has-text('Αποδοχή'), button:has-text('ΑΠΟΔΟΧΗ'), button:has-text('Accept')");
            if (await accept.CountAsync() > 0)
            {
                await accept.First.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
            }
        }
        catch
        {
            // ignore if not found / not clickable
        }

        // 3) Grab HTML
        var content = await page.ContentAsync();

        // 4) Save locally (for debugging / audits)
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dataDir);
        var htmlPath = Path.Combine(dataDir, "bog_rates.html");
        await File.WriteAllTextAsync(htmlPath, content, new UTF8Encoding(true));

        // 5) Parse if the table exists (even if page text contains "Access denied")
        if (HasRatesTable(content))
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(content);
            var periods = ParseDocumentToPeriods(doc);
            if (periods.Count > 0) return periods;
        }

        // 6) If we’re here, we didn’t find a parsable table
        throw new InvalidOperationException(
            "Access denied or the rates table was not found. " +
            $"Saved HTML: {htmlPath}. Try Source=Local with a manually saved page.");
    }

    /* ---------- helpers (keep these in the same class) ---------- */

    private static bool HasRatesTable(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var table =
            doc.DocumentNode.SelectSingleNode("//table[.//th[contains(.,'Αρχική') or contains(.,'Valid From')]]")
            ?? doc.DocumentNode.SelectSingleNode("//table[.//th[contains(.,'Τελική') or contains(.,'Valid To')]]")
            ?? doc.DocumentNode.SelectSingleNode("//table[.//th[contains(.,'Δικαιοπρακτικός') or contains(.,'Contractual')]]")
            ?? doc.DocumentNode.SelectSingleNode("//table[.//th[contains(.,'Υπερημερίας') or contains(.,'Overdue')]]");

        return table != null;
    }

    private static IReadOnlyList<RatePeriod> ParseDocumentToPeriods(HtmlDocument doc)
    {
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
