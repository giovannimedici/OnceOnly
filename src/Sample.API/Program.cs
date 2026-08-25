// Demo project for the OnceOnly idempotency middleware.
// See the repository README for curl scenarios and configuration: ../../README.md

using OnceOnly.Middleware;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Requires a running Redis instance.
var redis = ConnectionMultiplexer.Connect("localhost:6379");
builder.Services.AddOnceOnly(redis, options =>
{
    options.LockTtl = TimeSpan.FromSeconds(30);
    options.SavedResponseTtl = TimeSpan.FromHours(24);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseOnceOnly();

app.MapPost("/payments", async (PaymentRequest request) =>
{
    // Simulated processing time so concurrent retries can observe 409 Conflict.
    // 10s leaves enough time to Send a second request from Postman.
    await Task.Delay(TimeSpan.FromSeconds(10));

    var payment = new PaymentResponse(
        PaymentId: Guid.NewGuid(),
        Amount: request.Amount,
        Currency: request.Currency,
        Status: "completed");

    return Results.Created($"/payments/{payment.PaymentId}", payment);
});

app.Run();

internal record PaymentRequest(decimal Amount, string Currency);

internal record PaymentResponse(Guid PaymentId, decimal Amount, string Currency, string Status);
