namespace OnceOnly.Middleware.DTO;

/// <summary>
/// Represents a saved HTTP response that can be replayed later.
/// </summary>
/// <param name="StatusCode">HTTP status code from the original response.</param>
/// <param name="Body">Response body as a byte array.</param>
/// <param name="Headers">Dictionary of HTTP headers to be reapplied during replay.</param>
/// <param name="PayloadHash">SHA-256 hash of the original request body, used to reject key reuse with a different payload.</param>
public record SavedResponse(
    int StatusCode,
    byte[] Body,
    Dictionary<string, string> Headers,
    string? PayloadHash = null);