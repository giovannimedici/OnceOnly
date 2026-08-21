namespace OnceOnly.Middleware;

/// <summary>
/// Configuration options for the idempotency middleware.
/// </summary>
public class IdempotencyOptions
{
    /// <summary>
    /// List of HTTP header names that should be persisted along with the response
    /// and reapplied during replay. Defaults to "Content-Type".
    /// </summary>
    /// <remarks>
    /// Add other headers as needed (e.g., "Cache-Control", "ETag", etc.).
    /// Note: headers like "Content-Length" are managed automatically by ASP.NET
    /// Core and do not need to be added here.
    /// </remarks>
    public List<string> HeadersToPersist { get; set; } = new() { "Content-Type" };

    /// <summary>
    /// Name of the HTTP header that contains the idempotency key.
    /// Default: "Idempotency-Key".
    /// </summary>
    public string IdempotencyKeyHeader { get; set; } = "Idempotency-Key";

    /// <summary>
    /// How long an in-progress lock is held for a given idempotency key.
    /// Concurrent retries that arrive while the original request is still
    /// processing receive 409 Conflict until this TTL expires.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan LockTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a completed response is retained and replayed for the same
    /// idempotency key. Default: 24 hours.
    /// </summary>
    public TimeSpan SavedResponseTtl { get; set; } = TimeSpan.FromHours(24);
}
