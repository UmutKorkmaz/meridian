using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using Meridian.Mapping.Configuration;
using Meridian.Mapping.Converters;
using Meridian.Mapping.Execution;
using Meridian.Mapping.Queryable;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace Meridian.Mapping.EntityFramework.Tests;

[Trait("Provider", "SQLite")]
public sealed class SqliteProjectionSqlTests(ITestOutputHelper output)
    : ProjectionSqlTests(new SqliteFixture(), output);

[Trait("Provider", "PostgreSQL")]
public sealed class PostgreSqlProjectionSqlTests(ITestOutputHelper output)
    : ProjectionSqlTests(new PostgreSqlFixture(), output);

public abstract class ProjectionSqlTests(ProjectionFixture fixture, ITestOutputHelper output) : IDisposable
{
    private IQueryable<Person> People => fixture.Context.People.AsNoTracking().OrderBy(p => p.Id);

    private List<T> Execute<T>(IQueryable<T> query, string name, params string[] columns)
    {
        Assert.Empty(fixture.Commands.Sql);
        var sql = query.ToQueryString();
        Assert.Empty(fixture.Commands.Sql); // SQL inspection and projection construction must stay deferred.
        Assert.DoesNotContain("SecretPayload", sql);
        foreach (var column in columns) Assert.Contains('"' + column + '"', sql);
        var rows = query.ToList();
        var executed = Assert.Single(fixture.Commands.Sql);
        Assert.DoesNotContain("SecretPayload", executed);
        Assert.Empty(fixture.Context.ChangeTracker.Entries());
        output.WriteLine($"case={name}; runtime={Environment.Version}; provider={fixture.Context.Database.ProviderName}; server={fixture.Context.Database.GetDbConnection().ServerVersion}; rows={rows.Count}; commands=1\nToQueryString:\n{sql}\nExecuted:\n{executed}");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a =>
                     a.GetName().Name is "Microsoft.EntityFrameworkCore" or "Microsoft.EntityFrameworkCore.Sqlite" or
                         "Npgsql.EntityFrameworkCore.PostgreSQL" or "Microsoft.Data.Sqlite" or "Npgsql" or "Meridian.Mapping"))
        {
            output.WriteLine($"binary={assembly.GetName().Name}; version={assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}; sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))}");
        }
        return rows;
    }

    [Fact]
    public void Projection_Selects_Only_Required_Columns()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, Summary>());
        var query = People.ProjectTo<Summary>(config);
        var sql = query.ToQueryString();
        Assert.DoesNotContain("Age", sql);
        Assert.DoesNotContain("AddressId", sql);
        Assert.DoesNotContain("JOIN", sql);
        var rows = Execute(query, nameof(Projection_Selects_Only_Required_Columns), "Id", "Name");
        Assert.Equal(new[] { (1, "Ada"), (2, "Bob") }, rows.Select(r => (r.Id, r.Name)));
    }

    [Fact]
    public void Flattened_Navigation_Translates()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, Flat>());
        var query = People.ProjectTo<Flat>(config);
        Assert.Contains("LEFT JOIN", query.ToQueryString());
        var rows = Execute(query, nameof(Flattened_Navigation_Translates), "City");
        Assert.Equal(new string?[] { "Ankara", null }, rows.Select(r => r.AddressCity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expanded_Member_Is_Explicit(bool expand)
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, Expanded>()
            .ForMember(d => d.Age, o => o.ExplicitExpansion()));
        var query = expand ? People.ProjectTo<Expanded>(config, d => d.Age) : People.ProjectTo<Expanded>(config);
        if (!expand) Assert.DoesNotContain("Age", query.ToQueryString());
        var rows = Execute(query, $"{nameof(Expanded_Member_Is_Explicit)}({expand})", expand ? ["Id", "Age"] : ["Id"]);
        Assert.Equal(expand ? new[] { 37, 19 } : new[] { 0, 0 }, rows.Select(r => r.Age));
    }

    [Fact]
    public void Expanded_Expression_Preserves_Configured_Source()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, Expanded>()
            .ForMember(d => d.Age, o => { o.ExplicitExpansion(); o.MapFrom(s => s.Age + 1); }));
        var rows = Execute(People.ProjectTo<Expanded>(config, d => d.Age), nameof(Expanded_Expression_Preserves_Configured_Source), "Age");
        Assert.Equal(new[] { 38, 20 }, rows.Select(r => r.Age));
    }

    [Fact]
    public void Parameterless_Constructor_Remains_Preferred()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, MutableCtor>());
        Assert.All(Execute(People.ProjectTo<MutableCtor>(config), nameof(Parameterless_Constructor_Remains_Preferred)), r => Assert.Equal("default", r.Path));
    }

    [Fact]
    public void Constructor_Default_Does_Not_Discard_Explicit_Writable_Member()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, WritableCtor>()
            .ForMember(d => d.Rank, o => o.MapFrom(s => s.Age)));
        var rows = Execute(People.ProjectTo<WritableCtor>(config), nameof(Constructor_Default_Does_Not_Discard_Explicit_Writable_Member), "Age");
        Assert.Equal(new[] { 37, 19 }, rows.Select(r => r.Rank));
    }

    [Fact]
    public void Supported_Predicate_And_MapFrom_Are_Server_Translated()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, Summary>()
            .ForMember(d => d.Name, o => o.MapFrom(s => s.Name + "!")));
        var query = People.ProjectTo<Summary>(config).Where(d => d.Id > 1 && d.Name.StartsWith("B"));
        Assert.Contains("WHERE", query.ToQueryString());
        Assert.Contains("||", query.ToQueryString());
        var row = Assert.Single(Execute(query, nameof(Supported_Predicate_And_MapFrom_Are_Server_Translated), "Id", "Name"));
        Assert.Equal((2, "Bob!"), (row.Id, row.Name));
    }

    [Fact]
    public void Null_Substitution_Is_Server_Translated()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, Flat>()
            .ForMember(d => d.AddressCity, o => { o.MapFrom(s => s.Address!.City); o.NullSubstitute("unknown"); }));
        var query = People.ProjectTo<Flat>(config);
        Assert.Contains("COALESCE", query.ToQueryString());
        Assert.Equal(new[] { "Ankara", "unknown" }, Execute(query, nameof(Null_Substitution_Is_Server_Translated)).Select(r => r.AddressCity));
    }

    [Fact]
    public void Constructor_Dto_Projection_Translates()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, Immutable>());
        var rows = Execute(People.ProjectTo<Immutable>(config), nameof(Constructor_Dto_Projection_Translates), "Id", "Name");
        Assert.Equal(new[] { (1, "Ada"), (2, "Bob") }, rows.Select(r => (r.Id, r.Name)));
    }

    [Fact]
    public void Constructor_Explicit_Expression_And_Optional_Defaults_Translate()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, ConfiguredCtor>()
            .ForCtorParam("label", o => o.MapFrom(s => s.Name + "!")));
        var rows = Execute(People.ProjectTo<ConfiguredCtor>(config), nameof(Constructor_Explicit_Expression_And_Optional_Defaults_Translate), "Name");
        Assert.Equal(new[] { "Ada!", "Bob!" }, rows.Select(r => r.Label));
        Assert.All(rows, r => { Assert.Equal(7, r.Rank); Assert.Null(r.Note); Assert.Null(r.Score); });
    }

    [Fact]
    public void Ambiguous_Constructor_Fails_Before_Execution()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, AmbiguousCtor>());
        var error = Assert.ThrowsAny<Exception>(() => People.ProjectTo<AmbiguousCtor>(config)).GetBaseException();
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Commands.Sql);
    }

    [Fact]
    public void Record_Constructor_Expression_Is_Not_Overwritten_By_Convention()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, RecordDto>()
            .ForCtorParam("Name", o => o.MapFrom(s => s.Name + "!")));
        Assert.Equal(new[] { "Ada!", "Bob!" }, Execute(People.ProjectTo<RecordDto>(config), nameof(Record_Constructor_Expression_Is_Not_Overwritten_By_Convention)).Select(r => r.Name));
    }

    [Fact]
    public void Explicit_Writable_Member_Overrides_Constructor_Expression()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, RecordDto>()
            .ForCtorParam("Name", o => o.MapFrom(s => s.Name + " ctor"))
            .ForMember(d => d.Name, o => o.MapFrom(s => s.Name + " member")));
        Assert.Equal(new[] { "Ada member", "Bob member" }, Execute(People.ProjectTo<RecordDto>(config), nameof(Explicit_Writable_Member_Overrides_Constructor_Expression)).Select(r => r.Name));
    }

    [Fact]
    public void Expanded_Runtime_Resolver_Fails_Before_Execution()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, Summary>()
            .ForMember(d => d.Name, o => { o.ExplicitExpansion(); o.MapFrom<NameResolver>(); }));
        var error = Assert.ThrowsAny<Exception>(() => People.ProjectTo<Summary>(config, d => d.Name)).GetBaseException();
        Assert.Contains("value resolver", error.Message);
        Assert.Empty(fixture.Commands.Sql);
    }

    [Fact]
    public void Constructor_Nullable_Source_And_Nonnull_Optional_Default_Translate()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, NullableCtor>());
        var rows = Execute(People.ProjectTo<NullableCtor>(config), nameof(Constructor_Nullable_Source_And_Nonnull_Optional_Default_Translate), "Age");
        Assert.Equal(new int?[] { 37, 19 }, rows.Select(r => r.Age));
        Assert.All(rows, r => Assert.Equal(5, r.Score));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Required_Constructor_Omitted_Member_Fails_Before_Execution(bool ignored)
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, RecordDto>()
            .ForCtorParam("Name", o => o.MapFrom(s => s.Name + "!"))
            .ForMember(d => d.Name, o => { if (ignored) o.Ignore(); else o.ExplicitExpansion(); }));
        var error = Assert.ThrowsAny<Exception>(() => People.ProjectTo<RecordDto>(config)).GetBaseException();
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("omitted", error.Message);
        Assert.Empty(fixture.Commands.Sql);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Optional_Constructor_Omitted_Member_Uses_Default_Without_Column(bool ignored)
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, OptionalRecord>()
            .ForCtorParam("Age", o => o.MapFrom(s => s.Age + 1))
            .ForMember(d => d.Age, o => { if (ignored) o.Ignore(); else o.ExplicitExpansion(); }));
        var query = People.ProjectTo<OptionalRecord>(config);
        Assert.DoesNotContain("Age", query.ToQueryString());
        Assert.All(Execute(query, nameof(Optional_Constructor_Omitted_Member_Uses_Default_Without_Column)), r => Assert.Equal(7, r.Age));
    }

    [Fact]
    public void Requested_Constructor_Expansion_Preserves_Explicit_Argument()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, RecordDto>()
            .ForCtorParam("Name", o => o.MapFrom(s => s.Name + "!"))
            .ForMember(d => d.Name, o => o.ExplicitExpansion()));
        Assert.Equal(new[] { "Ada!", "Bob!" }, Execute(People.ProjectTo<RecordDto>(config, d => d.Name), nameof(Requested_Constructor_Expansion_Preserves_Explicit_Argument), "Name").Select(r => r.Name));
    }

    [Fact]
    public void Runtime_Type_Constructor_Projection_Translates()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, Immutable>());
        var expression = (System.Linq.Expressions.Expression<Func<Person, Immutable>>)
            ExpressionBuilder.BuildProjection(config, typeof(Person), typeof(Immutable));
        Assert.Equal(2, Execute(People.Select(expression), nameof(Runtime_Type_Constructor_Projection_Translates), "Name").Count);
    }

    [Fact]
    public void Nested_Constructor_Projection_Preserves_Null_Navigation()
    {
        var config = new MapperConfiguration(c => { c.CreateMap<Person, WithAddress>(); c.CreateMap<Address, ImmutableAddress>(); });
        var rows = Execute(People.ProjectTo<WithAddress>(config), nameof(Nested_Constructor_Projection_Preserves_Null_Navigation), "City");
        Assert.Equal("Ankara", rows[0].Address!.City);
        Assert.Null(rows[1].Address);
    }

    [Fact]
    public void Collection_Constructor_Projection_Uses_One_Query()
    {
        var config = new MapperConfiguration(c => { c.CreateMap<Person, WithItems>(); c.CreateMap<Item, ImmutableItem>(); });
        var rows = Execute(People.ProjectTo<WithItems>(config), nameof(Collection_Constructor_Projection_Uses_One_Query), "Label");
        Assert.Equal("book", Assert.Single(rows[0].Items).Label);
        Assert.Empty(rows[1].Items);
    }

    [Fact]
    public void Missing_Required_Constructor_Argument_Fails_Before_Execution()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, MissingCtor>());
        var error = Assert.ThrowsAny<Exception>(() => People.ProjectTo<MissingCtor>(config)).GetBaseException();
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("ForCtorParam", error.Message);
        Assert.Empty(fixture.Commands.Sql);
    }

    [Theory]
    [InlineData("resolver", "value resolver")]
    [InlineData("condition", "runtime conditions")]
    [InlineData("factory", "ConstructUsing")]
    [InlineData("converter", "type converter")]
    public void Runtime_Only_Configuration_Fails_Before_Execution(string kind, string expected)
    {
        var calls = 0;
        var config = new MapperConfiguration(c =>
        {
            var map = c.CreateMap<Person, Summary>();
            if (kind == "resolver") map.ForMember(d => d.Name, o => o.MapFrom<NameResolver>());
            if (kind == "condition") map.ForMember(d => d.Name, o => o.Condition(s => s.Age > 20));
            if (kind == "factory") map.ConstructUsing(s => { calls++; return new Summary(); });
            if (kind == "converter") map.ConvertUsing(s => { calls++; return new Summary(); });
        });
        var error = Assert.ThrowsAny<Exception>(() => People.ProjectTo<Summary>(config)).GetBaseException();
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains(expected, error.Message);
        Assert.Equal(0, calls);
        Assert.Empty(fixture.Commands.Sql);
        output.WriteLine($"Rejected before execution: {error.Message}; commands=0");
    }

    private static ProjectionTestProvider GetterConfig<T>(bool reflection, Action<IMappingExpression<Person, T>> configure)
    {
        IMappingExpression<Person, T>? expression = null;
        var config = new MapperConfiguration(c => { expression = c.CreateMap<Person, T>(); configure(expression); });
        var map = config.FindTypeMap(typeof(Person), typeof(T))!;
        if (reflection)
            map = (TypeMap)typeof(MapperConfiguration).GetMethod("CompileTypeMapReflection", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [typeof(Person), typeof(T), expression, new TypeMap(typeof(Person), typeof(T))])!;
        Assert.Empty(map.PropertyMaps); // Getter-only projection metadata must never become runtime assignments.
        return new ProjectionTestProvider(config, map);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Getter_Required_Omission_Rejects_Explicit_Constructor(bool reflection, bool ignored)
    {
        var config = GetterConfig<RequiredGetter>(reflection, m => m
            .ForCtorParam("name", o => o.MapFrom(p => p.Name + "!"))
            .ForMember(d => d.Name, o => { if (ignored) o.Ignore(); else o.ExplicitExpansion(); }));
        var error = Assert.ThrowsAny<Exception>(() => People.ProjectTo<RequiredGetter>(config)).GetBaseException();
        Assert.Contains("omitted", error.Message);
        Assert.Empty(fixture.Commands.Sql);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Getter_Optional_Omission_Uses_Default_Without_Name_Column(bool reflection, bool ignored)
    {
        var config = GetterConfig<OptionalGetter>(reflection, m => m
            .ForCtorParam("name", o => o.MapFrom(p => p.Name + "!"))
            .ForMember(d => d.Name, o => { if (ignored) o.Ignore(); else o.ExplicitExpansion(); }));
        // This metadata is projection-only: existing constructor-based runtime mapping is unchanged.
        Assert.Equal("Ada!", config.CreateMapper().Map<Person, OptionalGetter>(new Person { Name = "Ada" }).Name);
        var query = People.ProjectTo<OptionalGetter>(config);
        Assert.DoesNotContain("Name", query.ToQueryString());
        Assert.All(Execute(query, nameof(Getter_Optional_Omission_Uses_Default_Without_Name_Column)), d => Assert.Equal("default", d.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_Requested_Expansion_Preserves_Constructor_Expression(bool reflection)
    {
        var config = GetterConfig<RequiredGetter>(reflection, m => m
            .ForCtorParam("name", o => o.MapFrom(p => p.Name + "!"))
            .ForMember(d => d.Name, o => o.ExplicitExpansion()));
        Assert.Equal(new[] { "Ada!", "Bob!" }, Execute(People.ProjectTo<RequiredGetter>(config, d => d.Name), nameof(Getter_Requested_Expansion_Preserves_Constructor_Expression), "Name").Select(d => d.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_Runtime_Condition_Rejects_Before_Execution(bool reflection)
    {
        var calls = 0;
        var config = GetterConfig<RequiredGetter>(reflection, m => m.ForMember(d => d.Name, o => o.Condition(p => { calls++; return true; })));
        var error = Assert.ThrowsAny<Exception>(() => People.ProjectTo<RequiredGetter>(config)).GetBaseException();
        Assert.Contains("runtime conditions", error.Message);
        Assert.Equal(0, calls);
        Assert.Empty(fixture.Commands.Sql);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_Configured_Member_Expression_Is_Used(bool reflection)
    {
        var config = GetterConfig<RequiredGetter>(reflection, m => m.ForMember(d => d.Name, o => o.MapFrom(p => p.Name + " member")));
        Assert.Equal(new[] { "Ada member", "Bob member" }, Execute(People.ProjectTo<RequiredGetter>(config), nameof(Getter_Configured_Member_Expression_Is_Used), "Name").Select(d => d.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_Inherited_Omission_And_Derived_Override(bool overridden)
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<Person, BaseGetter>().ForMember(d => d.Name, o => o.Ignore());
            var derived = c.CreateMap<Person, DerivedGetter>().IncludeBase<Person, BaseGetter>();
            if (overridden) derived.ForMember(d => d.Name, o => o.MapFrom(p => p.Name + " derived"));
        });
        var query = People.ProjectTo<DerivedGetter>(config);
        if (!overridden) Assert.DoesNotContain("Name", query.ToQueryString());
        Assert.Equal(overridden ? new[] { "Ada derived", "Bob derived" } : new[] { "default", "default" },
            Execute(query, nameof(Getter_Inherited_Omission_And_Derived_Override)).Select(d => d.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_Named_Source_Selects_Configured_Column(bool reflection)
    {
        var config = GetterConfig<RequiredGetter>(reflection, m => m.ForMember(d => d.Name, o => o.MapFrom(nameof(Person.Alias))));
        var query = People.ProjectTo<RequiredGetter>(config);
        Assert.DoesNotContain("\"Name\"", query.ToQueryString());
        Assert.Equal(new string?[] { "public-Ada", null }, Execute(query, nameof(Getter_Named_Source_Selects_Configured_Column), "Alias").Select(d => d.Name));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Getter_Constant_Precedes_Expression_And_Substitution(bool reflection, bool nullConstant)
    {
        void Configure(IMemberConfigurationExpression<Person, RequiredGetter> o)
        { o.MapFrom(p => p.Name); o.NullSubstitute("fallback"); o.UseValue(nullConstant ? null! : "fixed"); }
        var config = GetterConfig<RequiredGetter>(reflection, m => m.ForMember(d => d.Name, Configure));
        var query = People.ProjectTo<RequiredGetter>(config);
        Assert.DoesNotContain("\"Name\"", query.ToQueryString());
        Assert.DoesNotContain("COALESCE", query.ToQueryString());
        Assert.All(Execute(query, nameof(Getter_Constant_Precedes_Expression_And_Substitution)), d => Assert.Equal(nullConstant ? null : "fixed", d.Name));
        var mutable = new MapperConfiguration(c => c.CreateMap<Person, Summary>().ForMember(d => d.Name,
            o => { o.MapFrom(p => p.Name); o.NullSubstitute("fallback"); o.UseValue(nullConstant ? null! : "fixed"); }));
        fixture.Commands.Sql.Clear();
        Assert.All(Execute(People.ProjectTo<Summary>(mutable), "Writable_Constant_Precedence_Control"), d => Assert.Equal(nullConstant ? null : "fixed", d.Name));
    }

    [Theory]
    [InlineData(false, "expression")]
    [InlineData(true, "expression")]
    [InlineData(false, "named")]
    [InlineData(true, "named")]
    [InlineData(false, "constructor")]
    [InlineData(true, "constructor")]
    public void Getter_Source_Substitution_Is_Server_Translated(bool reflection, string source)
    {
        var config = GetterConfig<RequiredGetter>(reflection, m =>
        {
            m.ForMember(d => d.Name, o =>
            {
                if (source == "expression") o.MapFrom(p => p.Alias);
                else o.MapFrom(nameof(Person.Alias));
                o.NullSubstitute("fallback");
            });
            if (source == "constructor") m.ForCtorParam("name", o => o.MapFrom(p => p.Alias == null ? null : p.Alias + " ctor"));
        });
        var query = People.ProjectTo<RequiredGetter>(config);
        Assert.Contains("COALESCE", query.ToQueryString());
        Assert.DoesNotContain("\"Name\"", query.ToQueryString());
        Assert.Equal(new[] { source == "constructor" ? "public-Ada ctor" : "public-Ada", "fallback" },
            Execute(query, nameof(Getter_Source_Substitution_Is_Server_Translated), "Alias").Select(d => d.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_Convention_Substitution_Is_Server_Translated(bool reflection)
    {
        var config = GetterConfig<AliasGetter>(reflection, m => m.ForMember(d => d.Alias, o => o.NullSubstitute("fallback")));
        var query = People.ProjectTo<AliasGetter>(config);
        Assert.Contains("COALESCE", query.ToQueryString());
        Assert.Equal(new[] { "public-Ada", "fallback" }, Execute(query, nameof(Getter_Convention_Substitution_Is_Server_Translated), "Alias").Select(d => d.Alias));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Getter_Substitution_Respects_Omission_And_Expansion(bool reflection, bool ignored, bool expand)
    {
        var config = GetterConfig<OptionalGetter>(reflection, m => m.ForMember(d => d.Name, o =>
        { o.MapFrom(nameof(Person.Alias)); o.NullSubstitute("fallback"); if (ignored) o.Ignore(); else o.ExplicitExpansion(); }));
        var query = expand ? People.ProjectTo<OptionalGetter>(config, d => d.Name) : People.ProjectTo<OptionalGetter>(config);
        if (!expand) { Assert.DoesNotContain("Alias", query.ToQueryString()); Assert.DoesNotContain("COALESCE", query.ToQueryString()); }
        Assert.Equal(expand ? new[] { "public-Ada", "fallback" } : new[] { "default", "default" },
            Execute(query, nameof(Getter_Substitution_Respects_Omission_And_Expansion)).Select(d => d.Name));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Getter_Constant_Respects_Expansion_And_Constructor_Override(bool reflection, bool expand)
    {
        var config = GetterConfig<OptionalGetter>(reflection, m => m
            .ForMember(d => d.Name, o => { o.UseValue("fixed"); o.NullSubstitute("fallback"); o.ExplicitExpansion(); })
            .ForCtorParam("name", o => o.MapFrom(p => p.Alias)));
        var query = expand ? People.ProjectTo<OptionalGetter>(config, d => d.Name) : People.ProjectTo<OptionalGetter>(config);
        Assert.DoesNotContain("\"Name\"", query.ToQueryString());
        if (!expand) Assert.DoesNotContain("Alias", query.ToQueryString());
        Assert.Equal(expand ? new[] { "public-Ada", "fallback" } : new[] { "default", "default" },
            Execute(query, nameof(Getter_Constant_Respects_Expansion_And_Constructor_Override)).Select(d => d.Name));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Getter_Constant_Respects_Omission_Without_Constructor_Override(bool reflection, bool ignored, bool expand)
    {
        var config = GetterConfig<OptionalGetter>(reflection, m => m.ForMember(d => d.Name,
            o => { o.UseValue("fixed"); o.ExplicitExpansion(); if (ignored) o.Ignore(); }));
        var query = expand ? People.ProjectTo<OptionalGetter>(config, d => d.Name) : People.ProjectTo<OptionalGetter>(config);
        Assert.DoesNotContain("\"Name\"", query.ToQueryString());
        Assert.All(Execute(query, nameof(Getter_Constant_Respects_Omission_Without_Constructor_Override)),
            d => Assert.Equal(expand && !ignored ? "fixed" : "default", d.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_Constant_Runtime_Condition_Still_Rejects(bool reflection)
    {
        var calls = 0;
        var config = GetterConfig<RequiredGetter>(reflection, m => m.ForMember(d => d.Name,
            o => { o.UseValue("fixed"); o.Condition(p => { calls++; return true; }); }));
        var error = Assert.ThrowsAny<Exception>(() => People.ProjectTo<RequiredGetter>(config)).GetBaseException();
        Assert.Contains("runtime conditions", error.Message);
        Assert.Equal(0, calls);
        Assert.Empty(fixture.Commands.Sql);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_Nullable_Constant_Uses_Typed_Value(bool reflection)
    {
        var config = GetterConfig<NullableCtor>(reflection, m => m.ForMember(d => d.Age, o => o.UseValue(42)));
        var query = People.ProjectTo<NullableCtor>(config);
        Assert.DoesNotContain("Age", query.ToQueryString());
        Assert.All(Execute(query, nameof(Getter_Nullable_Constant_Uses_Typed_Value)), d => { Assert.Equal(42, d.Age); Assert.Equal(5, d.Score); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_Nullable_Source_Substitution_And_Null_Constant(bool reflection)
    {
        var config = GetterConfig<NullableCtor>(reflection, m => m.ForMember(d => d.Age,
            o => { o.MapFrom(nameof(Person.AddressId)); o.NullSubstitute(9); }));
        var query = People.ProjectTo<NullableCtor>(config);
        Assert.Contains("COALESCE", query.ToQueryString());
        Assert.DoesNotContain("\"Age\"", query.ToQueryString());
        Assert.Equal(new int?[] { 1, 9 }, Execute(query, nameof(Getter_Nullable_Source_Substitution_And_Null_Constant), "AddressId").Select(d => d.Age));
        fixture.Commands.Sql.Clear();
        var constant = GetterConfig<NullableCtor>(reflection, m => m.ForMember(d => d.Age,
            o => { o.UseValue(null!); o.NullSubstitute(9); }));
        var constantQuery = People.ProjectTo<NullableCtor>(constant);
        Assert.DoesNotContain("Age", constantQuery.ToQueryString());
        Assert.All(Execute(constantQuery, "Nullable_Null_Constant_Control"), d => Assert.Null(d.Age));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_Invalid_Constant_Has_Member_Guidance(bool reflection)
    {
        var config = GetterConfig<RequiredGetter>(reflection, m => m.ForMember(d => d.Name, o => o.UseValue(42)));
        var error = Assert.ThrowsAny<Exception>(() => People.ProjectTo<RequiredGetter>(config)).GetBaseException();
        Assert.Contains("RequiredGetter.Name", error.Message);
        Assert.Contains("constant", error.Message);
        Assert.Empty(fixture.Commands.Sql);
    }

    [Fact]
    public void Getter_Invalid_Named_Source_Has_Configuration_Guidance()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new MapperConfiguration(c => c.CreateMap<Person, RequiredGetter>()
            .ForMember(d => d.Name, o => o.MapFrom("Unavailable"))));
        Assert.Contains("Unavailable", error.Message);
        Assert.Contains("Name", error.Message);
        Assert.Empty(fixture.Commands.Sql);
    }

    [Fact]
    public void Constructor_Valid_Widest_Ignores_Incompatible_Smaller()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, ValidWidest>().ForCtorParam("id", o => o.MapFrom(p => p.Id)));
        var rows = Execute(People.ProjectTo<ValidWidest>(config), nameof(Constructor_Valid_Widest_Ignores_Incompatible_Smaller), "Id", "Name");
        Assert.Equal(new[] { (1, "Ada"), (2, "Bob") }, rows.Select(d => (d.Id, d.Name)));
    }

    [Fact]
    public void Constructor_Invalid_Widest_Uses_Compatible_Smaller()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, ValidSmaller>().ForCtorParam("id", o => o.MapFrom(p => p.Id)));
        var rows = Execute(People.ProjectTo<ValidSmaller>(config), nameof(Constructor_Invalid_Widest_Uses_Compatible_Smaller), "Id");
        Assert.Equal(new[] { 1, 2 }, rows.Select(d => d.Id));
        Assert.All(rows, d => Assert.Equal("smaller", d.Path));
    }

    [Fact]
    public void Constructor_All_Incompatible_Has_Argument_Guidance()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, IncompatibleCtor>().ForCtorParam("id", o => o.MapFrom(p => p.Id)));
        var error = Assert.ThrowsAny<Exception>(() => People.ProjectTo<IncompatibleCtor>(config)).GetBaseException();
        Assert.Contains(nameof(IncompatibleCtor), error.Message);
        Assert.Contains("id", error.Message);
        Assert.Contains("ForCtorParam", error.Message);
        Assert.Empty(fixture.Commands.Sql);
    }

    [Fact]
    public void Constructor_Explicit_Numeric_Conversion_Remains_Supported()
    {
        var config = new MapperConfiguration(c => c.CreateMap<Person, LongCtor>().ForCtorParam("id", o => o.MapFrom(p => p.Id)));
        Assert.Equal(new long[] { 1, 2 }, Execute(People.ProjectTo<LongCtor>(config), nameof(Constructor_Explicit_Numeric_Conversion_Remains_Supported), "Id").Select(d => d.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fixture_Setup_Failure_Preserves_Error_And_Closes_Connection(bool afterSchema)
    {
        var failure = new SetupFailureInterceptor(afterSchema);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                using var broken = fixture is SqliteFixture
                    ? (ProjectionFixture)new SqliteFixture(failure)
                    : new PostgreSqlFixture(failure);
            });
            Assert.Same(failure.Primary, error);
            Assert.NotNull(failure.Connection);
            Assert.Equal(System.Data.ConnectionState.Closed, failure.Connection.State);
        }
        finally
        {
            // The injected cleanup failure intentionally leaves this owned test schema behind.
            if (failure.Schema != null)
                fixture.Context.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS {failure.Schema} CASCADE");
        }
    }

    public void Dispose() { fixture.Dispose(); GC.SuppressFinalize(this); }
}

public abstract class ProjectionFixture : IDisposable
{
    public ProjectionContext Context { get; protected set; } = null!;
    public CommandCapture Commands { get; } = new();
    protected void Seed()
    {
        Context.People.AddRange(
            new Person { Id = 1, Name = "Ada", Alias = "public-Ada", Age = 37, SecretPayload = "must-never-select", Address = new Address { Id = 1, City = "Ankara" }, Items = [new Item { Id = 1, Label = "book" }] },
            new Person { Id = 2, Name = "Bob", Age = 19, SecretPayload = "must-never-select" });
        Context.SaveChanges();
        Context.ChangeTracker.Clear();
        Commands.Sql.Clear();
    }
    public virtual void Dispose() { Context?.Dispose(); GC.SuppressFinalize(this); }
}

public sealed class CommandCapture : DbCommandInterceptor
{
    public List<string> Sql { get; } = [];
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Sql.Add(command.CommandText); return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Sql.Add(command.CommandText); return ValueTask.FromResult(result); }
}

public sealed class ProjectionContext(DbContextOptions<ProjectionContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Person>().HasOne(p => p.Address).WithMany().HasForeignKey(p => p.AddressId);
        modelBuilder.Entity<Person>().HasMany(p => p.Items).WithOne().HasForeignKey(i => i.PersonId);
    }
}
public sealed class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string? Alias { get; set; }
    public string SecretPayload { get; set; } = "";
    public int? AddressId { get; set; }
    public Address? Address { get; set; }
    public List<Item> Items { get; set; } = [];
}
public sealed class Address { public int Id { get; set; } public string City { get; set; } = ""; }
public sealed class Item { public int Id { get; set; } public int PersonId { get; set; } public string Label { get; set; } = ""; }
public class Summary { public int Id { get; set; } public string Name { get; set; } = ""; }
public sealed class Flat { public int Id { get; set; } public string? AddressCity { get; set; } }
public sealed class Expanded { public int Id { get; set; } public int Age { get; set; } }
public sealed class Immutable(int id, string name) { public int Id { get; } = id; public string Name { get; } = name; }
public sealed class ConfiguredCtor(string label, int rank = 7, string? note = null, int? score = null)
{ public string Label { get; } = label; public int Rank { get; } = rank; public string? Note { get; } = note; public int? Score { get; } = score; }
public sealed class ImmutableAddress(string city) { public string City { get; } = city; }
public sealed class ImmutableItem(string label) { public string Label { get; } = label; }
public sealed class WithAddress { public int Id { get; set; } public ImmutableAddress? Address { get; set; } }
public sealed class WithItems { public int Id { get; set; } public List<ImmutableItem> Items { get; set; } = []; }
public sealed class MissingCtor(string unavailable) { public string Value { get; } = unavailable; }
public sealed class NameResolver : IValueResolver<Person, Summary, string>
{ public string Resolve(Person source, Summary destination, string destMember, ResolutionContext context) => throw new InvalidOperationException("Resolver must not execute"); }

public sealed class AmbiguousCtor
{
    public AmbiguousCtor(int id) { }
    public AmbiguousCtor(string name) { }
}

public sealed class MutableCtor
{
    public MutableCtor() { Path = "default"; }
    public MutableCtor(string name) { Path = name; }
    public int Id { get; set; }
    public string Path { get; }
}
public sealed class WritableCtor(int rank = 7) { public int Rank { get; set; } = rank; }

public sealed record RecordDto(int Id, string Name);

public sealed class NullableCtor(int? age, int? score = 5) { public int? Age { get; } = age; public int? Score { get; } = score; }

public sealed record OptionalRecord(int Id, int Age = 7);

public sealed class AliasGetter(string? alias) { public string? Alias { get; } = alias; }
public sealed class RequiredGetter(string name) { public string Name { get; } = name; }
public sealed class OptionalGetter(string name = "default") { public string Name { get; } = name; }
public class BaseGetter(string name = "default") { public string Name { get; } = name; }
public sealed class DerivedGetter(string name = "default") : BaseGetter(name);
public sealed class ValidWidest
{
    public ValidWidest(int id, string name) { Id = id; Name = name; }
    public ValidWidest(Guid id) { Name = "wrong"; }
    public int Id { get; }
    public string Name { get; }
}
public sealed class ValidSmaller
{
    public ValidSmaller(Guid id, string name) { Path = "wrong"; }
    public ValidSmaller(int id) { Id = id; Path = "smaller"; }
    public int Id { get; }
    public string Path { get; }
}
public sealed class IncompatibleCtor(Guid id) { public Guid Id { get; } = id; }
public sealed class LongCtor(long id) { public long Id { get; } = id; }
internal sealed class ProjectionTestProvider(IConfigurationProvider inner, TypeMap map) : IConfigurationProvider
{
    public TypeMap? FindTypeMap(Type sourceType, Type destinationType) => sourceType == map.SourceType && destinationType == map.DestinationType ? map : inner.FindTypeMap(sourceType, destinationType);
    public IMapper CreateMapper() => inner.CreateMapper();
    public IMapper CreateMapper(IServiceProvider services) => inner.CreateMapper(services);
    public void AssertConfigurationIsValid() => inner.AssertConfigurationIsValid();
    public IReadOnlyCollection<TypeMap> GetAllTypeMaps() => [map];
    public bool AllowNullCollections => inner.AllowNullCollections;
    public bool AllowNullDestinationValues => inner.AllowNullDestinationValues;
    public int DefaultMaxDepth => inner.DefaultMaxDepth;
    public int DefaultMaxCollectionItems => inner.DefaultMaxCollectionItems;
    public string GetMappingPlan<TSource, TDestination>() => inner.GetMappingPlan<TSource, TDestination>();
    public string GetMappingPlan(Type sourceType, Type destinationType) => inner.GetMappingPlan(sourceType, destinationType);
}

internal sealed class SetupFailureInterceptor(bool afterSchema) : DbCommandInterceptor
{
    public InvalidOperationException Primary { get; } = new("primary setup failure");
    public DbConnection? Connection { get; private set; }
    public string? Schema { get; private set; }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (command.CommandText.StartsWith("CREATE SCHEMA ", StringComparison.Ordinal))
            Schema = command.CommandText["CREATE SCHEMA ".Length..];
        if (command.CommandText.StartsWith("DROP SCHEMA", StringComparison.Ordinal))
            throw new InvalidOperationException("secondary cleanup failure");
        if ((!afterSchema && command.CommandText.StartsWith("CREATE SCHEMA", StringComparison.Ordinal)) ||
            command.CommandText.Contains("CREATE TABLE", StringComparison.Ordinal))
        {
            Connection = command.Connection;
            throw Primary;
        }
        return result;
    }
}
