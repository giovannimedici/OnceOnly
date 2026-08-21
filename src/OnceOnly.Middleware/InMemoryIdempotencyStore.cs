using System.Collections.Concurrent;
using OnceOnly.Middleware.DTO;
using OnceOnly.Middleware.Enums;

namespace OnceOnly.Middleware;

/// <summary>
/// In-memory <see cref="IIdempotencyStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Suitable for a single process (local demos and tests). Use a distributed store such as Redis
/// when running multiple instances.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly IdempotencyOptions _options;

    public InMemoryIdempotencyStore(IdempotencyOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public Task<LockResultEnum> TryAcquireLockAsync(string idempotencyKey)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = _entries.GetOrAdd(idempotencyKey, static _ => new Entry());

        lock (entry)
        {
            if (HasLiveResponse(entry, now))
            {
                return Task.FromResult(LockResultEnum.Unlocked);
            }

            if (HasLiveLock(entry, now))
            {
                return Task.FromResult(LockResultEnum.Locked);
            }

            entry.Response = null;
            entry.ResponseExpiresAt = default;
            entry.LockExpiresAt = now.Add(_options.LockTtl);
            return Task.FromResult(LockResultEnum.NotExists);
        }
    }

    /// <inheritdoc />
    public Task<SavedResponse> GetSavedResponseAsync(string idempotencyKey)
    {
        if (!_entries.TryGetValue(idempotencyKey, out var entry))
        {
            return Task.FromResult<SavedResponse>(null!);
        }

        lock (entry)
        {
            if (!HasLiveResponse(entry, DateTimeOffset.UtcNow))
            {
                return Task.FromResult<SavedResponse>(null!);
            }

            return Task.FromResult(entry.Response!);
        }
    }

    /// <inheritdoc />
    public Task SaveResponseAsync(string idempotencyKey, SavedResponse response)
    {
        var entry = _entries.GetOrAdd(idempotencyKey, static _ => new Entry());

        lock (entry)
        {
            entry.Response = response;
            entry.ResponseExpiresAt = DateTimeOffset.UtcNow.Add(_options.SavedResponseTtl);
            entry.LockExpiresAt = default;
        }

        return Task.CompletedTask;
    }

    private static bool HasLiveResponse(Entry entry, DateTimeOffset now) =>
        entry.Response is not null && entry.ResponseExpiresAt > now;

    private static bool HasLiveLock(Entry entry, DateTimeOffset now) =>
        entry.Response is null && entry.LockExpiresAt > now;

    private sealed class Entry
    {
        public SavedResponse? Response { get; set; }
        public DateTimeOffset LockExpiresAt { get; set; }
        public DateTimeOffset ResponseExpiresAt { get; set; }
    }
}
