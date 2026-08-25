using OnceOnly.Middleware;
using OnceOnly.Middleware.DTO;
using OnceOnly.Middleware.Enums;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace OnceOnly.Tests;

/// <summary>
/// Integration tests for <see cref="RedisIdempotencyStore"/> using Testcontainers
/// to spin up a real Redis instance. These tests validate actual atomic behavior
/// against Redis rather than mocking IDatabase/IConnectionMultiplexer.
/// </summary>
public class RedisIdempotencyStoreIntegrationTests : IAsyncLifetime
{
    private readonly RedisContainer _redisContainer = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    private IConnectionMultiplexer _connectionMultiplexer = null!;
    private IDatabase _database = null!;
    private IdempotencyOptions _options = null!;
    private RedisIdempotencyStore _store = null!;

    public async Task InitializeAsync()
    {
        await _redisContainer.StartAsync();

        _connectionMultiplexer = await ConnectionMultiplexer.ConnectAsync(
            _redisContainer.GetConnectionString());
        _database = _connectionMultiplexer.GetDatabase();

        _options = new IdempotencyOptions
        {
            LockTtl = TimeSpan.FromSeconds(30),
            SavedResponseTtl = TimeSpan.FromHours(24)
        };

        _store = new RedisIdempotencyStore(_connectionMultiplexer, _options);
    }

    public async Task DisposeAsync()
    {
        _connectionMultiplexer?.Dispose();
        await _redisContainer.DisposeAsync();
    }

    /// <summary>
    /// (a) TryAcquireLockAsync returns "lock acquired" (NotExists) for a brand-new key.
    /// </summary>
    [Fact]
    public async Task TryAcquireLockAsync_NewKey_ReturnsNotExists()
    {
        // Arrange
        var idempotencyKey = $"test-key-{Guid.NewGuid()}";

        // Act
        var result = await _store.TryAcquireLockAsync(idempotencyKey);

        // Assert
        Assert.Equal(LockResultEnum.NotExists, result);

        // Verify the lock key was created in Redis
        var lockKeyExists = await _database.KeyExistsAsync($"onceonly:lock:{idempotencyKey}");
        Assert.True(lockKeyExists, "Lock key should exist in Redis after acquisition");
    }

    /// <summary>
    /// (b) A second concurrent call with the same key returns "already in progress" (Locked)
    /// before any response is saved.
    /// </summary>
    [Fact]
    public async Task TryAcquireLockAsync_ConcurrentCall_ReturnsLocked()
    {
        // Arrange
        var idempotencyKey = $"test-key-{Guid.NewGuid()}";

        // First call acquires the lock
        var firstResult = await _store.TryAcquireLockAsync(idempotencyKey);
        Assert.Equal(LockResultEnum.NotExists, firstResult);

        // Act - second call while lock is held (no response saved yet)
        var secondResult = await _store.TryAcquireLockAsync(idempotencyKey);

        // Assert
        Assert.Equal(LockResultEnum.Locked, secondResult);
    }

    /// <summary>
    /// (c) After SaveResponseAsync, a new call with the same key returns "already completed" (Unlocked)
    /// and GetSavedResponseAsync returns the exact saved data.
    /// </summary>
    [Fact]
    public async Task AfterSaveResponse_TryAcquireLock_ReturnsUnlocked_AndGetReturnsExactData()
    {
        // Arrange
        var idempotencyKey = $"test-key-{Guid.NewGuid()}";
        var savedResponse = new SavedResponse(
            StatusCode: 201,
            Body: """{"paymentId":"abc-123","status":"completed"}"""u8.ToArray(),
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Request-Id"] = "req-456"
            },
            PayloadHash: "ABC123HASH"
        );

        // Acquire lock first
        var lockResult = await _store.TryAcquireLockAsync(idempotencyKey);
        Assert.Equal(LockResultEnum.NotExists, lockResult);

        // Save the response
        await _store.SaveResponseAsync(idempotencyKey, savedResponse);

        // Act - try to acquire lock again (should see completed response)
        var secondLockResult = await _store.TryAcquireLockAsync(idempotencyKey);

        // Assert - should return Unlocked (already completed)
        Assert.Equal(LockResultEnum.Unlocked, secondLockResult);

        // Act - get the saved response
        var retrievedResponse = await _store.GetSavedResponseAsync(idempotencyKey);

        // Assert - exact data match
        Assert.NotNull(retrievedResponse);
        Assert.Equal(savedResponse.StatusCode, retrievedResponse.StatusCode);
        Assert.Equal(savedResponse.Body, retrievedResponse.Body);
        Assert.Equal(savedResponse.Headers["Content-Type"], retrievedResponse.Headers["Content-Type"]);
        Assert.Equal(savedResponse.Headers["X-Request-Id"], retrievedResponse.Headers["X-Request-Id"]);
        Assert.Equal(savedResponse.PayloadHash, retrievedResponse.PayloadHash);
    }

    /// <summary>
    /// (d) The lock key no longer exists in Redis after SaveResponseAsync completes.
    /// </summary>
    [Fact]
    public async Task SaveResponseAsync_RemovesLockKey()
    {
        // Arrange
        var idempotencyKey = $"test-key-{Guid.NewGuid()}";
        var lockKey = $"onceonly:lock:{idempotencyKey}";
        var responseKey = $"onceonly:response:{idempotencyKey}";

        var savedResponse = new SavedResponse(
            StatusCode: 200,
            Body: "OK"u8.ToArray(),
            Headers: new Dictionary<string, string>(),
            PayloadHash: null
        );

        // Acquire lock
        var lockResult = await _store.TryAcquireLockAsync(idempotencyKey);
        Assert.Equal(LockResultEnum.NotExists, lockResult);

        // Verify lock key exists before save
        Assert.True(await _database.KeyExistsAsync(lockKey), "Lock key should exist before save");

        // Act
        await _store.SaveResponseAsync(idempotencyKey, savedResponse);

        // Assert
        Assert.False(await _database.KeyExistsAsync(lockKey), "Lock key should NOT exist after save");
        Assert.True(await _database.KeyExistsAsync(responseKey), "Response key should exist after save");
    }

    /// <summary>
    /// Tests that GetSavedResponseAsync returns null when no response has been saved.
    /// </summary>
    [Fact]
    public async Task GetSavedResponseAsync_NoResponse_ReturnsNull()
    {
        // Arrange
        var idempotencyKey = $"nonexistent-key-{Guid.NewGuid()}";

        // Act
        var result = await _store.GetSavedResponseAsync(idempotencyKey);

        // Assert
        Assert.Null(result);
    }

    /// <summary>
    /// Tests atomic lock acquisition under concurrent access.
    /// Multiple parallel attempts to acquire the same lock should result in exactly
    /// one NotExists and the rest Locked.
    /// </summary>
    [Fact]
    public async Task TryAcquireLockAsync_ConcurrentAttempts_OnlyOneSucceeds()
    {
        // Arrange
        var idempotencyKey = $"concurrent-test-{Guid.NewGuid()}";
        const int concurrentAttempts = 10;

        // Act - fire multiple lock attempts concurrently
        var tasks = Enumerable.Range(0, concurrentAttempts)
            .Select(_ => _store.TryAcquireLockAsync(idempotencyKey))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assert - exactly one should succeed (NotExists), rest should be Locked
        var notExistsCount = results.Count(r => r == LockResultEnum.NotExists);
        var lockedCount = results.Count(r => r == LockResultEnum.Locked);

        Assert.Equal(1, notExistsCount);
        Assert.Equal(concurrentAttempts - 1, lockedCount);
    }

    /// <summary>
    /// Tests that the lock key has the correct TTL from options.
    /// </summary>
    [Fact]
    public async Task TryAcquireLockAsync_LockKeyHasCorrectTtl()
    {
        // Arrange
        var idempotencyKey = $"ttl-test-{Guid.NewGuid()}";
        var lockKey = $"onceonly:lock:{idempotencyKey}";

        // Act
        await _store.TryAcquireLockAsync(idempotencyKey);

        // Assert
        var ttl = await _database.KeyTimeToLiveAsync(lockKey);
        Assert.NotNull(ttl);

        // TTL should be close to LockTtl (30 seconds), allowing for some execution time
        Assert.True(ttl.Value.TotalSeconds > 25 && ttl.Value.TotalSeconds <= 30,
            $"Lock TTL should be close to 30 seconds, but was {ttl.Value.TotalSeconds}s");
    }

    /// <summary>
    /// Tests that the response key has the correct TTL from options.
    /// </summary>
    [Fact]
    public async Task SaveResponseAsync_ResponseKeyHasCorrectTtl()
    {
        // Arrange
        var idempotencyKey = $"response-ttl-test-{Guid.NewGuid()}";
        var responseKey = $"onceonly:response:{idempotencyKey}";

        var savedResponse = new SavedResponse(
            StatusCode: 200,
            Body: "test"u8.ToArray(),
            Headers: new Dictionary<string, string>(),
            PayloadHash: null
        );

        await _store.TryAcquireLockAsync(idempotencyKey);

        // Act
        await _store.SaveResponseAsync(idempotencyKey, savedResponse);

        // Assert
        var ttl = await _database.KeyTimeToLiveAsync(responseKey);
        Assert.NotNull(ttl);

        // TTL should be close to SavedResponseTtl (24 hours)
        var expectedHours = _options.SavedResponseTtl.TotalHours;
        Assert.True(ttl.Value.TotalHours > expectedHours - 1 && ttl.Value.TotalHours <= expectedHours,
            $"Response TTL should be close to {expectedHours} hours, but was {ttl.Value.TotalHours}h");
    }
}
