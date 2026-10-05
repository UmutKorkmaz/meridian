using Meridian.Mapping;
using Meridian.Mapping.Execution;

namespace Meridian.Mapping.Tests;

public class ConversionConsistencyTests
{
    private enum Role
    {
        Guest = 0,
        User = 1,
        Admin = 2
    }

    [Flags]
    private enum Permissions
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4
    }

    [Flags]
    private enum ReadWriteAccess
    {
        Read = 1,
        Write = 2
    }

    [Fact]
    public void FastPathAndInterpreterAgreeForSupportedConversionMatrix()
    {
        var fastConfiguration = new MapperConfiguration(c =>
        {
            c.CreateMap<FastNestedSource, FastNestedDestination>();
            c.CreateMap<FastConversionSource, FastConversionDestination>()
                .ForMember(d => d.RoleFromString, o => o.MapFrom(s => s.RoleName))
                .ForMember(d => d.RoleFromNumber, o => o.MapFrom(s => s.RoleNumber))
                .ForMember(d => d.NameFromRole, o => o.MapFrom(s => s.Role))
                .ForMember(d => d.NumberFromNullable, o => o.MapFrom(s => s.NullableNumber))
                .ForMember(d => d.NullableFromNumber, o => o.MapFrom(s => s.Number))
                .ForMember(d => d.NullableFromNullable, o => o.MapFrom(s => s.NullableNumber))
                .ForMember(d => d.WidenedNumber, o => o.MapFrom(s => s.Number))
                .ForMember(d => d.NarrowedNumber, o => o.MapFrom(s => s.NarrowingNumber))
                .ForMember(d => d.NumberFromString, o => o.MapFrom(s => s.NumberText));
        });
        var interpreterConfiguration = new MapperConfiguration(c =>
        {
            c.CreateMap<InterpreterNestedSource, InterpreterNestedDestination>();
            c.CreateMap<InterpreterConversionSource, InterpreterConversionDestination>()
                .ForMember(d => d.RoleFromString, o => o.MapFrom(s => s.RoleName))
                .ForMember(d => d.RoleFromNumber, o => o.MapFrom(s => s.RoleNumber))
                .ForMember(d => d.NameFromRole, o => o.MapFrom(s => s.Role))
                .ForMember(d => d.NumberFromNullable, o => o.MapFrom(s => s.NullableNumber))
                .ForMember(d => d.NullableFromNumber, o => o.MapFrom(s => s.Number))
                .ForMember(d => d.NullableFromNullable, o => o.MapFrom(s => s.NullableNumber))
                .ForMember(d => d.WidenedNumber, o => o.MapFrom(s => s.Number))
                .ForMember(d => d.NarrowedNumber, o => o.MapFrom(s => s.NarrowingNumber))
                .ForMember(d => d.NumberFromString, o => o.MapFrom(s => s.NumberText));
        });

        AssertFastPath(fastConfiguration, typeof(FastConversionSource), typeof(FastConversionDestination));
        AssertInterpreterPath(interpreterConfiguration, typeof(InterpreterConversionSource), typeof(InterpreterConversionDestination));

        var fastMapper = fastConfiguration.CreateMapper();
        var interpreterMapper = interpreterConfiguration.CreateMapper();

        var fast = fastMapper.Map<FastConversionSource, FastConversionDestination>(new FastConversionSource
        {
            RoleName = "aDmIn",
            RoleNumber = 2,
            Role = Role.Admin,
            NullableNumber = 17,
            Number = 42,
            NarrowingNumber = 42,
            NumberText = "84",
            Nested = new FastNestedSource { Value = 9 }
        });
        var interpreter = interpreterMapper.Map<InterpreterConversionSource, InterpreterConversionDestination>(new InterpreterConversionSource
        {
            RoleName = "aDmIn",
            RoleNumber = 2,
            Role = Role.Admin,
            NullableNumber = 17,
            Number = 42,
            NarrowingNumber = 42,
            NumberText = "84",
            Nested = new InterpreterNestedSource { Value = 9 },
            PathDisqualifier = [1]
        });

        Assert.Equal(interpreter.RoleFromString, fast.RoleFromString);
        Assert.Equal(interpreter.RoleFromNumber, fast.RoleFromNumber);
        Assert.Equal(interpreter.NameFromRole, fast.NameFromRole);
        Assert.Equal(interpreter.NumberFromNullable, fast.NumberFromNullable);
        Assert.Equal(interpreter.NullableFromNumber, fast.NullableFromNumber);
        Assert.Equal(interpreter.NullableFromNullable, fast.NullableFromNullable);
        Assert.Equal(interpreter.WidenedNumber, fast.WidenedNumber);
        Assert.Equal(interpreter.NarrowedNumber, fast.NarrowedNumber);
        Assert.Equal(interpreter.NumberFromString, fast.NumberFromString);
        Assert.Equal(interpreter.Nested.Value, fast.Nested.Value);

        var fastCollection = fastMapper.Map<List<FastNestedSource>, List<FastNestedDestination>>(
            [new FastNestedSource { Value = 3 }, new FastNestedSource { Value = 5 }]);
        var interpreterCollection = interpreterMapper.Map<List<InterpreterNestedSource>, List<InterpreterNestedDestination>>(
            [new InterpreterNestedSource { Value = 3 }, new InterpreterNestedSource { Value = 5 }]);

        Assert.Equal(interpreterCollection.Select(item => item.Value), fastCollection.Select(item => item.Value));

        var fastOverflow = Assert.Throws<OverflowException>(() =>
            fastMapper.Map<FastConversionSource, FastConversionDestination>(new()
            {
                RoleName = "Admin",
                RoleNumber = 2,
                Role = Role.Admin,
                NullableNumber = 17,
                Number = 42,
                NarrowingNumber = 300,
                NumberText = "84",
                Nested = new FastNestedSource { Value = 9 }
            }));
        var interpreterOverflow = Assert.Throws<OverflowException>(() =>
            interpreterMapper.Map<InterpreterConversionSource, InterpreterConversionDestination>(new()
            {
                RoleName = "Admin",
                RoleNumber = 2,
                Role = Role.Admin,
                NullableNumber = 17,
                Number = 42,
                NarrowingNumber = 300,
                NumberText = "84",
                Nested = new InterpreterNestedSource { Value = 9 },
                PathDisqualifier = [1]
            }));

        Assert.Equal(interpreterOverflow.GetType(), fastOverflow.GetType());

        var fastNull = fastMapper.Map<FastConversionSource, FastConversionDestination>(new()
        {
            RoleName = "Admin",
            RoleNumber = 2,
            Role = Role.Admin,
            NullableNumber = null,
            Number = 42,
            NarrowingNumber = 42,
            NumberText = "84",
            Nested = new FastNestedSource { Value = 9 }
        });
        var interpreterNull = interpreterMapper.Map<InterpreterConversionSource, InterpreterConversionDestination>(new()
        {
            RoleName = "Admin",
            RoleNumber = 2,
            Role = Role.Admin,
            NullableNumber = null,
            Number = 42,
            NarrowingNumber = 42,
            NumberText = "84",
            Nested = new InterpreterNestedSource { Value = 9 },
            PathDisqualifier = [1]
        });

        Assert.Equal(interpreterNull.NumberFromNullable, fastNull.NumberFromNullable);
        Assert.Equal(0, fastNull.NumberFromNullable);
        Assert.Null(fastNull.NullableFromNullable);
        Assert.Equal(interpreterNull.NullableFromNullable, fastNull.NullableFromNullable);
    }

    [Fact]
    public void UndefinedNumericEnumStringIsRejectedWithTypedMappingException()
    {
        var fastMapper = CreateEnumMapper();
        var interpreterMapper = new MapperConfiguration(c =>
            c.CreateMap<InterpreterEnumTextSource, InterpreterEnumDestination>()).CreateMapper();

        var fastException = Assert.Throws<MeridianEnumMappingException>(() =>
            fastMapper.Map<EnumTextSource, EnumDestination>(new() { Role = "999" }));
        var interpreterException = Assert.Throws<MeridianEnumMappingException>(() =>
            interpreterMapper.Map<InterpreterEnumTextSource, InterpreterEnumDestination>(new()
            {
                Role = "999",
                PathDisqualifier = [1]
            }));

        AssertEnumException(fastException, nameof(EnumDestination.Role));
        AssertEnumException(interpreterException, nameof(InterpreterEnumDestination.Role));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("aDmIn")]
    public void DefinedEnumNamesAreMappedCaseInsensitively(string value)
    {
        var result = CreateEnumMapper().Map<EnumTextSource, EnumDestination>(new() { Role = value });

        Assert.Equal(Role.Admin, result.Role);
    }

    [Fact]
    public void DefinedNumericEnumStringIsMapped()
    {
        var result = CreateEnumMapper().Map<EnumTextSource, EnumDestination>(new() { Role = "2" });

        Assert.Equal(Role.Admin, result.Role);
    }

    [Fact]
    public void FlagsEnumCombinationsOfDefinedNamesAreMapped()
    {
        var mapper = new MapperConfiguration(c => c.CreateMap<FlagsTextSource, FlagsDestination>()).CreateMapper();

        var result = mapper.Map<FlagsTextSource, FlagsDestination>(new() { Permissions = "read, WRITE" });

        Assert.Equal(Permissions.Read | Permissions.Write, result.Permissions);
    }

    [Fact]
    public void FlagsEnumNumericLiteralsAcceptDefinedBitsAndRejectUndefinedBits()
    {
        var mapper = new MapperConfiguration(c => c.CreateMap<ReadWriteAccessTextSource, ReadWriteAccessDestination>()).CreateMapper();

        var result = mapper.Map<ReadWriteAccessTextSource, ReadWriteAccessDestination>(new() { Access = "3" });
        var exception = Assert.Throws<MeridianEnumMappingException>(() =>
            mapper.Map<ReadWriteAccessTextSource, ReadWriteAccessDestination>(new() { Access = "5" }));

        Assert.Equal(ReadWriteAccess.Read | ReadWriteAccess.Write, result.Access);
        Assert.Equal("5", exception.SourceValue);
        Assert.Equal(typeof(ReadWriteAccess), exception.DestinationEnumType);
        Assert.Equal(nameof(ReadWriteAccessDestination.Access), exception.DestinationMember);
    }

    [Fact]
    public void ExplicitCollectionTypeMapTakesPrecedenceOverElementWiseCollectionMapping()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<CollectionItemSource, CollectionItemDestination>();
            c.CreateMap<List<CollectionItemSource>, List<CollectionItemDestination>>()
                .ConvertUsing(_ => [new CollectionItemDestination { Value = 99 }]);
        }).CreateMapper();

        var result = mapper.Map<List<CollectionItemSource>, List<CollectionItemDestination>>(
            [new CollectionItemSource { Value = 1 }]);

        Assert.Collection(result, item => Assert.Equal(99, item.Value));
    }

    [Fact]
    public void AllowUndefinedEnumValuesRestoresLegacyNumericStringCoercion()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.AllowUndefinedEnumValues = true;
            c.CreateMap<EnumTextSource, EnumDestination>();
        }).CreateMapper();

        var result = mapper.Map<EnumTextSource, EnumDestination>(new() { Role = "999" });

        Assert.Equal((Role)999, result.Role);
        Assert.False(Enum.IsDefined(result.Role));
    }

    private static IMapper CreateEnumMapper()
        => new MapperConfiguration(c => c.CreateMap<EnumTextSource, EnumDestination>()).CreateMapper();

    private static void AssertEnumException(MeridianEnumMappingException exception, string destinationMember)
    {
        Assert.Equal("999", exception.SourceValue);
        Assert.Equal(typeof(Role), exception.DestinationEnumType);
        Assert.Equal(destinationMember, exception.DestinationMember);
        Assert.Contains("999", exception.Message);
        Assert.Contains(nameof(Role), exception.Message);
        Assert.Contains(destinationMember, exception.Message);
    }

    private static void AssertFastPath(MapperConfiguration configuration, Type sourceType, Type destinationType)
    {
        var typeMap = Assert.IsType<TypeMap>(configuration.FindTypeMap(sourceType, destinationType));
        Assert.Null(FastPathCompiler.DescribeRejection(typeMap, configuration.AllowNullDestinationValues, hasValueTransformers: false));
        Assert.NotNull(typeMap.CompiledFastPath);
    }

    private static void AssertInterpreterPath(MapperConfiguration configuration, Type sourceType, Type destinationType)
    {
        var typeMap = Assert.IsType<TypeMap>(configuration.FindTypeMap(sourceType, destinationType));
        Assert.Equal("PathDisqualifier: collection-typed destination", FastPathCompiler.DescribeRejection(typeMap, configuration.AllowNullDestinationValues, hasValueTransformers: false));
        Assert.Null(typeMap.CompiledFastPath);
    }

    private sealed class EnumTextSource { public string Role { get; set; } = string.Empty; }
    private sealed class EnumDestination { public Role Role { get; set; } }
    private sealed class InterpreterEnumTextSource
    {
        public string Role { get; set; } = string.Empty;
        public List<int> PathDisqualifier { get; set; } = [];
    }
    private sealed class InterpreterEnumDestination
    {
        public Role Role { get; set; }
        public List<int> PathDisqualifier { get; set; } = [];
    }
    private sealed class FlagsTextSource { public string Permissions { get; set; } = string.Empty; }
    private sealed class FlagsDestination { public Permissions Permissions { get; set; } }
    private sealed class ReadWriteAccessTextSource { public string Access { get; set; } = string.Empty; }
    private sealed class ReadWriteAccessDestination { public ReadWriteAccess Access { get; set; } }
    private sealed class CollectionItemSource { public int Value { get; set; } }
    private sealed class CollectionItemDestination { public int Value { get; set; } }

    private sealed class FastNestedSource { public int Value { get; set; } }
    private sealed class FastNestedDestination { public int Value { get; set; } }
    private sealed class InterpreterNestedSource { public int Value { get; set; } }
    private sealed class InterpreterNestedDestination { public int Value { get; set; } }

    private sealed class FastConversionSource
    {
        public string RoleName { get; set; } = string.Empty;
        public int RoleNumber { get; set; }
        public Role Role { get; set; }
        public int? NullableNumber { get; set; }
        public int Number { get; set; }
        public long NarrowingNumber { get; set; }
        public string NumberText { get; set; } = string.Empty;
        public FastNestedSource Nested { get; set; } = new();
    }

    private sealed class FastConversionDestination
    {
        public Role RoleFromString { get; set; }
        public Role RoleFromNumber { get; set; }
        public string NameFromRole { get; set; } = string.Empty;
        public int NumberFromNullable { get; set; }
        public int? NullableFromNumber { get; set; }
        public int? NullableFromNullable { get; set; }
        public long WidenedNumber { get; set; }
        public byte NarrowedNumber { get; set; }
        public int NumberFromString { get; set; }
        public FastNestedDestination Nested { get; set; } = new();
    }

    private sealed class InterpreterConversionSource
    {
        public string RoleName { get; set; } = string.Empty;
        public int RoleNumber { get; set; }
        public Role Role { get; set; }
        public int? NullableNumber { get; set; }
        public int Number { get; set; }
        public long NarrowingNumber { get; set; }
        public string NumberText { get; set; } = string.Empty;
        public InterpreterNestedSource Nested { get; set; } = new();
        public List<int> PathDisqualifier { get; set; } = [];
    }

    private sealed class InterpreterConversionDestination
    {
        public Role RoleFromString { get; set; }
        public Role RoleFromNumber { get; set; }
        public string NameFromRole { get; set; } = string.Empty;
        public int NumberFromNullable { get; set; }
        public int? NullableFromNumber { get; set; }
        public int? NullableFromNullable { get; set; }
        public long WidenedNumber { get; set; }
        public byte NarrowedNumber { get; set; }
        public int NumberFromString { get; set; }
        public InterpreterNestedDestination Nested { get; set; } = new();
        public List<int> PathDisqualifier { get; set; } = [];
    }
}
