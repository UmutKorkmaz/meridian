using System.Reflection;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit.Sdk;

namespace Meridian.Mapping.Tests;

public sealed class PropertyConfigurationTests
{
    // Catch an override that is parsed but never reaches FsCheck's base attribute config.
    [Fact]
    public void Count_Override_Is_Consumed()
    {
        PropertyAttribute attribute = new MeridianPropertyAttribute(25,
            name => name == "MERIDIAN_FSCHECK_MAX_TEST" ? "10000" : null);

        Assert.Equal(10000, attribute.MaxTest);
        var config = RunnerConfiguration(attribute);
        var cases = 0;
        Check.One(config, Prop.ForAll<int>(_ => { cases++; return true; }));
        Assert.Equal(10000, cases);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("oops")]
    [InlineData("2147483648")]
    [InlineData("")]
    public void Invalid_Count_Is_Rejected(string value)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new MeridianPropertyAttribute(100, name => name == "MERIDIAN_FSCHECK_MAX_TEST" ? value : null));

        Assert.Contains("MERIDIAN_FSCHECK_MAX_TEST", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Collection_Budget_Is_Separate()
    {
        static string? Read(string name) => name switch
        {
            "MERIDIAN_FSCHECK_MAX_TEST" => "10000",
            "MERIDIAN_FSCHECK_COLLECTION_MAX_TEST" => "500",
            _ => null
        };
        PropertyAttribute collection = new CollectionMeridianPropertyAttribute(10, Read);

        Assert.Equal(500, collection.MaxTest);
        var cases = 0;
        Check.One(RunnerConfiguration(collection), Prop.ForAll<int>(_ => { cases++; return true; }));
        Assert.Equal(500, cases);
        Assert.Equal(10000, new MeridianPropertyAttribute(25, Read).MaxTest);
        Assert.Equal(10, new CollectionMeridianPropertyAttribute(10,
            name => name == "MERIDIAN_FSCHECK_MAX_TEST" ? "10000" : null).MaxTest);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(25)]
    public void UnsetCountPreservesDefault(int budget)
    {
        PropertyAttribute attribute = new MeridianPropertyAttribute(budget, _ => null);
        Assert.Equal(budget, attribute.MaxTest);
        Assert.Null(attribute.Replay);
    }

    [Fact]
    public void ReplayIsForwardedToFsCheck()
    {
        PropertyAttribute attribute = new MeridianPropertyAttribute(100,
            name => name == "MERIDIAN_FSCHECK_REPLAY" ? "12345,11400714819323198485" : null);
        Assert.Equal("12345,11400714819323198485", attribute.Replay);
        var replay = RunnerConfiguration(attribute).Replay.Value;
        Assert.Equal(12345UL, replay.Rnd.Seed);
        Assert.Equal(11400714819323198485UL, replay.Rnd.Gamma);
    }

    // FsCheck 3.3.3's PropertyTestCase.Init uses this conversion. Characterize
    // our boundary through that real conversion, without mutating global env.
    private static Config RunnerConfiguration(PropertyAttribute attribute)
    {
        var config = typeof(PropertyAttribute).GetProperty("Config", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(attribute);
        var module = typeof(PropertyAttribute).Assembly.GetType("FsCheck.Xunit.PropertyConfigModule", throwOnError: true)!;
        return (Config)module.GetMethod("toConfig", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new TestOutputHelper(), config])!;
    }

    [Theory]
    [InlineData("123")]
    [InlineData("123,2")]
    [InlineData("abc,1")]
    [InlineData("123,1,10")]
    public void InvalidReplayIsRejected(string replay)
    {
        Assert.Throws<ArgumentException>(() => new MeridianPropertyAttribute(100,
            name => name == "MERIDIAN_FSCHECK_REPLAY" ? replay : null));
    }
}
