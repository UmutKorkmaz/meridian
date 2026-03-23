using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Pipeline;

namespace Meridian.Mediator.Tests;

#region Test Fixtures — Validation

public record ValidatedRequest(string Name, int Age) : IRequest<string>;

public class ValidatedRequestHandler : IRequestHandler<ValidatedRequest, string>
{
    public Task<string> Handle(ValidatedRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"OK:{request.Name}");
    }
}

public class NameValidator : IValidator<ValidatedRequest>
{
    public Task<ValidationResult> ValidateAsync(ValidatedRequest instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();
        if (string.IsNullOrWhiteSpace(instance.Name))
        {
            result.Errors.Add(new ValidationError("Name", "Name is required"));
        }
        return Task.FromResult(result);
    }
}

public class AgeValidator : IValidator<ValidatedRequest>
{
    public Task<ValidationResult> ValidateAsync(ValidatedRequest instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();
        if (instance.Age < 0)
        {
            result.Errors.Add(new ValidationError("Age", "Age must be non-negative"));
        }
        return Task.FromResult(result);
    }
}

public class AlwaysPassValidator : IValidator<ValidatedRequest>
{
    public Task<ValidationResult> ValidateAsync(ValidatedRequest instance, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ValidationResult());
    }
}

#endregion

#region Test Fixtures — Transaction

public record TransactionalCommand(string Data) : IRequest<string>, ITransactionalRequest;

public class TransactionalCommandHandler : IRequestHandler<TransactionalCommand, string>
{
    public Task<string> Handle(TransactionalCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Done:{request.Data}");
    }
}

public class FailingTransactionalCommandHandler : IRequestHandler<TransactionalCommand, string>
{
    public Task<string> Handle(TransactionalCommand request, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Transaction handler failed");
    }
}

public class FakeTransactionScope : ITransactionScope
{
    public bool Committed { get; private set; }
    public bool RolledBack { get; private set; }
    public bool Disposed { get; private set; }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        Committed = true;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        RolledBack = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public class FakeTransactionScopeProvider : ITransactionScopeProvider
{
    public FakeTransactionScope LastScope { get; private set; } = null!;

    public Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken)
    {
        LastScope = new FakeTransactionScope();
        return Task.FromResult<ITransactionScope>(LastScope);
    }
}

#endregion

#region Test Fixtures — Caching

public record CacheableQuery(string Key) : IRequest<string>, ICacheableQuery
{
    public string CacheKey => $"cache:{Key}";
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(5);
}

public class CacheableQueryHandler : IRequestHandler<CacheableQuery, string>
{
    public static int CallCount { get; set; }

    public Task<string> Handle(CacheableQuery request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult($"Fresh:{request.Key}");
    }
}

public record CacheInvalidatingCommand(string Data) : IRequest<string>, ICacheInvalidatingRequest
{
    public string[] CacheKeysToInvalidate => new[] { "cache:key1", "cache:key2" };
}

public class CacheInvalidatingCommandHandler : IRequestHandler<CacheInvalidatingCommand, string>
{
    public Task<string> Handle(CacheInvalidatingCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Invalidated:{request.Data}");
    }
}

public class FakeCacheProvider : ICacheProvider
{
    private readonly Dictionary<string, object> _store = new();
    public List<string> RemovedKeys { get; } = new();
    public List<(string Key, object Value, TimeSpan? Duration)> SetCalls { get; } = new();

    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_store.TryGetValue(key, out var value))
            return Task.FromResult((true, (object?)value));
        return Task.FromResult((false, (object?)null));
    }

    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        _store[key] = value;
        SetCalls.Add((key, value, duration));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _store.Remove(key);
        RemovedKeys.Add(key);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pre-seed a value for testing cache hits.
    /// </summary>
    public void Seed(string key, object value)
    {
        _store[key] = value;
    }
}

#endregion

#region Test Fixtures — Retry

public record RetryableRequest(int FailuresBeforeSuccess) : IRequest<string>, IRetryableRequest
{
    public int MaxRetries => 3;
    public TimeSpan RetryDelay => TimeSpan.FromMilliseconds(1); // fast for tests
    public bool ShouldRetry(Exception exception) => exception is TransientException;
}

public record NonRetryableRequest() : IRequest<string>, IRetryableRequest
{
    public int MaxRetries => 3;
    public TimeSpan RetryDelay => TimeSpan.FromMilliseconds(1);
    public bool ShouldRetry(Exception exception) => false;
}

public class TransientException : Exception
{
    public TransientException(string message) : base(message) { }
}

#endregion

#region Test Fixtures — Authorization

public record AuthorizedCommand(string UserId) : IRequest<string>, IAuthorizedRequest;

public class AuthorizedCommandHandler : IRequestHandler<AuthorizedCommand, string>
{
    public Task<string> Handle(AuthorizedCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Authorized:{request.UserId}");
    }
}

public class AllowAllAuthorizationHandler : IAuthorizationHandler<AuthorizedCommand>
{
    public Task<AuthorizationResult> AuthorizeAsync(AuthorizedCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(AuthorizationResult.Success());
    }
}

public class DenyAllAuthorizationHandler : IAuthorizationHandler<AuthorizedCommand>
{
    public Task<AuthorizationResult> AuthorizeAsync(AuthorizedCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(AuthorizationResult.Fail("Access denied for user"));
    }
}

#endregion

#region Test Fixtures — Logging

public class FakeMediatorLogger : IMediatorLogger
{
    public List<string> InformationMessages { get; } = new();
    public List<(Exception Exception, string Message)> ErrorMessages { get; } = new();
    public List<string> WarningMessages { get; } = new();

    public void LogInformation(string message, params object[] args)
    {
        InformationMessages.Add(string.Format(message.Replace("{", "{{").Replace("}", "}}"), args.Length > 0 ? args : Array.Empty<object>()).Replace("{{", "{").Replace("}}", "}"));
        // Store the template for easier assertion
        InformationMessages[^1] = FormatMessage(message, args);
    }

    public void LogWarning(string message, params object[] args)
    {
        WarningMessages.Add(FormatMessage(message, args));
    }

    public void LogError(Exception exception, string message, params object[] args)
    {
        ErrorMessages.Add((exception, FormatMessage(message, args)));
    }

    private static string FormatMessage(string template, object[] args)
    {
        // Simple template format: replace {Name} placeholders with args in order
        var result = template;
        for (int i = 0; i < args.Length; i++)
        {
            var idx = result.IndexOf('{');
            if (idx < 0) break;
            var endIdx = result.IndexOf('}', idx);
            if (endIdx < 0) break;
            result = result[..idx] + args[i] + result[(endIdx + 1)..];
        }
        return result;
    }
}

public record LoggedRequest(string Value) : IRequest<string>;

public class LoggedRequestHandler : IRequestHandler<LoggedRequest, string>
{
    public Task<string> Handle(LoggedRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Logged:{request.Value}");
    }
}

public class FailingLoggedRequestHandler : IRequestHandler<LoggedRequest, string>
{
    public Task<string> Handle(LoggedRequest request, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Logging test failure");
    }
}

#endregion

#region Test Fixtures — Idempotency

public record IdempotentCommand(string Key, string Data) : IRequest<string>, IIdempotentRequest
{
    public string IdempotencyKey => Key;
}

public class IdempotentCommandHandler : IRequestHandler<IdempotentCommand, string>
{
    public static int CallCount { get; set; }

    public Task<string> Handle(IdempotentCommand request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult($"Executed:{request.Data}");
    }
}

public class FakeIdempotencyStore : IIdempotencyStore
{
    private readonly Dictionary<string, object> _store = new();

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var value))
            return Task.FromResult((true, (object?)value));
        return Task.FromResult((false, (object?)null));
    }

    public Task StoreAsync(string key, object response, CancellationToken ct)
    {
        _store[key] = response;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pre-seed a value for testing duplicate detection.
    /// </summary>
    public void Seed(string key, object value)
    {
        _store[key] = value;
    }
}

#endregion

public class BehaviorTests
{
    #region 1. ValidationBehavior Tests

    [Fact]
    public async Task ValidationBehavior_Should_Pass_When_No_Validators()
    {
        // Arrange: no validators
        var validators = Enumerable.Empty<IValidator<ValidatedRequest>>();
        var behavior = new ValidationBehavior<ValidatedRequest, string>(validators);
        var request = new ValidatedRequest("Alice", 30);
        var handlerCalled = false;

        RequestHandlerDelegate<string> next = () =>
        {
            handlerCalled = true;
            return Task.FromResult("OK:Alice");
        };

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.True(handlerCalled);
        Assert.Equal("OK:Alice", result);
    }

    [Fact]
    public async Task ValidationBehavior_Should_Pass_When_Validation_Succeeds()
    {
        // Arrange: one validator that always passes
        var validators = new List<IValidator<ValidatedRequest>> { new AlwaysPassValidator() };
        var behavior = new ValidationBehavior<ValidatedRequest, string>(validators);
        var request = new ValidatedRequest("Bob", 25);
        var handlerCalled = false;

        RequestHandlerDelegate<string> next = () =>
        {
            handlerCalled = true;
            return Task.FromResult("OK:Bob");
        };

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.True(handlerCalled);
        Assert.Equal("OK:Bob", result);
    }

    [Fact]
    public async Task ValidationBehavior_Should_Throw_ValidationException_On_Failure()
    {
        // Arrange: NameValidator will fail because name is empty
        var validators = new List<IValidator<ValidatedRequest>> { new NameValidator() };
        var behavior = new ValidationBehavior<ValidatedRequest, string>(validators);
        var request = new ValidatedRequest("", 25);
        var handlerCalled = false;

        RequestHandlerDelegate<string> next = () =>
        {
            handlerCalled = true;
            return Task.FromResult("should not be reached");
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => behavior.Handle(request, next, CancellationToken.None));

        Assert.False(handlerCalled);
        Assert.Single(ex.Errors);
        Assert.Equal("Name", ex.Errors[0].PropertyName);
        Assert.Equal("Name is required", ex.Errors[0].ErrorMessage);
    }

    [Fact]
    public async Task ValidationBehavior_Should_Aggregate_Multiple_Validator_Errors()
    {
        // Arrange: both validators will fail (empty name and negative age)
        var validators = new List<IValidator<ValidatedRequest>>
        {
            new NameValidator(),
            new AgeValidator()
        };
        var behavior = new ValidationBehavior<ValidatedRequest, string>(validators);
        var request = new ValidatedRequest("", -5);
        var handlerCalled = false;

        RequestHandlerDelegate<string> next = () =>
        {
            handlerCalled = true;
            return Task.FromResult("should not be reached");
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => behavior.Handle(request, next, CancellationToken.None));

        Assert.False(handlerCalled);
        Assert.Equal(2, ex.Errors.Count);
        Assert.Contains(ex.Errors, e => e.PropertyName == "Name");
        Assert.Contains(ex.Errors, e => e.PropertyName == "Age");
    }

    #endregion

    #region 2. TransactionBehavior Tests

    [Fact]
    public async Task TransactionBehavior_Should_Commit_On_Success()
    {
        // Arrange
        var transactionProvider = new FakeTransactionScopeProvider();
        var behavior = new TransactionBehavior<TransactionalCommand, string>(transactionProvider);
        var request = new TransactionalCommand("test");

        RequestHandlerDelegate<string> next = () => Task.FromResult("Done:test");

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.Equal("Done:test", result);
        Assert.True(transactionProvider.LastScope.Committed);
        Assert.False(transactionProvider.LastScope.RolledBack);
        Assert.True(transactionProvider.LastScope.Disposed);
    }

    [Fact]
    public async Task TransactionBehavior_Should_Rollback_On_Exception()
    {
        // Arrange
        var transactionProvider = new FakeTransactionScopeProvider();
        var behavior = new TransactionBehavior<TransactionalCommand, string>(transactionProvider);
        var request = new TransactionalCommand("fail");

        RequestHandlerDelegate<string> next = () =>
            throw new InvalidOperationException("Transaction handler failed");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.Handle(request, next, CancellationToken.None));

        Assert.False(transactionProvider.LastScope.Committed);
        Assert.True(transactionProvider.LastScope.RolledBack);
        Assert.True(transactionProvider.LastScope.Disposed);
    }

    #endregion

    #region 3. CachingBehavior Tests

    [Fact]
    public async Task CachingBehavior_Should_Return_Cached_Value_On_Hit()
    {
        // Arrange
        var cacheProvider = new FakeCacheProvider();
        cacheProvider.Seed("cache:mykey", "CachedValue");
        var behavior = new CachingBehavior<CacheableQuery, string>(cacheProvider);
        var request = new CacheableQuery("mykey");
        var handlerCalled = false;

        RequestHandlerDelegate<string> next = () =>
        {
            handlerCalled = true;
            return Task.FromResult("Fresh:mykey");
        };

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.Equal("CachedValue", result);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task CachingBehavior_Should_Call_Handler_And_Cache_On_Miss()
    {
        // Arrange
        var cacheProvider = new FakeCacheProvider();
        var behavior = new CachingBehavior<CacheableQuery, string>(cacheProvider);
        var request = new CacheableQuery("newkey");
        var handlerCalled = false;

        RequestHandlerDelegate<string> next = () =>
        {
            handlerCalled = true;
            return Task.FromResult("Fresh:newkey");
        };

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.True(handlerCalled);
        Assert.Equal("Fresh:newkey", result);
        Assert.Single(cacheProvider.SetCalls);
        Assert.Equal("cache:newkey", cacheProvider.SetCalls[0].Key);
        Assert.Equal("Fresh:newkey", cacheProvider.SetCalls[0].Value);
        Assert.Equal(TimeSpan.FromMinutes(5), cacheProvider.SetCalls[0].Duration);
    }

    [Fact]
    public async Task CacheInvalidationBehavior_Should_Remove_Keys_After_Success()
    {
        // Arrange
        var cacheProvider = new FakeCacheProvider();
        cacheProvider.Seed("cache:key1", "val1");
        cacheProvider.Seed("cache:key2", "val2");
        var behavior = new CacheInvalidationBehavior<CacheInvalidatingCommand, string>(cacheProvider);
        var request = new CacheInvalidatingCommand("data");

        RequestHandlerDelegate<string> next = () => Task.FromResult("Invalidated:data");

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.Equal("Invalidated:data", result);
        Assert.Contains("cache:key1", cacheProvider.RemovedKeys);
        Assert.Contains("cache:key2", cacheProvider.RemovedKeys);
        Assert.Equal(2, cacheProvider.RemovedKeys.Count);
    }

    #endregion

    #region 4. RetryBehavior Tests

    [Fact]
    public async Task RetryBehavior_Should_Retry_On_Transient_Failure()
    {
        // Arrange
        var behavior = new RetryBehavior<RetryableRequest, string>();
        var request = new RetryableRequest(2); // fail 2 times, then succeed
        var attempt = 0;

        RequestHandlerDelegate<string> next = () =>
        {
            attempt++;
            if (attempt <= 2)
                throw new TransientException($"Transient failure #{attempt}");
            return Task.FromResult("Success");
        };

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.Equal("Success", result);
        Assert.Equal(3, attempt); // 2 failures + 1 success
    }

    [Fact]
    public async Task RetryBehavior_Should_Not_Retry_When_ShouldRetry_Returns_False()
    {
        // Arrange
        var behavior = new RetryBehavior<NonRetryableRequest, string>();
        var request = new NonRetryableRequest();
        var attempt = 0;

        RequestHandlerDelegate<string> next = () =>
        {
            attempt++;
            throw new InvalidOperationException("Non-retryable error");
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.Handle(request, next, CancellationToken.None));

        Assert.Equal(1, attempt); // only one attempt, no retries
    }

    [Fact]
    public async Task RetryBehavior_Should_Throw_After_Max_Retries()
    {
        // Arrange
        var behavior = new RetryBehavior<RetryableRequest, string>();
        var request = new RetryableRequest(999); // always fails
        var attempt = 0;

        RequestHandlerDelegate<string> next = () =>
        {
            attempt++;
            throw new TransientException($"Transient failure #{attempt}");
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<TransientException>(
            () => behavior.Handle(request, next, CancellationToken.None));

        // MaxRetries = 3, so initial attempt + 3 retries = 4 total
        Assert.Equal(4, attempt);
    }

    #endregion

    #region 5. AuthorizationBehavior Tests

    [Fact]
    public async Task AuthorizationBehavior_Should_Pass_When_Authorized()
    {
        // Arrange
        var authHandlers = new List<IAuthorizationHandler<AuthorizedCommand>>
        {
            new AllowAllAuthorizationHandler()
        };
        var behavior = new AuthorizationBehavior<AuthorizedCommand, string>(authHandlers);
        var request = new AuthorizedCommand("user1");
        var handlerCalled = false;

        RequestHandlerDelegate<string> next = () =>
        {
            handlerCalled = true;
            return Task.FromResult("Authorized:user1");
        };

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.True(handlerCalled);
        Assert.Equal("Authorized:user1", result);
    }

    [Fact]
    public async Task AuthorizationBehavior_Should_Throw_UnauthorizedException_When_Not_Authorized()
    {
        // Arrange
        var authHandlers = new List<IAuthorizationHandler<AuthorizedCommand>>
        {
            new DenyAllAuthorizationHandler()
        };
        var behavior = new AuthorizationBehavior<AuthorizedCommand, string>(authHandlers);
        var request = new AuthorizedCommand("user1");
        var handlerCalled = false;

        RequestHandlerDelegate<string> next = () =>
        {
            handlerCalled = true;
            return Task.FromResult("should not be reached");
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => behavior.Handle(request, next, CancellationToken.None));

        Assert.False(handlerCalled);
        Assert.Equal("Access denied for user", ex.Message);
    }

    #endregion

    #region 6. LoggingBehavior Tests

    [Fact]
    public async Task LoggingBehavior_Should_Log_Start_And_Completion()
    {
        // Arrange
        var logger = new FakeMediatorLogger();
        var behavior = new Behaviors.LoggingBehavior<LoggedRequest, string>(logger);
        var request = new LoggedRequest("test");

        RequestHandlerDelegate<string> next = () => Task.FromResult("Logged:test");

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.Equal("Logged:test", result);
        Assert.Equal(2, logger.InformationMessages.Count);
        Assert.Contains("LoggedRequest", logger.InformationMessages[0]); // Start log
        Assert.Contains("LoggedRequest", logger.InformationMessages[1]); // Completion log
        Assert.Contains("ms", logger.InformationMessages[1]); // Contains elapsed time
        Assert.Empty(logger.ErrorMessages);
    }

    [Fact]
    public async Task LoggingBehavior_Should_Log_Errors()
    {
        // Arrange
        var logger = new FakeMediatorLogger();
        var behavior = new Behaviors.LoggingBehavior<LoggedRequest, string>(logger);
        var request = new LoggedRequest("fail");

        RequestHandlerDelegate<string> next = () =>
            throw new InvalidOperationException("Logging test failure");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.Handle(request, next, CancellationToken.None));

        // Should have 1 info (start) and 1 error
        Assert.Single(logger.InformationMessages);
        Assert.Contains("LoggedRequest", logger.InformationMessages[0]);
        Assert.Single(logger.ErrorMessages);
        Assert.IsType<InvalidOperationException>(logger.ErrorMessages[0].Exception);
        Assert.Contains("LoggedRequest", logger.ErrorMessages[0].Message);
    }

    #endregion

    #region 7. CorrelationId Tests

    [Fact]
    public async Task CorrelationIdBehavior_Should_Set_CorrelationId_When_Missing()
    {
        // Arrange
        CorrelationContext.CorrelationId = null; // Ensure no existing ID
        var behavior = new CorrelationIdBehavior<Ping, string>();
        var request = new Ping("test");
        string? capturedCorrelationId = null;

        RequestHandlerDelegate<string> next = () =>
        {
            // Capture inside the handler delegate where AsyncLocal is set
            capturedCorrelationId = CorrelationContext.CorrelationId;
            return Task.FromResult("Pong: test");
        };

        // Act
        await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedCorrelationId);
        Assert.NotEmpty(capturedCorrelationId!);
        Assert.Equal(32, capturedCorrelationId!.Length); // GUID "N" format = 32 chars
    }

    [Fact]
    public async Task CorrelationIdBehavior_Should_Preserve_Existing_CorrelationId()
    {
        // Arrange
        var existingId = "my-existing-correlation-id";
        CorrelationContext.CorrelationId = existingId;
        var behavior = new CorrelationIdBehavior<Ping, string>();
        var request = new Ping("test");
        string? capturedCorrelationId = null;

        RequestHandlerDelegate<string> next = () =>
        {
            capturedCorrelationId = CorrelationContext.CorrelationId;
            return Task.FromResult("Pong: test");
        };

        // Act
        await behavior.Handle(request, next, CancellationToken.None);

        // Assert — the existing ID should be preserved, not overwritten
        Assert.Equal(existingId, capturedCorrelationId);
    }

    #endregion

    #region 8. IdempotencyBehavior Tests

    [Fact]
    public async Task IdempotencyBehavior_Should_Return_Cached_On_Duplicate()
    {
        // Arrange
        IdempotentCommandHandler.CallCount = 0;
        var store = new FakeIdempotencyStore();
        store.Seed("dup-key", "PreviousResult");
        var behavior = new IdempotencyBehavior<IdempotentCommand, string>(store);
        var request = new IdempotentCommand("dup-key", "data");
        var handlerCalled = false;

        RequestHandlerDelegate<string> next = () =>
        {
            handlerCalled = true;
            return Task.FromResult("Executed:data");
        };

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.Equal("PreviousResult", result);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task IdempotencyBehavior_Should_Execute_And_Store_On_First_Call()
    {
        // Arrange
        var store = new FakeIdempotencyStore();
        var behavior = new IdempotencyBehavior<IdempotentCommand, string>(store);
        var request = new IdempotentCommand("new-key", "newdata");
        var handlerCalled = false;

        RequestHandlerDelegate<string> next = () =>
        {
            handlerCalled = true;
            return Task.FromResult("Executed:newdata");
        };

        // Act
        var result = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.True(handlerCalled);
        Assert.Equal("Executed:newdata", result);

        // Verify it was stored — a second call should return cached
        var (exists, cached) = await store.CheckAsync("new-key", CancellationToken.None);
        Assert.True(exists);
        Assert.Equal("Executed:newdata", cached);
    }

    #endregion
}
