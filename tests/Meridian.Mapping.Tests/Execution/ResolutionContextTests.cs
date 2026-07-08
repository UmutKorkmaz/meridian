using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Meridian.Mapping.Execution;
using Xunit;

namespace Meridian.Mapping.Tests.Execution;

public class DummyMapper : IMapper
{
    public TDestination Map<TDestination>(object source) => throw new NotImplementedException();
    public TDestination Map<TDestination>(object source, Action<IMappingOperationOptions> opts) => throw new NotImplementedException();

    public TDestination Map<TSource, TDestination>(TSource source) => throw new NotImplementedException();
    public TDestination Map<TSource, TDestination>(TSource source, Action<IMappingOperationOptions<TSource, TDestination>> opts) => throw new NotImplementedException();

    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination) => throw new NotImplementedException();
    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination, Action<IMappingOperationOptions<TSource, TDestination>> opts) => throw new NotImplementedException();

    public object Map(object source, Type sourceType, Type destinationType) => throw new NotImplementedException();
    public object Map(object source, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) => throw new NotImplementedException();

    public object Map(object source, object destination, Type sourceType, Type destinationType) => throw new NotImplementedException();
    public object Map(object source, object destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) => throw new NotImplementedException();

    public IQueryable<TDestination> ProjectTo<TDestination>(IQueryable source, params Expression<Func<TDestination, object>>[]? membersToExpand) => throw new NotImplementedException();

    public IConfigurationProvider ConfigurationProvider => throw new NotImplementedException();
}

public class ResolutionContextTests
{
    [Fact]
    public void CacheMapped_CachesAndRetrievesObject()
    {
        // Arrange
        var dummyMapper = new DummyMapper();
        var context = new ResolutionContext(dummyMapper);
        var source = new object();
        var destType = typeof(string);
        var mapped = "mapped string";

        // Act
        context.CacheMapped(source, destType, mapped);
        var found = context.TryGetMapped(source, destType, out var retrieved);

        // Assert
        Assert.True(found);
        Assert.Equal(mapped, retrieved);
    }

    [Fact]
    public void TryGetMapped_ReturnsFalse_BeforeCacheMappedIsCalled()
    {
        // Arrange
        var dummyMapper = new DummyMapper();
        var context = new ResolutionContext(dummyMapper);
        var source = new object();
        var destType = typeof(string);

        // Act
        var found = context.TryGetMapped(source, destType, out var retrieved);

        // Assert
        Assert.False(found);
        Assert.Null(retrieved);
    }
}
