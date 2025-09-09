using HtmlAgilityPack;
using Rates.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;

namespace Rates.Scraping;

public sealed class BankOfGreeceRateProvider : IRateProvider
{
    //private const string Url =
    //    "https://www.bankofgreece.gr/statistika/xrhmatopistwtikes-agores/ekswtrapezika-epitokia";

    public async Task<IReadOnlyList<RatePeriod>> GetPeriodsAsync()
    {
        var urls = new[]
        {
        // English page (friendlier to bots)
        "https://www.bankofgreece.gr/en/statistics/financial-markets-and-interest-rates/interest-rates-applicable-on-ligitation",
        // Greek page (fallback)
        "https://www.bankofgreece.gr/statistika/xrhmatopistwtikes-agores/ekswtrapezika-epitokia",
    };

        foreach (var url in urls)
        {
            // 1) Try HtmlWeb (often bypasses 403)
            var doc = await LoadWithHtmlWebAsync(url);
            if (doc != null)
            {
                var periods = ParseDocumentToPeriods(doc);
                if (periods.Count > 0) return periods;
            }

            // 2) Fallback: cookie-aware HttpClient + browser headers
            doc = await LoadWithHttpClientAsync(url);
            if (doc != null)
            {
                var periods = ParseDocumentToPeriods(doc);
                if (periods.Count > 0) return periods;
            }
        }

        throw new InvalidOperationException("Unable to fetch or parse non-bank interest rates from the Bank of Greece.");
    }

    // ---------- Loaders ----------

    private static Task<HtmlDocument?> LoadWithHtmlWebAsync(string url)
    {
        return Task.Run(() =>
        {
            try
            {
                var web = new HtmlWeb
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36",
                    UsingCache = false,
                };
                var doc = web.Load(url);
                return doc;
            }
            catch { return null; }
        });
    }

    private static async Task<HtmlDocument?> LoadWithHttpClientAsync(string url)
    {
        try
        {
            var baseUri = new Uri("https://www.bankofgreece.gr/");
            var cookies = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true
            };

            using var http = new HttpClient(handler) { BaseAddress = baseUri };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,el;q=0.8");

            // Preflight to get cookies
            using (var _ = await http.GetAsync("/")) { /* ignore */ }

            // Actual request (use full URL)
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = baseUri;

            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync();
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return doc;
        }
        catch { return null; }
    }

    // ---------- Parser ----------

    private static IReadOnlyList<RatePeriod> ParseDocumentToPeriods(HtmlDocument doc)
    {
        // Find the specific table by header names (English or Greek)
        var table = doc.DocumentNode.SelectSingleNode(
            "//table[.//th[contains(.,'Valid From') or contains(.,'Αρχική')]]");

        var rows = table?.SelectNodes(".//tr[td]")
                   ?? doc.DocumentNode.SelectNodes("//tr[td]");

        if (rows is null) throw new InvalidOperationException("Could not locate any data rows in the page.");

        var gr = new CultureInfo("el-GR");
        var list = new List<RatePeriod>();

        foreach (var tr in rows)
        {
            var tds = tr.SelectNodes("./td");
            if (tds is null || tds.Count < 6) continue;

            // Columns (English page): Valid From | Valid Until | Regulatory Act | Gazette | Contractual | Default
            if (!TryParseDate(tds[0].InnerText, gr, out var start)) continue;
            if (!TryParseDate(tds[1].InnerText, gr, out var end)) continue;
            if (!TryParsePercent(tds[4].InnerText, out var contractual)) continue;
            if (!TryParsePercent(tds[5].InnerText, out var overdue)) continue;

            if (end < start) (start, end) = (end, start);
            list.Add(new RatePeriod(start, end, contractual, overdue));
        }

        if (list.Count == 0)
            throw new InvalidOperationException("No rate periods parsed (the page structure may have changed).");

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
        raw = HtmlEntity.DeEntitize(raw)
            .Replace("%", "")
            .Replace("\u00A0", " ") // nbsp
            .Trim();

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
            bool contiguous = (cur.End.AddDays(1) == next.Start);
            bool sameRates = cur.Contractual == next.Contractual && cur.Overdue == next.Overdue;

            if (contiguous && sameRates)
            {
                cur = cur with { End = next.End };
            }
            else
            {
                merged.Add(cur);
                cur = next;
            }
        }
        merged.Add(cur);
        return merged;
    }

}
