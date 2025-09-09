using System;

namespace Rates.Core;

public sealed record InterestSlice(
    DateTime From,
    DateTime To,
    decimal AnnualRatePercent,
    int Days,
    decimal Interest
);
