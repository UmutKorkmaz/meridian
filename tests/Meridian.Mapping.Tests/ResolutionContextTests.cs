using Xunit;
using System;
using System.Linq;
using System.Linq.Expressions;
using Meridian.Mapping.Execution;

namespace Meridian.Mapping.Tests;

public class ResolutionContextTests
{
    private class DummyMapper : IMapper
    {
        public IConfigurationProvider ConfigurationProvider => throw new NotImplementedException();
        public TDestination Map<TDestination>(object source) => throw new NotImplementedException();
        public TDestination Map<TDestination>(object source, Action<IMappingOperationOptions> opts) => throw new NotImplementedException();
        public TDestination Map<TSource, TDestination>(TSource source) => throw new NotImplementedException();
        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination) => throw new NotImplementedException();
        public TDestination Map<TSource, TDestination>(TSource source, Action<IMappingOperationOptions> opts) => throw new NotImplementedException();
        public TDestination Map<TSource, TDestination>(TSource source, Action<IMappingOperationOptions<TSource, TDestination>> opts) => throw new NotImplementedException();
        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination, Action<IMappingOperationOptions> opts) => throw new NotImplementedException();
        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination, Action<IMappingOperationOptions<TSource, TDestination>> opts) => throw new NotImplementedException();
        public object Map(object source, Type sourceType, Type destinationType) => throw new NotImplementedException();
        public object Map(object source, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) => throw new NotImplementedException();
        public object Map(object source, object destination, Type sourceType, Type destinationType) => throw new NotImplementedException();
        public object Map(object source, object destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) => throw new NotImplementedException();
        public IQueryable<TDestination> ProjectTo<TDestination>(IQueryable source, params Expression<Func<TDestination, object>>[]? membersToExpand) => throw new NotImplementedException();
    }

    [Fact]
    public void TryGetMapped_ReturnsFalse_WhenNoObjectsCached()
    {
        // Arrange
        var mapper = new DummyMapper();
        var context = new ResolutionContext(mapper);
        var source = new object();

        // Act
        var result = context.TryGetMapped(source, typeof(string), out var mapped);

        // Assert
        Assert.False(result);
        Assert.Null(mapped);
    }

    [Fact]
    public void TryGetMapped_ReturnsTrue_AndSetsMapped_WhenObjectIsCached()
    {
        // Arrange
        var mapper = new DummyMapper();
        var context = new ResolutionContext(mapper);
        var source = new object();
        var destType = typeof(string);
        var expectedMapped = "mapped value";

        context.CacheMapped(source, destType, expectedMapped);

        // Act
        var result = context.TryGetMapped(source, destType, out var mapped);

        // Assert
        Assert.True(result);
        Assert.Equal(expectedMapped, mapped);
    }

    [Fact]
    public void TryGetMapped_ReturnsFalse_WhenDifferentSourceCached()
    {
        // Arrange
        var mapper = new DummyMapper();
        var context = new ResolutionContext(mapper);
        var source1 = new object();
        var source2 = new object();
        var destType = typeof(string);
        var expectedMapped = "mapped value";

        context.CacheMapped(source1, destType, expectedMapped);

        // Act
        var result = context.TryGetMapped(source2, destType, out var mapped);

        // Assert
        Assert.False(result);
        Assert.Null(mapped);
    }

    [Fact]
    public void TryGetMapped_ReturnsFalse_WhenDifferentDestTypeCached()
    {
        // Arrange
        var mapper = new DummyMapper();
        var context = new ResolutionContext(mapper);
        var source = new object();
        var destType1 = typeof(string);
        var destType2 = typeof(int);
        var expectedMapped = "mapped value";

        context.CacheMapped(source, destType1, expectedMapped);

        // Act
        var result = context.TryGetMapped(source, destType2, out var mapped);

        // Assert
        Assert.False(result);
        Assert.Null(mapped);
    }
}
