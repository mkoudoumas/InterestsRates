using System;

namespace Rates.Core;

public sealed record RatePeriod(
    DateTime Start,
    DateTime End,
    decimal Contractual, // Contractual interest rate (annual %)
    decimal Overdue      // Overdue interest rate (annual %)
);
