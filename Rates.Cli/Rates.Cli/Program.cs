using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Rates.Core;
using Rates.Scraping;
using Rates.Cli;

internal static class Program
{
    private static async System.Threading.Tasks.Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Interest Calculator ===");

        // --- load configuration ---
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "RATES_") // e.g. RATES_Source=Online
            .Build();

        var settings = new Settings();
        config.Bind(settings);

        // --- choose provider based on settings.Source ---
        IRateProvider provider = settings.Source?.Trim().ToLowerInvariant() switch
        {
            "online" => new BankOfGreeceRateProvider(),            // may 403, but path exists
            "special" => new SpecialRateProvider(),                 // placeholder
            _ => new LocalHtmlRateProvider(RequireLocal(settings.LocalHtmlPath))
        };

        Console.WriteLine($"Source: {settings.Source}");

        // --- inputs (no html path prompt anymore) ---
        decimal amount = ReadDecimal("Amount (€): ");
        DateTime from = ReadDate("Date (from) [yyyy-MM-dd]: ");
        DateTime to = ReadDate("Date (to)   [yyyy-MM-dd]: ");
        DayCount method = ReadMethod();
        RateType rate = ReadRateType();

        // --- fetch periods ---
        IReadOnlyList<RatePeriod> periods;
        try
        {
            periods = await provider.GetPeriodsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nERROR fetching rates: {ex.Message}");
            return;
        }

        // --- calculate ---
        var calc = new InterestCalculator();
        var slices = calc.Calculate(periods, from, to, amount, rate, method).ToList();

        if (slices.Count == 0)
        {
            Console.WriteLine("\nNo overlapping periods with the selected date range.");
            return;
        }

        // --- output: detailed breakdown ---
        Console.WriteLine("\n--- Detailed breakdown ---");
        Console.WriteLine("Period                Days   Annual %   Interest (€)");
        Console.WriteLine("----------------------------------------------------");
        decimal total = 0m;
        foreach (var s in slices)
        {
            total += s.Interest;
            Console.WriteLine($"{s.From:yyyy-MM-dd}..{s.To:yyyy-MM-dd}  {s.Days,4}   {s.AnnualRatePercent,7:0.00}      {s.Interest,10:0.00}");
        }

        // --- yearly summary ---
        Console.WriteLine("\n--- Yearly summary ---");
        foreach (var (year, value) in SummarizeByYear(slices, amount, method))
            Console.WriteLine($"{year}: {value:0.00} €");

        Console.WriteLine($"\nTOTAL INTEREST: {total:0.00} €");

        // --- optional CSV export ---
        if (YesNo("\nExport CSV with the detailed breakdown? [y/n]: "))
        {
            var csvPath = WriteCsv(slices, amount, rate, method);
            Console.WriteLine($"CSV saved: {csvPath}");
        }

        Console.WriteLine("\nDone.");
    }

    private static string RequireLocal(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new FileNotFoundException("LocalHtmlPath is empty in appsettings.json.");
        if (!File.Exists(configuredPath))
            throw new FileNotFoundException("Local HTML file was not found.", configuredPath);
        return configuredPath;
    }

    // ---------- input helpers ----------
    private static decimal ReadDecimal(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            if (decimal.TryParse(Console.ReadLine(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0)
                return v;
            Console.WriteLine("Enter a valid non-negative number. Use dot for decimals (e.g., 10000.50).");
        }
    }
    private static DateTime ReadDate(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            if (DateTime.TryParseExact(Console.ReadLine(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
            Console.WriteLine("Use format yyyy-MM-dd (e.g., 2024-03-15).");
        }
    }
    private static DayCount ReadMethod()
    {
        while (true)
        {
            Console.Write("Method [1=CalendarYear (Actual/365/366), 2=Banking360]: ");
            var s = (Console.ReadLine() ?? "").Trim();
            if (s == "1") return DayCount.CalendarYear;
            if (s == "2") return DayCount.Banking360;
            Console.WriteLine("Choose 1 or 2.");
        }
    }
    private static RateType ReadRateType()
    {
        while (true)
        {
            Console.Write("Rate type [1=Contractual, 2=Overdue]: ");
            var s = (Console.ReadLine() ?? "").Trim();
            if (s == "1") return RateType.Contractual;
            if (s == "2") return RateType.Overdue;
            Console.WriteLine("Choose 1 or 2.");
        }
    }
    private static bool YesNo(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            var s = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (s is "y" or "yes") return true;
            if (s is "n" or "no") return false;
        }
    }

    private static IEnumerable<(int year, decimal value)> SummarizeByYear(
        IEnumerable<InterestSlice> slices, decimal amount, DayCount method)
    {
        var byYear = new SortedDictionary<int, decimal>();
        foreach (var s in slices)
        {
            var cur = s.From;
            while (cur.Year < s.To.Year)
            {
                var yearEnd = new DateTime(cur.Year, 12, 31);
                Add(cur, yearEnd); cur = yearEnd.AddDays(1);
            }
            Add(cur, s.To);

            void Add(DateTime a, DateTime b)
            {
                int y = a.Year;
                int days = (int)(b - a).TotalDays + 1;
                decimal denom = method == DayCount.CalendarYear ? (DateTime.IsLeapYear(y) ? 366m : 365m) : 360m;
                decimal interest = amount * (s.AnnualRatePercent / 100m) * (days / denom);
                byYear.TryGetValue(y, out var curr);
                byYear[y] = curr + decimal.Round(interest, 2, MidpointRounding.AwayFromZero);
            }
        }
        foreach (var kv in byYear) yield return (kv.Key, kv.Value);
    }

    private static string WriteCsv(IEnumerable<InterestSlice> slices, decimal amount, RateType rateType, DayCount method)
    {
        var now = DateTime.Now;
        var fileName = $"interest_breakdown_{now:yyyyMMdd_HHmmss}.csv";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), fileName);

        var sb = new StringBuilder();
        sb.AppendLine("Amount,RateType,Method");
        sb.AppendLine($"{amount.ToString(CultureInfo.InvariantCulture)},{rateType},{method}");
        sb.AppendLine();
        sb.AppendLine("From,To,Days,AnnualPercent,Interest");

        foreach (var s in slices)
        {
            sb.AppendLine($"{s.From:yyyy-MM-dd},{s.To:yyyy-MM-dd},{s.Days}," +
                          $"{s.AnnualRatePercent.ToString("0.00", CultureInfo.InvariantCulture)}," +
                          $"{s.Interest.ToString("0.00", CultureInfo.InvariantCulture)}");
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
