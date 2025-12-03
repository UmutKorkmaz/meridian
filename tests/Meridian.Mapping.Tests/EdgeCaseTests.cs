namespace Meridian.Mapping.Tests;

public class EdgeCaseTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Map_Enum_Same_Values()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<StatusSource, StatusDest>());

        var source = new StatusSource { Status = SourceStatus.Active };
        var result = mapper.Map<StatusSource, StatusDest>(source);

        Assert.Equal(DestStatus.Active, result.Status);
    }

    [Fact]
    public void Should_Map_Enum_By_Integer_Value()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<StatusSource, StatusDest>());

        var source = new StatusSource { Status = SourceStatus.Pending };
        var result = mapper.Map<StatusSource, StatusDest>(source);

        Assert.Equal(DestStatus.Pending, result.Status);
    }

    [Fact]
    public void Should_Map_Nullable_To_Nullable()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<NullableSource, NullableDest>());

        var source = new NullableSource { Value = 42, Text = "hello", Date = new DateTime(2025, 1, 1) };
        var result = mapper.Map<NullableSource, NullableDest>(source);

        Assert.Equal(42, result.Value);
        Assert.Equal("hello", result.Text);
        Assert.Equal(new DateTime(2025, 1, 1), result.Date);
    }

    [Fact]
    public void Should_Map_Nullable_Null_To_Nullable()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<NullableSource, NullableDest>());

        var source = new NullableSource { Value = null, Text = null, Date = null };
        var result = mapper.Map<NullableSource, NullableDest>(source);

        Assert.Null(result.Value);
        Assert.Null(result.Text);
        Assert.Null(result.Date);
    }

    [Fact]
    public void Should_Map_NonNullable_To_Nullable()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<NonNullableSource, NullableIntDest>());

        var source = new NonNullableSource { Value = 10 };
        var result = mapper.Map<NonNullableSource, NullableIntDest>(source);

        Assert.Equal(10, result.Value);
    }

    [Fact]
    public void Should_MaxDepth_Prevent_Infinite_Recursion()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<TreeNode, TreeNodeDto>().MaxDepth(2);
        });

        var root = new TreeNode { Value = "root" };
        var child = new TreeNode { Value = "child", Parent = root };
        root.Children.Add(child);
        var grandchild = new TreeNode { Value = "grandchild", Parent = child };
        child.Children.Add(grandchild);

        var result = mapper.Map<TreeNode, TreeNodeDto>(root);

        Assert.Equal("root", result.Value);
        Assert.Single(result.Children);
        Assert.Equal("child", result.Children[0].Value);
    }

    [Fact]
    public void Should_Map_With_All_Properties_Ignored()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, AllIgnoredDest>()
                .ForMember(d => d.Name, opt => opt.Ignore())
                .ForMember(d => d.Age, opt => opt.Ignore());
        });

        var source = new Source { Name = "Test", Age = 30 };
        var result = mapper.Map<Source, AllIgnoredDest>(source);

        Assert.Equal(string.Empty, result.Name); // default initializer
        Assert.Equal(0, result.Age); // default int
    }

    [Fact]
    public void Should_Handle_String_To_String_Directly()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        var source = new Source { Name = "test string" };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal("test string", result.Name);
    }

    [Fact]
    public void Should_Map_Int_Properties_At_Boundary_Values()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        var source = new Source { Id = int.MaxValue, Age = int.MinValue };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal(int.MaxValue, result.Id);
        Assert.Equal(int.MinValue, result.Age);
    }

    [Fact]
    public void Should_Map_Assignable_Types_Without_Config()
    {
        var mapper = CreateMapper(cfg => { });

        var source = "hello";
        var result = mapper.Map<string, string>(source);

        Assert.Equal("hello", result);
    }
}
