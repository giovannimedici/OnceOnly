using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace OnceOnly.Middleware;

/// <summary>
/// Registration helpers for the OnceOnly idempotency middleware.
/// </summary>
public static class OnceOnlyExtensions
{
    /// <summary>
    /// Registers <see cref="IdempotencyOptions"/> and the in-memory <see cref="IIdempotencyStore"/>.
    /// Replace the store registration afterwards to use Redis (or another implementation).
    /// </summary>
    public static IServiceCollection AddOnceOnly(
        this IServiceCollection services,
        Action<IdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new IdempotencyOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        return services;
    }

    /// <summary>
    /// Adds <see cref="IdempotencyMiddleware"/> to the request pipeline.
    /// Register this before endpoint mapping so keyed requests are intercepted.
    /// </summary>
    public static IApplicationBuilder UseOnceOnly(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}
