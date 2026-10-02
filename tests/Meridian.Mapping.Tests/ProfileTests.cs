using System.Reflection;
using System.Reflection.Emit;

namespace Meridian.Mapping.Tests;

public class ProfileTests
{
    // ── Profile implementations ─────────────────────────────────

    private class SourceDestProfile : Profile
    {
        public SourceDestProfile()
        {
            CreateMap<Source, Destination>();
            CreateMap<EmployeeSource, EmployeeDest>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.FullName))
                .ForMember(d => d.JobTitle, opt => opt.MapFrom(s => s.Title))
                .ForMember(d => d.Experience, opt => opt.MapFrom(s => s.YearsExperience));
        }
    }

    private class ProductProfile : Profile
    {
        public ProductProfile()
        {
            CreateMap<ProductSource, ProductDest>();
        }
    }

    private class OrderProfile : Profile
    {
        public OrderProfile()
        {
            CreateMap<OrderSource, OrderDest>();
            CreateMap<OrderItemSource, OrderItemDest>();
        }
    }

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public void Should_Register_Single_Profile_With_Multiple_Maps()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<SourceDestProfile>();
        });
        var mapper = config.CreateMapper();

        // Source -> Destination
        var src = new Source { Id = 1, Name = "Test" };
        var dest = mapper.Map<Source, Destination>(src);
        Assert.Equal(1, dest.Id);
        Assert.Equal("Test", dest.Name);

        // EmployeeSource -> EmployeeDest
        var emp = new EmployeeSource { FullName = "John", Title = "Dev", YearsExperience = 5 };
        var empDest = mapper.Map<EmployeeSource, EmployeeDest>(emp);
        Assert.Equal("John", empDest.Name);
        Assert.Equal("Dev", empDest.JobTitle);
    }

    [Fact]
    public void Should_Register_Multiple_Profiles()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<SourceDestProfile>();
            cfg.AddProfile<ProductProfile>();
            cfg.AddProfile<OrderProfile>();
        });
        var mapper = config.CreateMapper();

        // From SourceDestProfile
        var src = new Source { Id = 1 };
        Assert.Equal(1, mapper.Map<Source, Destination>(src).Id);

        // From ProductProfile
        var prod = new ProductSource { ProductName = "Widget", UnitPrice = 9.99m };
        var prodDest = mapper.Map<ProductSource, ProductDest>(prod);
        Assert.Equal("Widget", prodDest.ProductName);
        Assert.Equal(9.99m, prodDest.UnitPrice);

        // From OrderProfile
        var order = new OrderSource
        {
            OrderId = 10,
            Items = new List<OrderItemSource> { new() { ProductName = "A", Price = 5m } }
        };
        var orderDest = mapper.Map<OrderSource, OrderDest>(order);
        Assert.Equal(10, orderDest.OrderId);
        Assert.Single(orderDest.Items);
    }

    [Fact]
    public void Should_Add_Profile_Instance()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile(new ProductProfile());
        });
        var mapper = config.CreateMapper();

        var prod = new ProductSource { ProductName = "Test", UnitPrice = 1.5m };
        var result = mapper.Map<ProductSource, ProductDest>(prod);

        Assert.Equal("Test", result.ProductName);
        Assert.Equal(1.5m, result.UnitPrice);
    }

    [Fact]
    public void Should_Support_AllowNullCollections_In_Profile()
    {
        var profile = new SourceDestProfile();
        profile.AllowNullCollections = true;
        Assert.True(profile.AllowNullCollections);
    }

    [Fact]
    public void Should_Support_AllowNullDestinationValues_In_Profile()
    {
        var profile = new SourceDestProfile();
        Assert.True(profile.AllowNullDestinationValues); // default is true
        profile.AllowNullDestinationValues = false;
        Assert.False(profile.AllowNullDestinationValues);
    }
    [Fact]
    public void ScanningTwoProfileAssembliesRetainsBothMappings()
    {
        var first = CreateProfileAssembly<FirstAssemblySource, FirstAssemblyDto>("First");
        var second = CreateProfileAssembly<SecondAssemblySource, SecondAssemblyDto>("Second");
        var mapper = new MapperConfiguration(c => c.AddMaps(first, second)).CreateMapper();
        Assert.Equal(42, mapper.Map<FirstAssemblySource, FirstAssemblyDto>(new() { Value = 42 }).Value);
        Assert.Equal("second", mapper.Map<SecondAssemblySource, SecondAssemblyDto>(new() { Name = "second" }).Name);
    }

    private static AssemblyBuilder CreateProfileAssembly<TSource, TDestination>(string suffix)
    {
        // Real profile subclasses in separate collectible assemblies exercise assembly discovery.
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("MappingProfile" + suffix + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule("Profiles");
        var profile = module.DefineType("Profile" + suffix, TypeAttributes.Public | TypeAttributes.Sealed,
            typeof(Profile));
        var constructor = profile.DefineConstructor(MethodAttributes.Public,
            CallingConventions.Standard, Type.EmptyTypes);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Profile).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, Type.EmptyTypes, modifiers: null)!);
        il.Emit(OpCodes.Ldarg_0);
        var createMap = typeof(Profile).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(method => method.Name == "CreateMap" && method.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(TSource), typeof(TDestination));
        il.Emit(OpCodes.Call, createMap);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        profile.CreateType();
        return assembly;
    }
    public sealed class FirstAssemblySource { public int Value { get; set; } }
    public sealed class FirstAssemblyDto { public int Value { get; set; } }
    public sealed class SecondAssemblySource { public string Name { get; set; } = ""; }
    public sealed class SecondAssemblyDto { public string Name { get; set; } = ""; }
}
