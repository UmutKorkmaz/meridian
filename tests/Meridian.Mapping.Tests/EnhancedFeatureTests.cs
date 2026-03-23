using Meridian.Mapping.Queryable;

namespace Meridian.Mapping.Tests;

// ── Test Models for Enhanced Features ──────────────────────────────────

public class BeforeAfterSource
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class BeforeAfterDest
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
    public string Extra { get; set; } = string.Empty;
}

public class CircularNodeSource
{
    public string Label { get; set; } = string.Empty;
    public CircularNodeSource? Partner { get; set; }
}

public class CircularNodeDest
{
    public string Label { get; set; } = string.Empty;
    public CircularNodeDest? Partner { get; set; }
}

public class ProjectionSource
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class ProjectionDest
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class ProjectionCustomDest
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class ProjectionFlatSource
{
    public string Name { get; set; } = string.Empty;
    public ProjectionInnerSource Inner { get; set; } = new();
}

public class ProjectionInnerSource
{
    public string City { get; set; } = string.Empty;
}

public class ProjectionFlatDest
{
    public string Name { get; set; } = string.Empty;
    public string InnerCity { get; set; } = string.Empty;
}

public class TransformerSource
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class TransformerDest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class GenericWrapper<T>
{
    public T Value { get; set; } = default!;
    public string Label { get; set; } = string.Empty;
}

public class GenericWrapperDto<T>
{
    public T Value { get; set; } = default!;
    public string Label { get; set; } = string.Empty;
}

// ── BeforeMap Tests ────────────────────────────────────────────────────

public class BeforeMapTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Execute_BeforeMap_Action_Before_Mapping()
    {
        var beforeCalled = false;

        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<BeforeAfterSource, BeforeAfterDest>()
                .BeforeMap((src, dest) =>
                {
                    beforeCalled = true;
                });
        });

        var source = new BeforeAfterSource { Name = "Test", Value = 42 };
        var result = mapper.Map<BeforeAfterSource, BeforeAfterDest>(source);

        Assert.True(beforeCalled);
        Assert.Equal("Test", result.Name);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Should_Execute_Multiple_BeforeMap_Actions_In_Order()
    {
        var executionOrder = new List<int>();

        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<BeforeAfterSource, BeforeAfterDest>()
                .BeforeMap((src, dest) => executionOrder.Add(1))
                .BeforeMap((src, dest) => executionOrder.Add(2))
                .BeforeMap((src, dest) => executionOrder.Add(3));
        });

        var source = new BeforeAfterSource { Name = "Order", Value = 10 };
        mapper.Map<BeforeAfterSource, BeforeAfterDest>(source);

        Assert.Equal(new List<int> { 1, 2, 3 }, executionOrder);
    }

    [Fact]
    public void Should_Allow_Modifying_Source_In_BeforeMap()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<BeforeAfterSource, BeforeAfterDest>()
                .BeforeMap((src, dest) =>
                {
                    // Modify source before mapping occurs
                    src.Name = src.Name.ToUpper();
                });
        });

        var source = new BeforeAfterSource { Name = "hello", Value = 5 };
        var result = mapper.Map<BeforeAfterSource, BeforeAfterDest>(source);

        Assert.Equal("HELLO", result.Name);
    }
}

// ── AfterMap Tests ─────────────────────────────────────────────────────

public class AfterMapTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Execute_AfterMap_Action_After_Mapping()
    {
        var afterCalled = false;

        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<BeforeAfterSource, BeforeAfterDest>()
                .AfterMap((src, dest) =>
                {
                    afterCalled = true;
                });
        });

        var source = new BeforeAfterSource { Name = "Test", Value = 42 };
        var result = mapper.Map<BeforeAfterSource, BeforeAfterDest>(source);

        Assert.True(afterCalled);
        Assert.Equal("Test", result.Name);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Should_Execute_Multiple_AfterMap_Actions_In_Order()
    {
        var executionOrder = new List<int>();

        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<BeforeAfterSource, BeforeAfterDest>()
                .AfterMap((src, dest) => executionOrder.Add(1))
                .AfterMap((src, dest) => executionOrder.Add(2))
                .AfterMap((src, dest) => executionOrder.Add(3));
        });

        var source = new BeforeAfterSource { Name = "Order", Value = 10 };
        mapper.Map<BeforeAfterSource, BeforeAfterDest>(source);

        Assert.Equal(new List<int> { 1, 2, 3 }, executionOrder);
    }

    [Fact]
    public void Should_Allow_Modifying_Destination_In_AfterMap()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<BeforeAfterSource, BeforeAfterDest>()
                .AfterMap((src, dest) =>
                {
                    // Modify destination after mapping
                    dest.Extra = $"{src.Name}_{src.Value}";
                });
        });

        var source = new BeforeAfterSource { Name = "Item", Value = 99 };
        var result = mapper.Map<BeforeAfterSource, BeforeAfterDest>(source);

        Assert.Equal("Item", result.Name);
        Assert.Equal(99, result.Value);
        Assert.Equal("Item_99", result.Extra);
    }
}

// ── BeforeMap + AfterMap Combined Tests ────────────────────────────────

public class BeforeAfterMapCombinedTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Execute_BeforeMap_Then_AfterMap_In_Correct_Order()
    {
        var executionOrder = new List<string>();

        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<BeforeAfterSource, BeforeAfterDest>()
                .BeforeMap((src, dest) => executionOrder.Add("before1"))
                .BeforeMap((src, dest) => executionOrder.Add("before2"))
                .AfterMap((src, dest) => executionOrder.Add("after1"))
                .AfterMap((src, dest) => executionOrder.Add("after2"));
        });

        var source = new BeforeAfterSource { Name = "Test", Value = 1 };
        mapper.Map<BeforeAfterSource, BeforeAfterDest>(source);

        Assert.Equal(new List<string> { "before1", "before2", "after1", "after2" }, executionOrder);
    }
}

// ── PreserveReferences Tests ───────────────────────────────────────────

public class PreserveReferencesTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Handle_Circular_Reference_With_PreserveReferences()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<CircularNodeSource, CircularNodeDest>()
                .PreserveReferences();
        });

        var nodeA = new CircularNodeSource { Label = "A" };
        var nodeB = new CircularNodeSource { Label = "B" };
        nodeA.Partner = nodeB;
        nodeB.Partner = nodeA; // circular reference

        var result = mapper.Map<CircularNodeSource, CircularNodeDest>(nodeA);

        Assert.Equal("A", result.Label);
        Assert.NotNull(result.Partner);
        Assert.Equal("B", result.Partner!.Label);
        // The circular reference should point back to the same destination object
        Assert.NotNull(result.Partner.Partner);
        Assert.Same(result, result.Partner.Partner);
    }

    [Fact]
    public void Should_Return_Same_Destination_For_Same_Source_Object()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<PersonSource, PersonDest>()
                .PreserveReferences();
            cfg.CreateMap<Address, Address>();
        });

        // Create a shared source address
        var sharedAddress = new Address { Street = "123 Main St", City = "Portland", ZipCode = "97201" };
        var person1 = new PersonSource { Name = "Person1", Address = sharedAddress };

        // Map two different sources that share the same Address object
        // With PreserveReferences, the second encounter should return the cached mapping
        var result1 = mapper.Map<PersonSource, PersonDest>(person1);

        Assert.Equal("Person1", result1.Name);
        Assert.Equal("123 Main St", result1.Address.Street);
    }
}

// ── ProjectTo / IQueryable Tests ───────────────────────────────────────

public class ProjectToTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Project_Simple_Properties()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<ProjectionSource, ProjectionDest>();
        });
        var mapper = config.CreateMapper();

        var sources = new List<ProjectionSource>
        {
            new() { Id = 1, FirstName = "Alice", LastName = "Smith", Age = 30 },
            new() { Id = 2, FirstName = "Bob", LastName = "Jones", Age = 25 },
        }.AsQueryable();

        var projected = mapper.ProjectTo<ProjectionDest>(sources);
        var results = projected.ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].Id);
        Assert.Equal("Alice", results[0].FirstName);
        Assert.Equal("Smith", results[0].LastName);
        Assert.Equal(30, results[0].Age);
        Assert.Equal(2, results[1].Id);
        Assert.Equal("Bob", results[1].FirstName);
        Assert.Equal(25, results[1].Age);
    }

    [Fact]
    public void Should_Project_With_Custom_MapFrom_Expression()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<ProjectionSource, ProjectionCustomDest>()
                .ForMember(d => d.FullName, opt => opt.MapFrom(s => s.FirstName + " " + s.LastName));
        });
        var mapper = config.CreateMapper();

        var sources = new List<ProjectionSource>
        {
            new() { Id = 1, FirstName = "Alice", LastName = "Smith", Age = 30 },
            new() { Id = 2, FirstName = "Bob", LastName = "Jones", Age = 25 },
        }.AsQueryable();

        var projected = mapper.ProjectTo<ProjectionCustomDest>(sources);
        var results = projected.ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Alice Smith", results[0].FullName);
        Assert.Equal(30, results[0].Age);
        Assert.Equal("Bob Jones", results[1].FullName);
        Assert.Equal(25, results[1].Age);
    }

    [Fact]
    public void Should_Project_With_Flattened_Properties()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<ProjectionFlatSource, ProjectionFlatDest>();
        });
        var mapper = config.CreateMapper();

        var sources = new List<ProjectionFlatSource>
        {
            new() { Name = "Alice", Inner = new ProjectionInnerSource { City = "Portland" } },
            new() { Name = "Bob", Inner = new ProjectionInnerSource { City = "Seattle" } },
        }.AsQueryable();

        var projected = mapper.ProjectTo<ProjectionFlatDest>(sources);
        var results = projected.ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Alice", results[0].Name);
        Assert.Equal("Portland", results[0].InnerCity);
        Assert.Equal("Bob", results[1].Name);
        Assert.Equal("Seattle", results[1].InnerCity);
    }

    [Fact]
    public void Should_Project_Empty_Queryable()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<ProjectionSource, ProjectionDest>();
        });
        var mapper = config.CreateMapper();

        var sources = new List<ProjectionSource>().AsQueryable();
        var projected = mapper.ProjectTo<ProjectionDest>(sources);
        var results = projected.ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Should_Throw_For_Missing_ProjectTo_Configuration()
    {
        var config = new MapperConfiguration(cfg => { });
        var mapper = config.CreateMapper();

        var sources = new List<ProjectionSource>().AsQueryable();

        // ProjectTo uses reflection internally, so the InvalidOperationException
        // may be wrapped in a TargetInvocationException
        var ex = Assert.ThrowsAny<Exception>(() =>
            mapper.ProjectTo<ProjectionDest>(sources).ToList());

        // Unwrap if wrapped in TargetInvocationException
        var actual = ex is System.Reflection.TargetInvocationException tie
            ? tie.InnerException!
            : ex;

        Assert.IsType<InvalidOperationException>(actual);
        Assert.Contains("Missing mapping configuration", actual.Message);
    }

    [Fact]
    public void Should_Project_Using_ExpressionBuilder_Directly()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<ProjectionSource, ProjectionDest>();
        });

        var expression = ExpressionBuilder.BuildProjection<ProjectionSource, ProjectionDest>(config);

        Assert.NotNull(expression);

        // Compile and execute the expression manually
        var compiled = expression.Compile();
        var source = new ProjectionSource { Id = 42, FirstName = "Direct", LastName = "Test", Age = 99 };
        var result = compiled(source);

        Assert.Equal(42, result.Id);
        Assert.Equal("Direct", result.FirstName);
        Assert.Equal("Test", result.LastName);
        Assert.Equal(99, result.Age);
    }
}

// ── Value Transformers Tests ───────────────────────────────────────────

public class ValueTransformerTests
{
    [Fact]
    public void Should_Apply_String_Trim_Value_Transformer()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.ValueTransformers.Add<string>(s => s.Trim());
            cfg.CreateMap<TransformerSource, TransformerDest>();
        });
        var mapper = config.CreateMapper();

        var source = new TransformerSource
        {
            Name = "  Alice  ",
            Description = "  Trimmed description  "
        };

        var result = mapper.Map<TransformerSource, TransformerDest>(source);

        Assert.Equal("Alice", result.Name);
        Assert.Equal("Trimmed description", result.Description);
    }

    [Fact]
    public void Should_Apply_Multiple_Value_Transformers()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.ValueTransformers.Add<string>(s => s.Trim());
            cfg.ValueTransformers.Add<string>(s => s.ToUpper());
            cfg.CreateMap<TransformerSource, TransformerDest>();
        });
        var mapper = config.CreateMapper();

        var source = new TransformerSource
        {
            Name = "  alice  ",
            Description = "  hello world  "
        };

        var result = mapper.Map<TransformerSource, TransformerDest>(source);

        // First trim, then uppercase
        Assert.Equal("ALICE", result.Name);
        Assert.Equal("HELLO WORLD", result.Description);
    }

    [Fact]
    public void Should_Not_Apply_Transformer_To_NonMatching_Types()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.ValueTransformers.Add<string>(s => s.ToUpper());
            cfg.CreateMap<Source, Destination>();
        });
        var mapper = config.CreateMapper();

        var source = new Source { Id = 42, Name = "hello", Age = 25 };
        var result = mapper.Map<Source, Destination>(source);

        // String should be transformed
        Assert.Equal("HELLO", result.Name);
        // Int should NOT be affected
        Assert.Equal(42, result.Id);
        Assert.Equal(25, result.Age);
    }
}

// ── Open Generic Mapping Tests ─────────────────────────────────────────

public class OpenGenericMappingTests
{
    [Fact]
    public void Should_Map_Open_Generic_Types()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap(typeof(GenericWrapper<>), typeof(GenericWrapperDto<>));
        });
        var mapper = config.CreateMapper();

        var source = new GenericWrapper<int> { Value = 42, Label = "Answer" };
        var result = mapper.Map<GenericWrapper<int>, GenericWrapperDto<int>>(source);

        Assert.Equal(42, result.Value);
        Assert.Equal("Answer", result.Label);
    }

    [Fact]
    public void Should_Map_Open_Generic_Types_With_String()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap(typeof(GenericWrapper<>), typeof(GenericWrapperDto<>));
        });
        var mapper = config.CreateMapper();

        var source = new GenericWrapper<string> { Value = "Hello", Label = "Greeting" };
        var result = mapper.Map<GenericWrapper<string>, GenericWrapperDto<string>>(source);

        Assert.Equal("Hello", result.Value);
        Assert.Equal("Greeting", result.Label);
    }

    [Fact]
    public void Should_Map_Different_Closed_Generics_From_Same_Open_Definition()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap(typeof(GenericWrapper<>), typeof(GenericWrapperDto<>));
        });
        var mapper = config.CreateMapper();

        // Map int variant
        var intSource = new GenericWrapper<int> { Value = 100, Label = "Int" };
        var intResult = mapper.Map<GenericWrapper<int>, GenericWrapperDto<int>>(intSource);

        Assert.Equal(100, intResult.Value);
        Assert.Equal("Int", intResult.Label);

        // Map string variant using the same config
        var strSource = new GenericWrapper<string> { Value = "text", Label = "Str" };
        var strResult = mapper.Map<GenericWrapper<string>, GenericWrapperDto<string>>(strSource);

        Assert.Equal("text", strResult.Value);
        Assert.Equal("Str", strResult.Label);
    }
}
