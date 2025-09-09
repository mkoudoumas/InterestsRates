using System.Collections.Generic;
using System.Threading.Tasks;

namespace Rates.Core;

public interface IRateProvider
{
    Task<IReadOnlyList<RatePeriod>> GetPeriodsAsync();
}
