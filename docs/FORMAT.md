# Wire format

Serial line: 9600 baud, 8 data bits, no parity, 1 stop bit (8N1). Multi-byte integers are big-endian.

## Frame (scale → host)

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 1 | Start byte | Always `0xFF` |
| 1 | 1 | Address | Device address, `0x00` on single-scale lines |
| 2 | 1 | Command | See below |
| 3 | 1 | Length | **Total** frame length in bytes, including the start byte (min 4, max 255) |
| 4 | Length − 4 | Payload | Command specific |

`0xFF` can also appear inside the payload, so a frame ends where its length byte says, not at the next `0xFF`.

## Commands

| Code | Name | Payload |
|---|---|---|
| `1` | Weighing | One vehicle pass, see below. Must be acknowledged. |
| `5` | Sensor status | 1 byte, fault bitmask |
| `6` | Keepalive | Ignored by the host |
| `9` | Aux status | 1 byte, device specific |

### Weighing payload (command 1)

| Payload offset | Size | Field |
|---|---|---|
| 0 | 2 | Year |
| 2 | 1 | Month |
| 3 | 1 | Day |
| 4 | 1 | Hour |
| 5 | 1 | Minute |
| 6 | 1 | Second |
| 7 | 1 | Sensor status (same bits as command 5) |
| 8 | 2 | Speed × 10 |
| 10 | 1 | Acceleration (raw) |
| 11 | 1 | Number of axles *N* (≥ 1) |
| 12 | 1 | Number of axle groups *G* |
| 13 | 2 × N | Axle weights |
| … | 2 × G | Axle-group weights |
| … | G | Axle-group types |
| … | 2 × (N − 1) | Distances between axles (optional, only if enabled on the device) |

### Sensor status bits

A set bit means the sensor reports a fault.

| Bit | Sensor |
|---|---|
| 0 | Weigh sensor |
| 1 | Infrared |
| 2 | Induction loop |
| 3 | Tire sensor |
| 4 | Communication |

The host uses `0xFF` as a synthetic "scale offline" status when the port is unavailable.

## Acknowledgement (host → scale)

Sent after every weighing frame.

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | `0xFF` |
| 1 | 1 | `0x00` |
| 2 | 1 | Command being acknowledged (`1`) |
| 3 | 1 | Result: `0` accepted, `1` rejected (scale resends) |
| 4 | 2 | CRC-16/XMODEM of bytes 0–3, high byte first |

CRC-16/XMODEM: polynomial `0x1021`, initial value `0x0000`, no reflection, no final XOR (check value for `"123456789"` is `0x31C3`).
