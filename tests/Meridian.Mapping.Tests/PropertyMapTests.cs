using System;
using System.Linq.Expressions;
using System.Reflection;
using Meridian.Mapping.Execution;
using Xunit;

namespace Meridian.Mapping.Tests;

public class PropertyMapTests
{
    private class DummySource
    {
        public string SourceProp { get; set; } = string.Empty;
        public DummyNestedSource NestedSource { get; set; } = new DummyNestedSource();
    }

    private class DummyNestedSource
    {
        public string NestedProp { get; set; } = string.Empty;
    }

    private class DummyDest
    {
        public string DestProp { get; set; } = string.Empty;
        public DummyNestedDest NestedDest { get; set; } = new DummyNestedDest();
    }

    private class DummyNestedDest
    {
        public string NestedProp { get; set; } = string.Empty;
    }

    [Fact]
    public void Compile_Should_Assign_CompiledSetter_For_Single_DestinationProperty()
    {
        // Arrange
        var destProp = typeof(DummyDest).GetProperty(nameof(DummyDest.DestProp))!;
        var map = new PropertyMap(destProp);

        // Act
        map.Compile();

        // Assert
        Assert.NotNull(map.CompiledSetter);

        var dest = new DummyDest();
        map.CompiledSetter(dest, "TestValue");
        Assert.Equal("TestValue", dest.DestProp);
    }

    [Fact]
    public void Compile_Should_Assign_CompiledSetter_For_DestinationPropertyChain()
    {
        // Arrange
        var destProp = typeof(DummyNestedDest).GetProperty(nameof(DummyNestedDest.NestedProp))!;
        var chain = new PropertyInfo[]
        {
            typeof(DummyDest).GetProperty(nameof(DummyDest.NestedDest))!,
            destProp
        };
        var map = new PropertyMap(destProp, chain);

        // Act
        map.Compile();

        // Assert
        Assert.NotNull(map.CompiledSetter);

        var dest = new DummyDest { NestedDest = null! }; // To test intermediate object creation
        map.CompiledSetter(dest, "TestValue");

        Assert.NotNull(dest.NestedDest);
        Assert.Equal("TestValue", dest.NestedDest.NestedProp);
    }

    [Fact]
    public void Compile_Should_Assign_CompiledGetter_For_Single_SourceProperty()
    {
        // Arrange
        var destProp = typeof(DummyDest).GetProperty(nameof(DummyDest.DestProp))!;
        var sourceProp = typeof(DummySource).GetProperty(nameof(DummySource.SourceProp))!;

        var map = new PropertyMap(destProp)
        {
            SourceProperty = sourceProp
        };

        // Act
        map.Compile();

        // Assert
        Assert.NotNull(map.CompiledGetter);

        var source = new DummySource { SourceProp = "SourceValue" };
        var result = map.CompiledGetter(source);
        Assert.Equal("SourceValue", result);
    }

    [Fact]
    public void Compile_Should_Assign_CompiledGetter_For_SourcePropertyChain()
    {
        // Arrange
        var destProp = typeof(DummyDest).GetProperty(nameof(DummyDest.DestProp))!;
        var chain = new PropertyInfo[]
        {
            typeof(DummySource).GetProperty(nameof(DummySource.NestedSource))!,
            typeof(DummyNestedSource).GetProperty(nameof(DummyNestedSource.NestedProp))!
        };

        var map = new PropertyMap(destProp)
        {
            SourcePropertyChain = chain
        };

        // Act
        map.Compile();

        // Assert
        Assert.NotNull(map.CompiledGetter);

        var source = new DummySource { NestedSource = new DummyNestedSource { NestedProp = "NestedSourceValue" } };
        var result = map.CompiledGetter(source);
        Assert.Equal("NestedSourceValue", result);

        // Test null intermediate
        var nullSource = new DummySource { NestedSource = null! };
        var nullResult = map.CompiledGetter(nullSource);
        Assert.Null(nullResult);
    }

    [Fact]
    public void Compile_Should_Assign_CompiledGetter_For_CustomMapExpression()
    {
        // Arrange
        var destProp = typeof(DummyDest).GetProperty(nameof(DummyDest.DestProp))!;

        Expression<Func<DummySource, object>> expr = src => src.SourceProp + "_Custom";

        var map = new PropertyMap(destProp)
        {
            CustomMapExpression = expr
        };

        // Act
        map.Compile();

        // Assert
        Assert.NotNull(map.CompiledGetter);

        var source = new DummySource { SourceProp = "Base" };
        var result = map.CompiledGetter(source);
        Assert.Equal("Base_Custom", result);
    }
}
