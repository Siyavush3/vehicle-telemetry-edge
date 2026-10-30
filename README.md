# vehicle-telemetry-edge

Selected components of **Vehicle Telemetry & Safety System**, an edge-deployed system I built for real-time vehicle classification and axle-load telemetry on mountain roads. It fuses weigh-in-motion (WIM) scales and ANPR cameras to detect overloaded vehicles at the source and reports violations to an external enforcement API.

> **About this repository.** The system was developed for a client under a non-disclosure agreement. The full codebase (ANPR integration, data parser, storage schema, client API integration, deployment) stays private. This repository contains only components I am permitted to publish, rewritten and documented as standalone modules. The client's API, schema and identifiers are anonymised.

## System overview

```
 WIM scale ─serial─► [1] Wim.Serial / Wim.Protocol ─┐
                                                   ├─► parser ─► PostgreSQL ─► sender ─► enforcement API
 ANPR cameras ─────────────────────────────────────┘                │       [2] Telemetry.Delivery
                                                                    └─ delivery log (audit, retries)
```

Published here:

| # | Component | What it shows |
|---|---|---|
| 1 | **Serial protocol driver** for the WIM scale (`src/Wim.Protocol`, `src/Wim.Serial`) | Binary framing over RS-232/485, resynchronisation, CRC-16 acknowledgements, port auto-detection, reconnects. Published in full, with a simulator. |
| 2 | **Delivery core** of the sender service (`src/Telemetry.Delivery`) | Retry/give-up policy, thread-safe token cache and the outbox-style selection query. The service around it stays private. |

Not published (under NDA): ANPR integration, the parser that merges scale and camera data, the database schema, the client API client, configuration and deployment.

## 1. Serial protocol driver: v1 → v2

[`docs/legacy/Scale_v1.cs`](docs/legacy/Scale_v1.cs) is my first version of the algorithm, as written for the field installation (published with permission). The rewrite keeps wire compatibility and fixes the problems I found in it:

| v1 | v2 |
|---|---|
| Frame end found by searching for the next `0xFF`, so a `0xFF` byte inside the payload corrupted framing | Frame boundary taken from the length byte; tested with `0xFF` inside the payload |
| `buffer[pos + 3]` read right after the start byte, before the bytes arrived | Header is parsed only when 4 bytes are buffered |
| A corrupted length byte could stall the stream forever | `SkipCurrentFrame()` resynchronises after a timeout; discarded bytes are counted as a line-quality metric |
| `NumberOfAxles = 0` threw on `new int[-1]`; bad dates crashed parsing | Payload is validated; malformed data raises `ProtocolException` and the scale gets a negative ACK |
| Polling with `Thread.Sleep` and `goto`, empty `catch {}` blocks | Async reads with cancellation, timeouts and an explicit reconnect loop with logging |
| Byte buffer rebuilt through `List<byte>` on every frame | Single growable buffer with compaction |
| CRC byte order hidden in a byte swap plus `BitConverter` | `Crc16Xmodem` with a known check value; ACK layout covered by a test |

Wire format: [`docs/FORMAT.md`](docs/FORMAT.md).

## 2. Delivery core: lessons from production

The sender polls new vehicle passes from PostgreSQL and delivers them to the external API, logging every attempt. Running the first version in production surfaced three issues, which these components fix:

| Problem in v1 | Fix |
|---|---|
| Any answer outside a fixed list of codes (unknown code, HTTP 5xx, unparsable body) left a record unfinished, so it was retried in a tight loop and re-selected on every cycle forever | [`DeliveryPolicy`](src/Telemetry.Delivery/DeliveryPolicy.cs) maps every possible answer to *Delivered / Retry / Refresh token / Give up*, backs off exponentially and enforces an attempt budget, so each record reaches a final state. Receiver codes live in configuration. |
| A filter ran in application code after `LIMIT`, so skipped records were re-selected every cycle and newer ones starved; the endpoint name was hard-coded in SQL | [`PendingDeliveriesQuery`](src/Telemetry.Delivery/PendingDeliveriesQuery.cs) filters in SQL before the limit, takes the endpoint as a parameter, honours the retry schedule and uses `FOR UPDATE SKIP LOCKED` for safe parallel senders |
| A rejected token kept failing until its cached expiry; part of the token was written to logs | [`TokenProvider`](src/Telemetry.Delivery/TokenProvider.cs) refreshes ahead of expiry, can be invalidated on HTTP 401, shares one refresh between concurrent callers and never logs the token |

## Repository structure

| Path | Contents |
|---|---|
| `src/Wim.Protocol` | Pure protocol library, no I/O: `FrameDecoder`, `MessageCodec`, `Crc16Xmodem`, message types |
| `src/Wim.Serial` | `ScaleReader`: port auto-detection, read loop, ACKs, reconnects |
| `src/Telemetry.Delivery` | `DeliveryPolicy`, `TokenProvider`, `PendingDeliveriesQuery` |
| `tools/Wim.Simulator` | Command-line tool that can act as the scale or as the host |
| `tests/` | 39 xUnit tests: framing edge cases, CRC, v1 regressions, retry decisions, backoff, token caching and concurrency |
| `docs/` | Wire format and the legacy v1 driver |

## Quick start

Requires the .NET 8 SDK.

```bash
dotnet test                                             # run all tests
dotnet run --project tools/Wim.Simulator -- hex 3       # print three simulated weighing frames
```

Without hardware, create a virtual serial-port pair (com0com on Windows, `socat` on Linux) and run:

```bash
dotnet run --project tools/Wim.Simulator -- scale COM10   # simulated scale on one end
dotnet run --project tools/Wim.Simulator -- host COM11    # host on the other end
```

## Notes

- Scale units (weight, speed, distance) are passed through exactly as the device reports them.
- Like v1, the host does not verify a checksum on incoming scale frames; integrity relies on framing and payload validation, and acknowledgements carry CRC-16/XMODEM.
- Result codes in the tests are examples, not the client's real codes.

## Contact

Siyavush Djamalov · [LinkedIn](https://www.linkedin.com/in/siyavush-robotics)
