using System.Text.Json;
using OnceOnly.Middleware.DTO;
using OnceOnly.Middleware.Enums;
using StackExchange.Redis;

namespace OnceOnly.Middleware;

/// <summary>
/// Redis-backed <see cref="IIdempotencyStore"/> using StackExchange.Redis.
/// Suitable for distributed deployments where multiple instances share idempotency state.
/// </summary>
/// <remarks>
/// <para>
/// Key naming convention:
/// - Lock key: <c>onceonly:lock:{idempotencyKey}</c> — stores the payload hash during processing
/// - Response key: <c>onceonly:response:{idempotencyKey}</c> — stores the serialized SavedResponse after completion
/// </para>
/// <para>
/// The lock is acquired atomically using <c>SET key value NX EX seconds</c>.
/// Once the response is saved, the lock key is removed (the response key becomes authoritative).
/// </para>
/// </remarks>
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private const string LockKeyPrefix = "onceonly:lock:";
    private const string ResponseKeyPrefix = "onceonly:response:";

    private readonly IDatabase _database;
    private readonly IdempotencyOptions _options;

    /// <summary>
    /// Initializes a new instance of the Redis idempotency store.
    /// </summary>
    /// <param name="connectionMultiplexer">
    /// The Redis connection multiplexer. The consuming application is responsible for
    /// managing the connection lifecycle (standard practice for StackExchange.Redis in ASP.NET Core).
    /// </param>
    /// <param name="options">Idempotency configuration options (TTLs, etc.).</param>
    public RedisIdempotencyStore(IConnectionMultiplexer connectionMultiplexer, IdempotencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        ArgumentNullException.ThrowIfNull(options);

        _database = connectionMultiplexer.GetDatabase();
        _options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses <c>SET key value NX EX seconds</c> for atomic lock acquisition.
    /// The value stored is a placeholder ("1") since the payload hash is validated separately
    /// during response replay. The lock key exists only while processing is in progress.
    /// </remarks>
    public async Task<LockResultEnum> TryAcquireLockAsync(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var lockKey = GetLockKey(idempotencyKey);
        var responseKey = GetResponseKey(idempotencyKey);

        // First, check if a completed response already exists.
        // If it does, we return Unlocked immediately (no lock acquisition needed).
        if (await _database.KeyExistsAsync(responseKey))
        {
            return LockResultEnum.Unlocked;
        }

        // Attempt atomic lock acquisition: SET lockKey "1" NX EX lockTtlSeconds
        // This single Redis command determines the outcome — no separate check-then-set.
        var lockAcquired = await _database.StringSetAsync(
            lockKey,
            "1",
            _options.LockTtl,
            When.NotExists,
            CommandFlags.None);

        if (lockAcquired)
        {
            return LockResultEnum.NotExists;
        }

        // Lock acquisition failed — either someone else holds the lock (in progress)
        // or a response was saved between our KeyExists check and StringSet.
        // Double-check for a completed response to distinguish the two cases.
        if (await _database.KeyExistsAsync(responseKey))
        {
            return LockResultEnum.Unlocked;
        }

        // Response still doesn't exist, so another request is holding the lock.
        return LockResultEnum.Locked;
    }

    /// <inheritdoc />
    public async Task<SavedResponse> GetSavedResponseAsync(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var responseKey = GetResponseKey(idempotencyKey);
        var data = await _database.StringGetAsync(responseKey);

        if (data.IsNullOrEmpty)
        {
            return null!;
        }

        return JsonSerializer.Deserialize<SavedResponse>(data.ToString())!;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Serializes the response using System.Text.Json, stores it with the configured
    /// SavedResponseTtl, and removes the lock key since it's no longer needed.
    /// </remarks>
    public async Task SaveResponseAsync(string idempotencyKey, SavedResponse response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(response);

        var responseKey = GetResponseKey(idempotencyKey);
        var lockKey = GetLockKey(idempotencyKey);

        var serialized = JsonSerializer.Serialize(response);

        // Save the completed response with its own TTL.
        await _database.StringSetAsync(responseKey, serialized, _options.SavedResponseTtl);

        // Remove the lock key — the response key is now authoritative.
        await _database.KeyDeleteAsync(lockKey);
    }

    private static string GetLockKey(string idempotencyKey) => $"{LockKeyPrefix}{idempotencyKey}";

    private static string GetResponseKey(string idempotencyKey) => $"{ResponseKeyPrefix}{idempotencyKey}";
}
