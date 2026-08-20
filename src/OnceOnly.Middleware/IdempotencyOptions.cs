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
}
