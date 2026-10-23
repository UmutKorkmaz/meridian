using System.Collections.ObjectModel;
using Meridian.Mapping.Execution;

namespace Meridian.Mapping.Tests;

// Boundary tests for MappingEngine paths that eng/coverage_gate.py measured below the 90% line / 80% branch targets:
// null-source defaults, empty-collection shapes, constructor-built collections, enum literal validation for every
// underlying type, destination-path onto existing objects, setter failures and derived-type selection distance.
public class MappingEngineBoundaryTests
{
    private static readonly int[] WithDefault = { 1, 0, 3 };
    private static readonly int[] ThreeOneThree = { 3, 1, 3 };
    private static readonly int[] OneThree = { 1, 3 };
    private static readonly string[] XY = { "x", "y" };

    private static IMapper CreateMapper(Action<IMapperConfigurationExpression> configure) =>
        new MapperConfiguration(configure).CreateMapper();

    #region Null sources and default values

    public sealed class Plain
    {
        public int Id { get; set; }
    }

    public sealed class PlainDto
    {
        public int Id { get; set; } = 7;
    }

    // IMapper rejects a null top-level source, so the engine's own null contract is exercised directly.
    private static object? MapNull(MapperConfiguration config, Type destination)
    {
        var mapper = config.CreateMapper();
        return new MappingEngine(config).Map(null, typeof(Plain), destination, new ResolutionContext(mapper));
    }

    [Fact]
    public void Null_Source_Maps_To_Defaults_By_Destination_Kind()
    {
        var config = new MapperConfiguration(cfg => cfg.CreateMap<Plain, PlainDto>());

        Assert.Null(MapNull(config, typeof(PlainDto)));
        Assert.Null(MapNull(config, typeof(string)));
        Assert.Equal(0, MapNull(config, typeof(int)));
        Assert.Equal(default(Guid), MapNull(config, typeof(Guid)));
    }

    [Fact]
    public void Null_Source_To_A_Collection_Destination_Is_Empty_By_Default_And_Null_When_Allowed()
    {
        var strict = new MapperConfiguration(cfg => cfg.CreateMap<Plain, PlainDto>());
        Assert.Empty((List<int>)MapNull(strict, typeof(List<int>))!);
        Assert.Empty((int[])MapNull(strict, typeof(int[]))!);
        Assert.Empty((HashSet<int>)MapNull(strict, typeof(HashSet<int>))!);
        Assert.Empty((ISet<int>)MapNull(strict, typeof(ISet<int>))!);
        Assert.Empty((Dictionary<string, int>)MapNull(strict, typeof(Dictionary<string, int>))!);
        Assert.Empty((IReadOnlyDictionary<string, int>)MapNull(strict, typeof(IReadOnlyDictionary<string, int>))!);
        Assert.Empty((ObservableCollection<int>)MapNull(strict, typeof(ObservableCollection<int>))!);
        Assert.Empty((Queue<int>)MapNull(strict, typeof(Queue<int>))!);

        var lenient = new MapperConfiguration(cfg =>
        {
            cfg.AllowNullCollections = true;
            cfg.CreateMap<Plain, PlainDto>();
        });
        Assert.Null(MapNull(lenient, typeof(List<int>)));
        Assert.Null(MapNull(lenient, typeof(HashSet<int>)));
    }

    public sealed class NullableItems
    {
        public List<int?>? Values { get; set; }
        public List<string?>? Names { get; set; }
        public List<int>? Missing { get; set; }
    }

    public sealed class CollectionShapes
    {
        public List<int> Values { get; set; } = new();
        public List<string?> Names { get; set; } = new();
        public HashSet<int> Missing { get; set; } = new() { 9 };
    }

    [Fact]
    public void Null_Elements_Become_Defaults_Of_The_Destination_Element_Type()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<NullableItems, CollectionShapes>());
        var result = mapper.Map<CollectionShapes>(new NullableItems { Values = new() { 1, null, 3 }, Names = new() { "a", null } });

        Assert.Equal(WithDefault, result.Values);
        Assert.Equal(new string?[] { "a", null }, result.Names);
        Assert.Empty(result.Missing);
    }

    public sealed class QueueDestination
    {
        public Queue<int> Values { get; set; } = new();
        public ObservableCollection<int> Observed { get; set; } = new();
        public ISet<int> Unique { get; set; } = new HashSet<int>();
        public int[] Array { get; set; } = System.Array.Empty<int>();
    }

    public sealed class ListSource
    {
        public List<int> Values { get; set; } = new();
        public List<int> Observed { get; set; } = new();
        public List<int> Unique { get; set; } = new();
        public List<int> Array { get; set; } = new();
    }

    [Fact]
    public void Collections_Without_A_List_Shape_Are_Built_Through_Their_Enumerable_Constructor_Or_Set_Type()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<ListSource, QueueDestination>());
        var data = new List<int> { 3, 1, 3 };
        var result = mapper.Map<QueueDestination>(new ListSource { Values = data, Observed = data, Unique = data, Array = data });

        Assert.Equal(ThreeOneThree, result.Values.ToArray());
        Assert.Equal(ThreeOneThree, result.Observed.ToArray());
        Assert.Equal(OneThree, result.Unique.OrderBy(x => x).ToArray());
        Assert.Equal(ThreeOneThree, result.Array);
    }

    [Fact]
    public void Null_Source_Collections_Use_The_Matching_Empty_Collection_Shape()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<ListSourceNullable, QueueDestination>());
        var result = mapper.Map<QueueDestination>(new ListSourceNullable());

        Assert.Empty(result.Values);
        Assert.Empty(result.Observed);
        Assert.Empty(result.Unique);
        Assert.Empty(result.Array);
    }

    public sealed class ListSourceNullable
    {
        public List<int>? Values { get; set; }
        public List<int>? Observed { get; set; }
        public List<int>? Unique { get; set; }
        public List<int>? Array { get; set; }
    }

    #endregion

    #region Mapping onto an existing destination

    public sealed class AddressSource
    {
        public string? Street { get; set; }
    }

    public sealed class AddressTarget
    {
        public string? Street { get; set; }
    }

    public sealed class PersonSource
    {
        public AddressSource? Address { get; set; }
        public List<string?> Tags { get; set; } = new();
        public string[] Codes { get; set; } = System.Array.Empty<string>();
    }

    public sealed class PersonTarget
    {
        public AddressTarget? Address { get; set; }
        public List<string?> Tags { get; set; } = new() { "stale" };
        public string[] Codes { get; set; } = new[] { "old" };
    }

    [Fact]
    public void Mapping_Onto_An_Existing_Destination_Reuses_Nested_Objects_And_Replaces_Collection_Contents()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<AddressSource, AddressTarget>();
            cfg.CreateMap<PersonSource, PersonTarget>()
                .ForMember(d => d.Address, opt => { opt.MapFrom(s => s.Address); opt.UseDestinationValue(); })
                .ForMember(d => d.Tags, opt => { opt.MapFrom(s => s.Tags); opt.UseDestinationValue(); })
                .ForMember(d => d.Codes, opt => { opt.MapFrom(s => s.Codes); opt.UseDestinationValue(); });
        });
        var existingAddress = new AddressTarget { Street = "old street" };
        var existingTags = new List<string?> { "stale" };
        var destination = new PersonTarget { Address = existingAddress, Tags = existingTags };

        var result = mapper.Map(
            new PersonSource { Address = new AddressSource { Street = "new street" }, Tags = new() { "a", null, "b" }, Codes = XY },
            destination);

        Assert.Same(destination, result);
        Assert.Same(existingAddress, result.Address);
        Assert.Equal("new street", result.Address!.Street);
        Assert.Same(existingTags, result.Tags);
        Assert.Equal(new string?[] { "a", null, "b" }, result.Tags);
        Assert.Equal(XY, result.Codes);
    }

    public sealed class FlatSource
    {
        public string? Street { get; set; }
    }

    public sealed class NestedTarget
    {
        public AddressTarget? Address { get; set; }
    }

    [Fact]
    public void Destination_Paths_Fill_Existing_Intermediate_Objects_And_Create_Missing_Ones()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<FlatSource, NestedTarget>()
            .ForPath(d => d.Address!.Street, opt => opt.MapFrom(s => s.Street)));
        var existing = new AddressTarget { Street = "keep identity" };
        var withAddress = new NestedTarget { Address = existing };

        mapper.Map(new FlatSource { Street = "main" }, withAddress);
        Assert.Same(existing, withAddress.Address);
        Assert.Equal("main", existing.Street);

        var withoutAddress = mapper.Map(new FlatSource { Street = "side" }, new NestedTarget());
        Assert.Equal("side", withoutAddress.Address!.Street);
    }

    #endregion

    #region Failure reporting and conversions

    public sealed class ThrowingTarget
    {
        private readonly int _id;

        public ThrowingTarget()
        {
            _id = 0;
        }

        public int Id
        {
            get => _id;
            set => throw new InvalidOperationException("setter refused");
        }
    }

    [Fact]
    public void A_Throwing_Destination_Setter_Is_Reported_With_The_Member_And_Type_And_Keeps_The_Cause()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Plain, ThrowingTarget>()
            .ForMember(d => d.Id, opt => { opt.MapFrom(s => s.Id); opt.Condition(src => true); }));
        var failure = Assert.Throws<InvalidOperationException>(() => mapper.Map<ThrowingTarget>(new Plain { Id = 1 }));
        Assert.Contains("'Id'", failure.Message);
        Assert.Contains(nameof(ThrowingTarget), failure.Message);
        Assert.Equal("setter refused", failure.InnerException!.Message);
    }

    public sealed class StringSource
    {
        public string? Number { get; set; }
        public Guid Identity { get; set; }
        public ConsoleColor Color { get; set; }
        public int Raw { get; set; }
    }

    public sealed class ConvertedTarget
    {
        public int? Number { get; set; }
        public string? Identity { get; set; }
        public int Color { get; set; }
        public ConsoleColor Raw { get; set; }
    }

    [Fact]
    public void Scalar_Conversions_Cover_Nullable_Targets_Enums_And_Non_Convertible_Strings()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<StringSource, ConvertedTarget>());
        var identity = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var result = mapper.Map<ConvertedTarget>(new StringSource { Number = "5", Identity = identity, Color = ConsoleColor.Red, Raw = (int)ConsoleColor.Blue });

        Assert.Equal(5, result.Number);
        Assert.Equal(identity.ToString(), result.Identity);
        Assert.Equal((int)ConsoleColor.Red, result.Color);
        Assert.Equal(ConsoleColor.Blue, result.Raw);
    }

    #endregion

    #region Enum literals for every underlying type

    [Flags] public enum FlagsSByte : sbyte { None = 0, A = 1, B = 2 }
    [Flags] public enum FlagsByte : byte { None = 0, A = 1, B = 2 }
    [Flags] public enum FlagsShort : short { None = 0, A = 1, B = 2 }
    [Flags] public enum FlagsUShort : ushort { None = 0, A = 1, B = 2 }
    [Flags] public enum FlagsInt : int { None = 0, A = 1, B = 2 }
    [Flags] public enum FlagsUInt : uint { None = 0, A = 1, B = 2 }
    [Flags] public enum FlagsLong : long { None = 0, A = 1, B = 2 }
    [Flags] public enum FlagsULong : ulong { None = 0, A = 1, B = 2 }

    public sealed class LiteralSource
    {
        public string Value { get; set; } = "";
    }

    public sealed class EnumHolder<TEnum>
        where TEnum : struct, Enum
    {
        public TEnum Value { get; set; }
    }

    private static void AssertFlagsLiterals<TEnum>(string undefinedLiteral)
        where TEnum : struct, Enum
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<LiteralSource, EnumHolder<TEnum>>());
        Assert.Equal(Enum.Parse<TEnum>("A, B"), mapper.Map<EnumHolder<TEnum>>(new LiteralSource { Value = "3" }).Value);
        Assert.Equal(Enum.Parse<TEnum>("A"), mapper.Map<EnumHolder<TEnum>>(new LiteralSource { Value = "A" }).Value);
        Assert.Equal(Enum.Parse<TEnum>("A, B"), mapper.Map<EnumHolder<TEnum>>(new LiteralSource { Value = "a, b" }).Value);
        var failure = Assert.Throws<MeridianEnumMappingException>(() => mapper.Map<EnumHolder<TEnum>>(new LiteralSource { Value = undefinedLiteral }));
        Assert.Contains(typeof(TEnum).Name, failure.Message);
        Assert.Throws<MeridianEnumMappingException>(() => mapper.Map<EnumHolder<TEnum>>(new LiteralSource { Value = "NotAMember" }));
    }

    [Fact]
    public void Flags_Numeric_Literals_Are_Checked_Against_The_Defined_Bits_For_Every_Underlying_Type()
    {
        AssertFlagsLiterals<FlagsSByte>("4");
        AssertFlagsLiterals<FlagsByte>("4");
        AssertFlagsLiterals<FlagsShort>("4");
        AssertFlagsLiterals<FlagsUShort>("4");
        AssertFlagsLiterals<FlagsInt>("4");
        AssertFlagsLiterals<FlagsUInt>("4");
        AssertFlagsLiterals<FlagsLong>("4");
        AssertFlagsLiterals<FlagsULong>("4");
    }

    [Fact]
    public void Signed_Flags_Literals_And_Overflowing_Literals_Are_Rejected_Without_Leaking_Raw_Exceptions()
    {
        var signed = CreateMapper(cfg => cfg.CreateMap<LiteralSource, EnumHolder<FlagsSByte>>());
        Assert.Throws<MeridianEnumMappingException>(() => signed.Map<EnumHolder<FlagsSByte>>(new LiteralSource { Value = "-1" }));
        Assert.Throws<MeridianEnumMappingException>(() => signed.Map<EnumHolder<FlagsSByte>>(new LiteralSource { Value = "300" }));
        Assert.Equal(FlagsSByte.A, signed.Map<EnumHolder<FlagsSByte>>(new LiteralSource { Value = "+1" }).Value);
        Assert.Throws<MeridianEnumMappingException>(() => signed.Map<EnumHolder<FlagsSByte>>(new LiteralSource { Value = "" }));
        Assert.Throws<MeridianEnumMappingException>(() => signed.Map<EnumHolder<FlagsSByte>>(new LiteralSource { Value = "-" }));
        Assert.Throws<MeridianEnumMappingException>(() => signed.Map<EnumHolder<FlagsSByte>>(new LiteralSource { Value = "A,,B" }));

        var unsigned = CreateMapper(cfg => cfg.CreateMap<LiteralSource, EnumHolder<FlagsULong>>());
        Assert.Throws<MeridianEnumMappingException>(() => unsigned.Map<EnumHolder<FlagsULong>>(new LiteralSource { Value = "18446744073709551615" }));
        Assert.Throws<MeridianEnumMappingException>(() => unsigned.Map<EnumHolder<FlagsULong>>(new LiteralSource { Value = "99999999999999999999999" }));
    }

    public enum Plainer { A = 1, B = 2 }

    public sealed class PlainerHolder
    {
        public Plainer Value { get; set; }
    }

    [Fact]
    public void Non_Flags_Numeric_Literals_Must_Name_A_Defined_Member_Unless_Undefined_Values_Are_Allowed()
    {
        var strict = CreateMapper(cfg => cfg.CreateMap<LiteralSource, PlainerHolder>());
        Assert.Equal(Plainer.B, strict.Map<PlainerHolder>(new LiteralSource { Value = "2" }).Value);
        Assert.Equal(Plainer.A, strict.Map<PlainerHolder>(new LiteralSource { Value = " 1 " }).Value);
        Assert.Throws<MeridianEnumMappingException>(() => strict.Map<PlainerHolder>(new LiteralSource { Value = "3" }));

        var lenient = CreateMapper(cfg =>
        {
            cfg.AllowUndefinedEnumValues = true;
            cfg.CreateMap<LiteralSource, PlainerHolder>();
        });
        Assert.Equal((Plainer)3, lenient.Map<PlainerHolder>(new LiteralSource { Value = "3" }).Value);
    }

    #endregion

    #region Derived-type selection

    public class Animal
    {
        public string Name { get; set; } = "";
    }

    public class Dog : Animal
    {
        public int Tricks { get; set; }
    }

    public sealed class Puppy : Dog
    {
        public bool Teething { get; set; }
    }

    public class AnimalDto
    {
        public string Name { get; set; } = "";
    }

    public sealed class DogDto : AnimalDto
    {
        public int Tricks { get; set; }
    }

    public class Hound : Dog
    {
        public bool Trained { get; set; }
    }

    public sealed class HoundPup : Hound;

    public sealed class HoundDto : AnimalDto
    {
        public int Tricks { get; set; }
        public bool Trained { get; set; }
    }

    [Fact]
    public void The_Nearest_Mapped_Ancestor_Wins_For_A_Runtime_Type_Below_An_Included_Derived_Map()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Animal, AnimalDto>().Include<Dog, DogDto>();
            cfg.CreateMap<Dog, DogDto>();
        });

        Animal puppy = new Puppy { Name = "Rex", Tricks = 4, Teething = true };
        var mapped = mapper.Map<Animal, AnimalDto>(puppy);
        var dog = Assert.IsType<DogDto>(mapped);
        Assert.Equal("Rex", dog.Name);
        Assert.Equal(4, dog.Tricks);

        Assert.IsType<AnimalDto>(mapper.Map<AnimalDto>(new Animal { Name = "Generic" }));

        var competing = CreateMapper(cfg =>
        {
            // The farther candidate deliberately appears first, so first-assignable selection cannot pass.
            cfg.CreateMap<Animal, AnimalDto>().Include<Dog, DogDto>().Include<Hound, HoundDto>();
            cfg.CreateMap<Dog, DogDto>();
            cfg.CreateMap<Hound, HoundDto>();
        });
        Animal houndPup = new HoundPup { Name = "Scout", Tricks = 7, Trained = true };
        var reverseRegistration = CreateMapper(cfg =>
        {
            cfg.CreateMap<Animal, AnimalDto>().Include<Dog, DogDto>().Include<Hound, HoundDto>();
            cfg.CreateMap<Hound, HoundDto>();
            cfg.CreateMap<Dog, DogDto>();
        });
        foreach (var candidate in new[] { competing, reverseRegistration })
        {
            var nearest = Assert.IsType<HoundDto>(candidate.Map<Animal, AnimalDto>(houndPup));
            Assert.Equal("Scout", nearest.Name);
            Assert.Equal(7, nearest.Tricks);
            Assert.True(nearest.Trained);
        }
    }

    #endregion
}
