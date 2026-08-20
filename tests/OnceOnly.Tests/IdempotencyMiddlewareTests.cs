using Microsoft.AspNetCore.Http;
using NSubstitute;
using OnceOnly.Middleware;
using OnceOnly.Middleware.DTO;
using OnceOnly.Middleware.Enums;
using System.Text;

namespace OnceOnly.Tests;

/// <summary>
/// Unit tests for IdempotencyMiddleware, covering response capture and replay flows.
/// </summary>
public class IdempotencyMiddlewareTests
{
    private readonly IIdempotencyStore _mockStore;
    private readonly IdempotencyOptions _options;

    public IdempotencyMiddlewareTests()
    {
        _mockStore = Substitute.For<IIdempotencyStore>();
        _options = new IdempotencyOptions
        {
            HeadersToPersist = new List<string> { "Content-Type", "X-Custom-Header" },
            IdempotencyKeyHeader = "Idempotency-Key"
        };
    }

    /// <summary>
    /// Tests that a request without an idempotency header passes through without store checks.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_NoIdempotencyKey_PassesThrough()
    {
        // Arrange
        var context = CreateHttpContext();
        // Don't add the Idempotency-Key header

        bool nextCalled = false;
        var middleware = new IdempotencyMiddleware(
            next: (context) =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            store: _mockStore,
            options: _options
        );

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled, "next() should have been called");
        await _mockStore.DidNotReceive().TryAcquireLockAsync(Arg.Any<string>());
    }

    /// <summary>
    /// (a) Tests that a new key (NotExists) calls next() and persists the response correctly.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_NewKey_CallsNextAndPersistsResponse()
    {
        // Arrange
        var context = CreateHttpContext();
        context.Request.Headers["Idempotency-Key"] = "test-key-123";

        _mockStore.TryAcquireLockAsync("test-key-123").Returns(LockResultEnum.NotExists);

        bool nextCalled = false;
        SavedResponse? capturedResponse = null;
        await _mockStore.SaveResponseAsync(Arg.Any<string>(), Arg.Do<SavedResponse>(r => capturedResponse = r));

        // Simulate an endpoint that writes a JSON response
        var middleware = new IdempotencyMiddleware(
            next: async (ctx) =>
            {
                nextCalled = true;
                ctx.Response.StatusCode = 201;
                ctx.Response.Headers["Content-Type"] = "application/json";
                ctx.Response.Headers["X-Custom-Header"] = "CustomValue";
                await ctx.Response.WriteAsync("{\"id\":42,\"status\":\"created\"}");
            },
            store: _mockStore,
            options: _options
        );

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled, "next() should have been called");
        
        // Verify that SaveResponseAsync was called
        await _mockStore.Received(1).SaveResponseAsync("test-key-123", Arg.Any<SavedResponse>());

        // Verify the captured data
        Assert.NotNull(capturedResponse);
        Assert.Equal(201, capturedResponse.StatusCode);
        Assert.Equal("application/json", capturedResponse.Headers["Content-Type"]);
        Assert.Equal("CustomValue", capturedResponse.Headers["X-Custom-Header"]);
        
        var bodyText = Encoding.UTF8.GetString(capturedResponse.Body);
        Assert.Equal("{\"id\":42,\"status\":\"created\"}", bodyText);

        // Verify that the response was sent to the client (copied to the original stream)
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        Assert.Equal("{\"id\":42,\"status\":\"created\"}", responseBody);
    }

    /// <summary>
    /// (b) Tests that an existing key (Unlocked) returns the saved response without calling next().
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ExistingKey_ReturnsStoredResponseWithoutCallingNext()
    {
        // Arrange
        var context = CreateHttpContext();
        context.Request.Headers["Idempotency-Key"] = "existing-key-456";

        _mockStore.TryAcquireLockAsync("existing-key-456").Returns(LockResultEnum.Unlocked);

        var storedResponse = new SavedResponse(
            StatusCode: 200,
            Body: Encoding.UTF8.GetBytes("{\"cached\":true}"),
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Custom-Header"] = "CachedValue"
            }
        );

        _mockStore.GetSavedResponseAsync("existing-key-456").Returns(storedResponse);

        bool nextCalled = false;
        var middleware = new IdempotencyMiddleware(
            next: (context) =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            store: _mockStore,
            options: _options
        );

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.False(nextCalled, "next() should NOT have been called during replay");
        
        // Verify that the status and headers were reapplied
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.Headers["Content-Type"].ToString());
        Assert.Equal("CachedValue", context.Response.Headers["X-Custom-Header"].ToString());

        // Verify that the body was written
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        Assert.Equal("{\"cached\":true}", responseBody);

        // Verify that SaveResponseAsync was NOT called (no persistence during replay)
        await _mockStore.DidNotReceive().SaveResponseAsync(Arg.Any<string>(), Arg.Any<SavedResponse>());
    }

    /// <summary>
    /// Tests that a key being processed (Locked) returns 409 Conflict.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_LockedKey_Returns409Conflict()
    {
        // Arrange
        var context = CreateHttpContext();
        context.Request.Headers["Idempotency-Key"] = "locked-key-789";

        _mockStore.TryAcquireLockAsync("locked-key-789").Returns(LockResultEnum.Locked);

        bool nextCalled = false;
        var middleware = new IdempotencyMiddleware(
            next: (context) =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            store: _mockStore,
            options: _options
        );

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.False(nextCalled, "next() should NOT have been called for a locked key");
        Assert.Equal(409, context.Response.StatusCode);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        Assert.Contains("still being processed", responseBody);
    }

    /// <summary>
    /// (c) Tests that an exception thrown inside next() does not result in a saved response
    /// and still restores the original Response.Body.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ExceptionDuringNext_DoesNotSaveResponseAndRestoresOriginalBody()
    {
        // Arrange
        var context = CreateHttpContext();
        context.Request.Headers["Idempotency-Key"] = "exception-key-999";

        _mockStore.TryAcquireLockAsync("exception-key-999").Returns(LockResultEnum.NotExists);

        var originalBodyStream = context.Response.Body;
        var testException = new InvalidOperationException("Simulated endpoint error");
        bool nextCalled = false;

        var middleware = new IdempotencyMiddleware(
            next: async (ctx) =>
            {
                nextCalled = true;
                // Simulate a partial write before the exception
                await ctx.Response.WriteAsync("starting...");
                throw testException;
            },
            store: _mockStore,
            options: _options
        );

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await middleware.InvokeAsync(context)
        );

        Assert.Equal("Simulated endpoint error", exception.Message);
        Assert.True(nextCalled, "next() should have been called before the exception");

        // Verify that SaveResponseAsync was NOT called (exception prevented persistence)
        await _mockStore.DidNotReceive().SaveResponseAsync(Arg.Any<string>(), Arg.Any<SavedResponse>());

        // Verify that the original stream was restored (critical check)
        Assert.Same(originalBodyStream, context.Response.Body);
    }

    /// <summary>
    /// Tests that headers not configured in HeadersToPersist are not saved or reapplied.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_OnlyConfiguredHeadersArePersisted()
    {
        // Arrange
        var context = CreateHttpContext();
        context.Request.Headers["Idempotency-Key"] = "headers-test-key";

        _mockStore.TryAcquireLockAsync("headers-test-key").Returns(LockResultEnum.NotExists);

        SavedResponse? capturedResponse = null;
        await _mockStore.SaveResponseAsync(Arg.Any<string>(), Arg.Do<SavedResponse>(r => capturedResponse = r));

        var middleware = new IdempotencyMiddleware(
            next: async (ctx) =>
            {
                ctx.Response.Headers["Content-Type"] = "text/plain";
                ctx.Response.Headers["X-Custom-Header"] = "SaveMe";
                ctx.Response.Headers["X-Not-Configured"] = "DontSaveMe"; // Not in HeadersToPersist
                await ctx.Response.WriteAsync("test");
            },
            store: _mockStore,
            options: _options
        );

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.NotNull(capturedResponse);
        Assert.True(capturedResponse.Headers.ContainsKey("Content-Type"));
        Assert.True(capturedResponse.Headers.ContainsKey("X-Custom-Header"));
        Assert.False(capturedResponse.Headers.ContainsKey("X-Not-Configured"),
            "Unconfigured headers should not be persisted");
    }

    /// <summary>
    /// Helper to create a test HttpContext with configurable streams.
    /// </summary>
    private DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream(); // Writable stream for tests
        return context;
    }
}
