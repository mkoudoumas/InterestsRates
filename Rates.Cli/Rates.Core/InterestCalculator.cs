using System;
using System.Collections.Generic;
using System.Linq;

namespace Rates.Core;

public sealed class InterestCalculator
{
    public IEnumerable<InterestSlice> Calculate(
        IEnumerable<RatePeriod> periods,
        DateTime from, DateTime to,
        decimal amount,
        RateType rateType,
        DayCount dayCount)
    {
        if (to < from) throw new ArgumentException("'to' is before 'from'.");

        foreach (var p in periods.OrderBy(p => p.Start))
        {
            // skip non-overlapping
            if (p.End < from || p.Start > to) continue;

            // clamp to requested range
            var s = p.Start < from ? from : p.Start;
            var e = p.End > to ? to : p.End;

            // split by year to handle leap correctly under CalendarYear
            foreach (var (ys, ye) in SplitByYear(s, e))
            {
                int days = (int)(ye - ys).TotalDays + 1; // inclusive
                decimal denom = dayCount switch
                {
                    DayCount.CalendarYear => DateTime.IsLeapYear(ys.Year) ? 366m : 365m,
                    DayCount.Banking360 => 360m,
                    _ => 365m
                };

                decimal annualPct = rateType == RateType.Contractual ? p.Contractual : p.Overdue;
                decimal interest = amount * (annualPct / 100m) * (days / denom);

                yield return new InterestSlice(
                    ys, ye,
                    annualPct,
                    days,
                    decimal.Round(interest, 2, MidpointRounding.AwayFromZero)
                );
            }
        }
    }

    private static IEnumerable<(DateTime, DateTime)> SplitByYear(DateTime start, DateTime end)
    {
        var cur = start;
        while (cur.Year < end.Year)
        {
            var yearEnd = new DateTime(cur.Year, 12, 31);
            yield return (cur, yearEnd);
            cur = yearEnd.AddDays(1);
        }
        yield return (cur, end);
    }
}
