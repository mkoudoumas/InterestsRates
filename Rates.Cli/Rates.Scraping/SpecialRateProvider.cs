using System.Collections.Generic;
using System.Threading.Tasks;
using Rates.Core;

namespace Rates.Scraping
{
    public sealed class SpecialRateProvider : IRateProvider
    {
        public Task<IReadOnlyList<RatePeriod>> GetPeriodsAsync()
        {
            // Placeholder for future logic.
            // Returning empty list keeps the app running without data.
            return Task.FromResult<IReadOnlyList<RatePeriod>>(new List<RatePeriod>());
        }
    }
}
