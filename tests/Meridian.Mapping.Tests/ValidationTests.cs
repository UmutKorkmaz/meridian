using Meridian.Mapping.Configuration;

namespace Meridian.Mapping.Tests;

public class ValidationTests
{
    [Fact]
    public void Should_Pass_Validation_For_Fully_Mapped_Config()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Source, Destination>();
        });

        // Should not throw
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Should_Throw_For_Unmapped_Destination_Members()
    {
        var config = new MapperConfiguration(cfg =>
        {
            // EmployeeSource -> EmployeeDest: Name, JobTitle, Experience have no matching source props
            cfg.CreateMap<EmployeeSource, EmployeeDest>();
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.AssertConfigurationIsValid());

        Assert.Contains("Unmapped destination member", ex.Message);
    }

    [Fact]
    public void Should_Not_Fail_For_Ignored_Members()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EmployeeSource, EmployeeDest>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.FullName))
                .ForMember(d => d.JobTitle, opt => opt.MapFrom(s => s.Title))
                .ForMember(d => d.Experience, opt => opt.Ignore());
        });

        // Should not throw - Experience is ignored
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Should_Not_Fail_When_ConvertUsing_Bypasses_Validation()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EmployeeSource, EmployeeDest>()
                .ConvertUsing(s => new EmployeeDest
                {
                    Name = s.FullName,
                    JobTitle = s.Title,
                    Experience = s.YearsExperience
                });
        });

        // ConvertUsing means no PropertyMaps, so no unmapped members
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Should_Not_Fail_When_ValidateMemberList_Is_None()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EmployeeSource, EmployeeDest>()
                .ValidateMemberList(MemberList.None);
        });

        // MemberList.None skips validation entirely
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Should_Return_All_TypeMaps()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Source, Destination>();
            cfg.CreateMap<EmployeeSource, EmployeeDest>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.FullName))
                .ForMember(d => d.JobTitle, opt => opt.MapFrom(s => s.Title))
                .ForMember(d => d.Experience, opt => opt.MapFrom(s => s.YearsExperience));
        });

        var typeMaps = config.GetAllTypeMaps();
        Assert.Equal(2, typeMaps.Count);
    }
}
