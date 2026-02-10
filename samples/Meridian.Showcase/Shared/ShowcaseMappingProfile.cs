using Meridian.Mapping;
using Meridian.Mapping.Converters;
using Meridian.Mapping.Execution;

namespace Meridian.Showcase;

public sealed class ShowcaseMappingProfile : Profile
{
    public ShowcaseMappingProfile()
    {
        CreateMap<OrderSource, OrderView>()
            .ForMember(dest => dest.CustomerName, opt => opt.MapFrom(src => $"{src.FirstName} {src.LastName}"))
            .ForMember(dest => dest.FinalTotal, opt => opt.MapFrom<OrderMarkupResolver, decimal>(src => src.Subtotal))
            .ForMember(dest => dest.Tags, opt => opt.MapFrom(src => src.Tags))
            .ForPath(dest => dest.Shipping.Country, opt => opt.MapFrom(src => src.Shipping.Country))
            .ForPath(dest => dest.Shipping.City, opt => opt.MapFrom(src => src.Shipping.City))
            .ForAllOtherMembers(opt => opt.Ignore());

        CreateMap<CustomerEnvelope, CustomerCard>()
            .IncludeMembers(src => src.Profile);
        CreateMap<CustomerProfile, CustomerCard>();

        CreateMap<ProductEntity, ProductDto>().ReverseMap();
        CreateMap<ProductEntity, ProductRow>()
            .ValidateMemberList(MemberList.Source);

        CreateMap<CatalogItemEntity, CatalogItemDto>();
    }
}

public sealed class ShowcaseAssemblyMarker;
