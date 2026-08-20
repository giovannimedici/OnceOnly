using OnceOnly.Middleware.Enums;
using OnceOnly.Middleware.DTO;

namespace OnceOnly.Middleware;

public interface IIdempotencyStore
{
    Task<LockResultEnum> TryAcquireLockAsync(string idempotencyKey);
    Task<SavedResponse> GetSavedResponseAsync(string idempotencyKey);
    Task SaveResponseAsync(string idempotencyKey, SavedResponse response);
}