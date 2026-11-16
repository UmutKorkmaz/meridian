using Meridian.Mapping;

namespace Meridian.EventDrivenCqrs;

public sealed class EventDrivenProfile : Profile
{
    public EventDrivenProfile()
    {
        CreateMap<OrderAggregate, OrderStatusDto>();
        CreateMap<OrderProjection, OrderProjectionDto>()
            .ForCtorParam(nameof(OrderProjectionDto.EventCount), opt => opt.MapFrom(src => src.Events.Length));
    }
}
