using Meridian.Mapping;
using Meridian.Mapping.Execution;

namespace Meridian.Showcase;

public sealed class OrderMarkupResolver : IMemberValueResolver<OrderSource, OrderView, decimal, decimal>
{
    public decimal Resolve(
        OrderSource source,
        OrderView destination,
        decimal sourceMember,
        decimal destMember,
        ResolutionContext context)
    {
        return decimal.Round(sourceMember * 1.20m, 2);
    }
}
