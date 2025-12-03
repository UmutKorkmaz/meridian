using Meridian.Mapping.Converters;
using Meridian.Mapping.Configuration;
using Meridian.Mapping.Execution;
using Meridian.Mapping.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mapping.Tests;

public class AssemblyScanSourceOne
{
    public string Name { get; set; } = string.Empty;
}

public class AssemblyScanDestOne
{
    public string Name { get; set; } = string.Empty;
}

public class AssemblyScanSourceTwo
{
    public int Count { get; set; }
}

public class AssemblyScanDestTwo
{
    public int Count { get; set; }
}

public class AssemblyScanProfileOne : Profile
{
    public AssemblyScanProfileOne()
    {
        CreateMap<AssemblyScanSourceOne, AssemblyScanDestOne>();
    }
}

public class AssemblyScanProfileTwo : Profile
{
    public AssemblyScanProfileTwo()
    {
        CreateMap<AssemblyScanSourceTwo, AssemblyScanDestTwo>();
    }
}

public class ForAllOtherMembersSource
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class ForAllOtherMembersDest
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; } = 99;
    public string Notes { get; set; } = "seed";
}

public class SourceMemberValidationSource
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string UnmappedSource { get; set; } = string.Empty;
}

public class SourceMemberValidationDest
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class MemberResolverSource
{
    public int Value { get; set; }
}

public class MemberResolverDest
{
    public int Computed { get; set; }
}

public class MemberResolver : IMemberValueResolver<MemberResolverSource, MemberResolverDest, int, int>
{
    public int Resolve(
        MemberResolverSource source,
        MemberResolverDest destination,
        int sourceMember,
        int destMember,
        ResolutionContext context)
    {
        return sourceMember * 2 + destMember;
    }
}

public class MemberResolverProfile : Profile
{
    public MemberResolverProfile()
    {
        CreateMap<MemberResolverSource, MemberResolverDest>()
            .ForMember(d => d.Computed, opt => opt.MapFrom<MemberResolver, int>(s => s.Value));
    }
}

public class ProfileNullBehaviorSource
{
    public List<string>? Tags { get; set; }
}

public class ProfileNullBehaviorDest
{
    public List<string>? Tags { get; set; } = new() { "seed" };
}

public class ProfileNullValueSource
{
    public string? Name { get; set; }
}

public class ProfileNullValueDest
{
    public string? Name { get; set; } = "keep";
}

public class ProfileNullBehaviorProfile : Profile
{
    public ProfileNullBehaviorProfile()
    {
        AllowNullCollections = true;
        AllowNullDestinationValues = false;

        CreateMap<ProfileNullBehaviorSource, ProfileNullBehaviorDest>();
        CreateMap<ProfileNullValueSource, ProfileNullValueDest>();
    }
}

public class CoverageFeatureTests
{
    [Fact]
    public void AddProfiles_AssemblyScan_Registers_All_Profile_Maps()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddProfiles(typeof(AssemblyScanProfileOne).Assembly);
        });

        var mapper = config.CreateMapper();

        var first = mapper.Map<AssemblyScanSourceOne, AssemblyScanDestOne>(
            new AssemblyScanSourceOne { Name = "first" });
        var second = mapper.Map<AssemblyScanSourceTwo, AssemblyScanDestTwo>(
            new AssemblyScanSourceTwo { Count = 42 });

        Assert.Equal("first", first.Name);
        Assert.Equal(42, second.Count);
    }

    [Fact]
    public void ForAllOtherMembers_Ignore_Preserves_Unconfigured_Members()
    {
        var mapper = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<ForAllOtherMembersSource, ForAllOtherMembersDest>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name))
                .ForAllOtherMembers(opt => opt.Ignore());
        }).CreateMapper();

        var result = mapper.Map<ForAllOtherMembersSource, ForAllOtherMembersDest>(
            new ForAllOtherMembersSource { Name = "mapped", Age = 21 });

        Assert.Equal("mapped", result.Name);
        Assert.Equal(99, result.Age);
        Assert.Equal("seed", result.Notes);
    }

    [Fact]
    public void ValidateMemberList_Source_Fails_On_Unmapped_Source_Member()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<SourceMemberValidationSource, SourceMemberValidationDest>()
                .ValidateMemberList(MemberList.Source);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => config.AssertConfigurationIsValid());

        Assert.Contains("Unmapped source member", ex.Message);
        Assert.Contains("UnmappedSource", ex.Message);
    }

    [Fact]
    public void MemberValueResolver_Is_Resolved_From_DI_And_Applied()
    {
        var services = new ServiceCollection();
        services.AddMeridianMapping(typeof(MemberResolverProfile).Assembly);

        var provider = services.BuildServiceProvider();
        var mapper = provider.GetRequiredService<IMapper>();

        var result = mapper.Map<MemberResolverSource, MemberResolverDest>(
            new MemberResolverSource { Value = 21 });

        Assert.Equal(42, result.Computed);
    }

    [Fact]
    public void Profile_Level_AllowNullCollections_Propagates_To_Mapping()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<ProfileNullBehaviorProfile>();
        });

        var mapper = config.CreateMapper();

        var result = mapper.Map<ProfileNullBehaviorSource, ProfileNullBehaviorDest>(
            new ProfileNullBehaviorSource { Tags = null });

        Assert.Null(result.Tags);
    }

    [Fact]
    public void Profile_Level_AllowNullDestinationValues_Preserves_Existing_Destination_Value()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<ProfileNullBehaviorProfile>();
        });

        var mapper = config.CreateMapper();
        var destination = new ProfileNullValueDest { Name = "keep" };

        var result = mapper.Map(
            new ProfileNullValueSource { Name = null },
            destination);

        Assert.Same(destination, result);
        Assert.Equal("keep", result.Name);
    }
}
