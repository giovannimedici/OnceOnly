using Microsoft.AspNetCore.Http;
using OnceOnly.Middleware.DTO;
using OnceOnly.Middleware.Enums;

namespace OnceOnly.Middleware;

/// <summary>
/// Idempotency middleware that prevents duplicate processing of HTTP requests.
/// </summary>
/// <remarks>
/// <para>
/// This middleware captures and stores complete HTTP responses (status, headers, body)
/// for requests that include an "Idempotency-Key" header. Subsequent requests
/// with the same key receive the stored response without reprocessing.
/// </para>
/// <para>
/// IMPORTANT: This middleware stores the complete response in memory (MemoryStream)
/// before sending it to the client. This is acceptable for typical JSON API payloads,
/// but is NOT recommended for endpoints that return large files or voluminous
/// data streams, as it may cause high memory consumption.
/// </para>
/// </remarks>
public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IIdempotencyStore _store;
    private readonly IdempotencyOptions _options;

    /// <summary>
    /// Initializes a new instance of the idempotency middleware.
    /// </summary>
    /// <param name="next">The next delegate in the request pipeline.</param>
    /// <param name="store">Idempotency store implementation for persisting keys and responses.</param>
    /// <param name="options">Middleware configuration options.</param>
    public IdempotencyMiddleware(
        RequestDelegate next,
        IIdempotencyStore store,
        IdempotencyOptions options)
    {
        _next = next;
        _store = store;
        _options = options;
    }

    /// <summary>
    /// Processes the HTTP request, applying idempotency logic if applicable.
    /// </summary>
    /// <param name="context">The HTTP context of the current request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        // If there's no idempotency key in the header, pass through without special processing
        if (!context.Request.Headers.TryGetValue(_options.IdempotencyKeyHeader, out var idempotencyKey)
            || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _next(context);
            return;
        }

        var key = idempotencyKey.ToString();

        // Try to acquire a lock for this key
        var lockResult = await _store.TryAcquireLockAsync(key);

        switch (lockResult)
        {
            case LockResultEnum.Locked:
                // CASE: Key is already being processed by another concurrent request.
                // Return 409 Conflict to indicate that the request is still in progress.
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsync("Request with this idempotency key is still being processed.");
                return;

            case LockResultEnum.Unlocked:
                // FLOW 2 — Key was already processed previously, replay the saved response.
                await ReplayStoredResponseAsync(context, key);
                return;

            case LockResultEnum.NotExists:
                // FLOW 1 — First time seeing this key, process and capture the response.
                await ProcessAndCaptureResponseAsync(context, key);
                return;

            default:
                throw new InvalidOperationException($"Unrecognized lock result: {lockResult}");
        }
    }

    /// <summary>
    /// FLOW 1: Processes the request normally (calls next()), captures the complete
    /// response and saves it to the store for future replays.
    /// </summary>
    /// <remarks>
    /// This method temporarily replaces Response.Body with a MemoryStream to
    /// capture the content written by the endpoint. After processing, the data is
    /// copied back to the original stream so the client receives the response.
    /// </remarks>
    private async Task ProcessAndCaptureResponseAsync(HttpContext context, string idempotencyKey)
    {
        // Keep reference to the original response stream (typically a network socket)
        var originalBodyStream = context.Response.Body;

        // Create a temporary MemoryStream that will be used by the endpoint to write the response
        using var captureStream = new MemoryStream();

        try
        {
            // Replace Response.Body with our temporary stream.
            // From here on, any write made by the endpoint will go to captureStream.
            context.Response.Body = captureStream;

            // Call the next middleware/endpoint in the pipeline.
            // The endpoint executes normally, unaware that it's writing to a buffer.
            await _next(context);

            // If we got here without an exception, the endpoint finished successfully.
            // Now we'll capture the response data to persist.

            // Reposition the stream to the beginning to read all content
            captureStream.Position = 0;

            // Read all response body content as a byte array
            var bodyBytes = captureStream.ToArray();

            // Capture the configured headers for persistence
            var headersToSave = new Dictionary<string, string>();
            foreach (var headerName in _options.HeadersToPersist)
            {
                if (context.Response.Headers.TryGetValue(headerName, out var headerValue))
                {
                    headersToSave[headerName] = headerValue.ToString();
                }
            }

            // Create the saved response object with all captured data
            var savedResponse = new SavedResponse(
                StatusCode: context.Response.StatusCode,
                Body: bodyBytes,
                Headers: headersToSave
            );

            // Persist the response in the store for future replays
            await _store.SaveResponseAsync(idempotencyKey, savedResponse);

            // Copy the captured content back to the original stream,
            // so the client actually receives the response.
            captureStream.Position = 0;
            await captureStream.CopyToAsync(originalBodyStream);
        }
        finally
        {
            // CRITICAL: Restore the original stream even in case of exception.
            // This ensures ASP.NET Core maintains the correct response state.
            // Without this, subsequent exceptions may try to write to the
            // already-disposed MemoryStream, causing undefined behavior.
            context.Response.Body = originalBodyStream;

            // Note: If an exception occurred during next(), we DON'T save the response
            // (SaveResponseAsync above was not reached). The exception propagates
            // normally through the pipeline and will be handled by global error handlers.
        }
    }

    /// <summary>
    /// FLOW 2: Retrieves a previously saved response from the store and resends it to the client,
    /// without calling next() (the real endpoint is never invoked).
    /// </summary>
    private async Task ReplayStoredResponseAsync(HttpContext context, string idempotencyKey)
    {
        // Retrieve the saved response from the store
        var savedResponse = await _store.GetSavedResponseAsync(idempotencyKey);

        if (savedResponse == null)
        {
            // Unexpected case: the lock indicated there was a response, but it wasn't found.
            // Can happen if the response expired between the lock check and the get, or due to store inconsistency.
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Error retrieving stored response.");
            return;
        }

        // Set the status code from the stored response
        context.Response.StatusCode = savedResponse.StatusCode;

        // Reapply all persisted headers
        foreach (var (headerName, headerValue) in savedResponse.Headers)
        {
            context.Response.Headers[headerName] = headerValue;
        }

        // Write the original response body directly to the output stream
        await context.Response.Body.WriteAsync(savedResponse.Body);

        // Return without calling next() — the real endpoint is never executed in this flow
    }
}
