# BookYourShow — real-time ticket booking (core path)

.NET 10 implementation of the core booking path from the design doc *BookYourShow — Real-Time Ticket Booking System Design (.NET 10 on AWS)*.

| Service | Port | What it does |
| --- | --- | --- |
| Catalog | 5101 | Venues, halls (seat layouts), events, shows, prices; publishes `ShowPublished` |
| Seat Inventory | 5102 | Atomic seat holds in Valkey (Lua), 5-min TTL, availability snapshots, seat deltas |
| Booking | 5103 | Booking saga: hold → booking → payment → confirm / fail / expire; reconciliation worker |
| Payment | 5104 | Gateway adapters (Fake for local, Razorpay), signed webhooks, refunds |
| Realtime | 5105 | SignalR hub pushing coalesced seat changes; hosts the demo seat-map page |

## How double booking is prevented

1. **Hold (Valkey):** `Scripts/hold.lua` checks and claims all requested seats in one atomic script — all or nothing. Every key of a show shares the `{showId}` hash tag, so the script stays atomic on a cluster.
2. **Sale (PostgreSQL):** `booking_seat` has `PRIMARY KEY (show_id, seat_id)`. Confirmation inserts the seats (in seat order, so racing transactions cannot deadlock). If any seat is already sold, the booking fails and the payment is refunded.
3. **Timeouts:** holds expire after 5 min (extended to 10 min once payment starts). Expiry is enforced lazily by the hold script, by the hold's TTL, and by a delayed SQS message (`HoldExpiryCheck`) that releases the seats and tells Booking.

Everything that crosses services goes through a **transactional outbox** → SNS → per-service SQS queues (with DLQs). Consumers are idempotent. Mutating APIs require an `Idempotency-Key` header.

## Run it locally

Prerequisites: Docker Desktop, .NET 10 SDK (only for running tests / services outside Docker).

```bash
docker compose up --build
./scripts/seed.sh          # Windows: ./scripts/seed.ps1   -> prints a showId
```

Open http://localhost:5105, paste the show id, and open a second window with another user id. Select seats → **Hold** → **Create booking** → **Pay (simulate success)**. The other window sees seats turn held, then sold, live.

Things to try:

- Two windows hold the same seat: the second gets `409 SEAT_HELD`.
- **Pay (simulate failure)**: the booking fails and the seats are released at once.
- Hold seats and wait 5 minutes: the hold expires and the seats reappear in both windows.
- Lost webhook: `POST http://localhost:5104/dev/payments/{paymentId}/simulate?dropWebhook=true`; the reconciliation worker in Booking confirms the booking within ~2.5 minutes.

LocalStack 4.4 is pinned. If your LocalStack version asks for an auth token, set `LOCALSTACK_AUTH_TOKEN` before `docker compose up`.

## API (dev auth)

In Development, pass `X-User-Id: <any id>` instead of a JWT (admins also send `X-User-Role: admin`). Outside Development, services validate Cognito JWTs (`Auth:Authority`).

```http
POST /shows/{showId}/holds          (5102)  Idempotency-Key, {"seatIds":["A1","A2"]}
GET  /shows/{showId}/availability   (5102)
POST /bookings                      (5103)  Idempotency-Key, {"showId":"...","holdId":"..."}
GET  /bookings/{id}                 (5103)
POST /bookings/{id}/cancel          (5103)  Idempotency-Key
POST /webhooks/payments/{gateway}   (5104)  signed by the gateway
```

OpenAPI documents are at `/openapi/v1.json` on each service.

## Tests

```bash
dotnet test        # needs Docker: Testcontainers starts Valkey and PostgreSQL
```

- `SeatInventory.Tests` — runs the real Lua scripts: all-or-nothing holds, expiry, extension, and 2,000 concurrent users racing for 100 seats with no seat held twice.
- `Booking.Tests` — 50 paid bookings for the same seats confirm exactly one; duplicates, late payments and wrong amounts.

## Layout

```text
src/BuildingBlocks/BookYourShow.Contracts       events and DTOs shared between services
src/BuildingBlocks/BookYourShow.BuildingBlocks  outbox, inbox, idempotency, SNS/SQS, service defaults (OTel, auth)
src/Services/*                                  one deployable per service (Minimal APIs, Dapper, Npgsql)
db/init                                         database schemas (one database per service)
infra/localstack                                SNS topic, SQS queues, DLQs, filter policies
tests/                                          xUnit + Testcontainers
```

## What maps to AWS

| Local | AWS |
| --- | --- |
| PostgreSQL 17 | Aurora PostgreSQL (separate clusters for Booking and Payment) via RDS Proxy |
| Valkey 8 | ElastiCache for Valkey (cluster mode; separate cluster for the SignalR backplane) |
| LocalStack SNS/SQS | Amazon SNS + SQS (FIFO for payment events) |
| docker compose | ECS Fargate behind API Gateway / ALB; payment webhooks via Lambda → SQS |
| `X-User-Id` header | Amazon Cognito JWT |
| OTel collector | ADOT → X-Ray, CloudWatch, Managed Prometheus |

## Not in this pass

Search (OpenSearch), Waiting Room, Notification, Identity profile service, Audit/Firehose, AWS CDK stacks, and the degraded-mode fallback when Valkey is down. The contracts and events they need already exist.
