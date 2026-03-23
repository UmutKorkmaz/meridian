using Meridian.Mapping;

namespace Meridian.EventDrivenCqrs;

public sealed class EventDrivenProfile : Profile
{
    public EventDrivenProfile()
    {
        CreateMap<OrderAggregate, OrderStatusDto>();
        CreateMap<OrderProjection, OrderProjectionDto>();
    }
}
