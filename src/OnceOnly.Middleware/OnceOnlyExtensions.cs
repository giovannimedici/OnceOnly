using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace OnceOnly.Middleware;

/// <summary>
/// Registration helpers for the OnceOnly idempotency middleware.
/// </summary>
public static class OnceOnlyExtensions
{
    /// <summary>
    /// Registers <see cref="IdempotencyOptions"/> and the Redis-backed <see cref="RedisIdempotencyStore"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionMultiplexer">
    /// The Redis connection multiplexer. The consuming application is responsible for
    /// managing the connection lifecycle (standard practice for StackExchange.Redis).
    /// </param>
    /// <param name="configure">Optional configuration for idempotency options.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// var redis = ConnectionMultiplexer.Connect("localhost:6379");
    /// builder.Services.AddOnceOnly(redis, options =>
    /// {
    ///     options.LockTtl = TimeSpan.FromSeconds(30);
    ///     options.SavedResponseTtl = TimeSpan.FromHours(24);
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddOnceOnly(
        this IServiceCollection services,
        IConnectionMultiplexer connectionMultiplexer,
        Action<IdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);

        var options = new IdempotencyOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton(connectionMultiplexer);
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();

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
