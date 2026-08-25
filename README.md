# OnceOnly.Net

A lightweight idempotency middleware for ASP.NET Core, preventing duplicate processing of retried requests.

## The Problem

When a client sends a request that changes state — creating a payment, placing an order, sending an email — network failures can make the outcome ambiguous. The connection might drop *after* the server processed the request but *before* the response reached the client. From the client's perspective, it looks like the request failed, so the natural reaction is to retry.

Without protection, that retry creates a duplicate: a second payment is charged, a second order is placed, a second email is sent. This is a well-known problem in distributed systems, and the standard solution is **idempotency keys**: the client generates a unique key (typically a UUID) and sends it on every attempt of the same logical operation via an `Idempotency-Key` header. The server uses that key to detect retries and return the original result instead of reprocessing.

OnceOnly.Net implements this pattern as a drop-in ASP.NET Core middleware, so any API can add idempotency support without changing business logic.

## How It Works

```
Client Request (with Idempotency-Key header)
            │
            ▼
   ┌──────────────────┐
   │ IdempotencyMiddleware │
   └──────────────────┘
            │
            ▼
   Does this key already exist in the store?
            │
   ┌────────┼─────────────────┐
   │        │                 │
  No     Yes, still       Yes, already
         processing        finished
   │        │                 │
   ▼        ▼                 ▼
Process   Return 409       Return the
request   Conflict         saved response
   │                        (no reprocessing)
   ▼
Save response
in the store
```

In short: the first request with a given key is processed normally, and its result is stored. Any subsequent request carrying the same key — whether it arrives while the first one is still running, or after it has completed — is intercepted by the middleware and never reaches the actual endpoint logic again.

If no `Idempotency-Key` header is present, the middleware simply passes the request through. Idempotency protection is opt-in per request, not enforced globally — this keeps the middleware safe to add to existing APIs without breaking read-only or non-critical endpoints.

## Design Decisions

This section documents the reasoning behind a few choices that aren't obvious from the code alone.

**Three states, not a boolean.** A naive implementation might check "does this key exist?" with a simple true/false. In practice, a key can be in three distinct states: *unseen* (first attempt, safe to process), *in progress* (a previous attempt hasn't finished — likely a concurrent retry, should be rejected with `409 Conflict`), and *completed* (a previous attempt finished — safe to replay its saved response). Collapsing these into a boolean would either cause duplicate processing under concurrency or force retries to fail unnecessarily while the original request is still running.

**Locking before processing.** To avoid a race condition where two concurrent requests with the same key both pass the "key doesn't exist yet" check, the store acquires a short-lived lock atomically before the request is processed. This lock is separate from the final saved response and has its own, shorter expiration — it represents "someone is working on this" rather than "here is the final answer."

**Storing the response as raw bytes with headers, not just a JSON string.** Assuming every response is JSON is a simplifying shortcut that breaks for other content types. Storing the full response — status code, headers, and body as bytes — makes the middleware correct regardless of what the wrapped endpoint returns.

**Validating the request payload against the key.** An idempotency key is only meaningful if it's tied to a specific request payload. If a client reuses the same key with a *different* body, that's a client error, not a legitimate retry — the middleware detects this (via a hash of the payload) and rejects the request instead of silently returning a mismatched cached response.

**Storage as an abstraction, not a fixed dependency.** The core middleware depends on an `IIdempotencyStore` interface, not on Redis directly. This keeps the library flexible (an in-memory implementation is useful for testing; Redis or DynamoDB fit production use) and keeps the middleware's logic independent of any specific storage technology.

## Quick Start

A working demo lives in `[src/Sample.API](src/Sample.API)`: a `POST /payments` endpoint that sleeps for 10 seconds so the in-progress (`409`) and replay (`201`) paths are easy to observe.

```bash
dotnet run --project src/Sample.API
```

The HTTP profile listens on `http://localhost:5261`.

```bash
# 1. First request with a new Idempotency-Key → waits ~10s, returns 201
curl -i -X POST http://localhost:5261/payments \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: payment-001" \
  -d '{"amount": 100.00, "currency": "USD"}'

# 2. Same key while the first request is still processing → 409 Conflict
#    Send this as a *second, separate* HTTP request (another terminal or client)
#    within ~10 seconds of step 1. Do not concatenate two JSON objects.
#    Each request body must be exactly one object:
#    {"amount": 100.00, "currency": "USD"}
curl -i -X POST http://localhost:5261/payments \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: payment-001" \
  -d '{"amount": 100.00, "currency": "USD"}'

# Optional: two sequential curls in one shell. The trailing `&` is bash
# (run the first curl in the background); it is NOT part of the JSON body.
curl -s -o /tmp/onceonly-1.txt -w "request-1: %{http_code}\n" \
  -X POST http://localhost:5261/payments \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: payment-concurrent" \
  -d '{"amount": 100.00, "currency": "USD"}' &
sleep 0.3
curl -s -o /tmp/onceonly-2.txt -w "request-2: %{http_code}\n" \
  -X POST http://localhost:5261/payments \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: payment-concurrent" \
  -d '{"amount": 100.00, "currency": "USD"}'
wait

# 3. Same key after the first request completed → replayed 201, same paymentId, no 10s delay
curl -i -X POST http://localhost:5261/payments \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: payment-001" \
  -d '{"amount": 100.00, "currency": "USD"}'

# 4. Same key, different body → 422 Unprocessable Entity
curl -i -X POST http://localhost:5261/payments \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: payment-001" \
  -d '{"amount": 200.00, "currency": "EUR"}'

# 5. No Idempotency-Key → always processed as a new payment (201, ~10s)
curl -i -X POST http://localhost:5261/payments \
  -H "Content-Type: application/json" \
  -d '{"amount": 75.00, "currency": "BRL"}'
```



## Configuration

Register the in-memory store and options, then add the middleware before endpoints:

```csharp
builder.Services.AddOnceOnly(options =>
{
    options.LockTtl = TimeSpan.FromSeconds(30);           // default
    options.SavedResponseTtl = TimeSpan.FromHours(24);    // default
});

var app = builder.Build();
app.UseOnceOnly();
```

`AddOnceOnly` uses `InMemoryIdempotencyStore` by default. To switch to Redis once `RedisIdempotencyStore` is implemented:

```csharp
builder.Services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
```



## Running Locally / Contributing

```bash
dotnet test
dotnet run --project src/Sample.API
```

