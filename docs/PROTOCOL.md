# AirLens Protocol v1

A framed binary protocol over one TCP connection. The phone is the **server** and the PC is
the **client**. All integers are **big-endian**. Both implementations are tested against the
golden byte vectors at the bottom of this file:

- `android/app/src/main/java/net/silow/airlens/net/Protocol.kt`
- `windows/src/AirLens.Core/Protocol/`

## Packet header (20 bytes)

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 1 | `type` | See packet types |
| 1 | 1 | `flags` | Per-type bit field |
| 2 | 2 | `reserved` | Must be 0, receivers ignore it |
| 4 | 4 | `payloadLength` | u32, ≤ 16 MiB (`MAX_PAYLOAD`). A larger value is a protocol error: close the connection |
| 8 | 4 | `sequence` | u32, wraps. Frames: send sequence per session |
| 12 | 8 | `timestampUs` | i64. Frames: encoder PTS (sender monotonic clock, µs). Ping/Pong: echo field |

The payload (`payloadLength` bytes) follows immediately.

## Handshake

1. The client connects and sends `HELLO` within 2 s, or the server closes the socket.
2. The server validates the magic and version, then **preempts any existing session**.
3. The server sends `STREAM_INFO`, then the cached `CODEC_CONFIG` (if the encoder is running), then
   frames starting from the **next keyframe** (it requests one immediately).
4. The client sends `PING` every 500 ms. The server closes the session after 3 s without any client packet.
   The server sends `HEARTBEAT` after 1 s without frames (camera restarting), so the client's
   3 s read timeout only fires on a real link failure.

## Packet types

### Server → client

| Type | Name | Payload |
|---|---|---|
| `0x01` | `STREAM_INFO` | 20 bytes, see below. Sent at session start and whenever the encoder is reconfigured |
| `0x02` | `VIDEO_FRAME` | One Annex-B access unit (start codes included) |
| `0x03` | `PONG` | 16 bytes: i64 server time (µs) when the PING was read, i64 server time when the PONG was written. Header `timestampUs` echoes the PING. RTT = `now − echo − (sent − received)` |
| `0x04` | `HEARTBEAT` | Empty |
| `0x05` | `DEVICE_STATE` | UTF-8 JSON, at most 64 KiB, see below. Sent after `STREAM_INFO` at session start, and again whenever the lens, the quality, the lists of either, the focus or the heat level change (and when the temperature moves by 0.5 °C) |

`VIDEO_FRAME` flags:

| Bit | Name | Meaning |
|---|---|---|
| `0x01` | `KEYFRAME` | IDR. With `PREPEND_HEADER`, it carries SPS/PPS (VPS for HEVC) |
| `0x02` | `CODEC_CONFIG` | Payload is parameter sets only, no picture |
| `0x04` | `DISCONTINUITY` | The sender dropped frames before this one. Expect a keyframe |

`STREAM_INFO` payload (20 bytes):

| Offset | Size | Field |
|---:|---:|---|
| 0 | 1 | `codec` (1 = H.264, 2 = H.265) |
| 1 | 1 | `flags` (bit0 = full-range, bit1 = receiver must mirror horizontally; direct mode only, because GL mode mirrors on the phone) |
| 2 | 2 | `width` |
| 4 | 2 | `height` |
| 6 | 2 | `fps` |
| 8 | 4 | `bitrateBps` |
| 12 | 2 | `rotationDegrees`: clockwise rotation the **receiver** must still apply (non-zero only in direct mode) |
| 14 | 2 | reserved |
| 16 | 4 | `epoch`: increments on every encoder (re)start. When it changes, the receiver flushes its decoder |

### Client → server

| Type | Name | Payload |
|---|---|---|
| `0x80` | `HELLO` | 8 bytes: magic `0x414C4E53` ("ALNS"), u16 `version` (= 1), u16 `capabilities` (bit0 HEVC, bit1 4K) |
| `0x81` | `REQUEST_KEYFRAME` | Empty. The server rate-limits it to 4 per second |
| `0x82` | `PING` | Empty. Header `timestampUs` = client clock (µs) |
| `0x83` | `RECEIVER_REPORT` | 12 bytes: u32 `framesDecoded`, u32 `framesDropped`, u16 `decodeBacklogMs`, u16 reserved |
| `0x84` | `CONTROL` | UTF-8 JSON, at most 4 KiB, see below. Asks the phone to change lens, quality or focus, or tells it the virtual camera is on |

Unknown types are skipped (the length is known), so newer peers stay compatible. `DEVICE_STATE`
and `CONTROL` were added this way: an older phone ignores `CONTROL`, and an older PC ignores
`DEVICE_STATE`.

### Remote control

`DEVICE_STATE` tells the PC what it may change. Unknown fields are ignored.

```json
{
  "device": "HUAWEI P30",
  "lens": "2",
  "lenses": [
    { "key": "3", "label": "Ultra-wide", "kind": "ultra_wide" },
    { "key": "2", "label": "Main", "kind": "wide" }
  ],
  "quality": "best",
  "presets": [
    { "id": "best", "label": "4K", "tag": "Best picture", "width": 3840, "height": 2160,
      "fps": 30, "codec": "hevc", "bitrateBps": 80000000, "direct": false }
  ],
  "focus": { "supported": true, "auto": false, "value": 0.6 },
  "heat": { "tempC": 44.5, "level": "hot", "lowered": "1440p" }
}
```

| Field | Meaning |
|---|---|
| `device` | Name to show for the phone |
| `id` | The phone's stable id, the same one discovery announces (see Discovery) |
| `lens` | `key` of the active lens |
| `lenses[]` | Every lens the phone offers. `kind`: `ultra_wide`, `wide`, `telephoto`, `front` or `external` |
| `quality` | `id` of the preset that matches the running settings, or `custom` |
| `presets[]` | The quality presets for the **active lens**. `id`: `best`, `balanced` or `fast`; a lens can offer fewer (the P30's front camera has no `balanced`). `codec`: `h264` or `hevc`. `bitrateBps` is the encoder's maximum, which the preset uses |
| `focus` | Optional. `supported`: the active lens takes a manual focus distance. `auto`: autofocus is on. `value`: the fixed distance while `auto` is false, 0 (far, infinity) .. 1 (the lens's nearest), else null |
| `heat` | Optional. `tempC`: battery temperature. `level`: `normal`, `warm`, `hot` or `critical` (the phone already caps bitrate and, from `hot`, frame rate). `lowered`: the preset `label` the stream was stepped down to because of heat, else null. The phone steps down one preset after 60 s at `hot` (15 s at `critical`), at most twice, and back up after 5 minutes at `normal`. `quality` then names the preset that actually runs; the user's choice stays saved |

`CONTROL` carries one or more fields:

| Field | Effect |
|---|---|
| `lens` | Switch to this lens key. Done in place where possible, otherwise the encoder restarts |
| `quality` | Apply this preset. The encoder restarts: a new `STREAM_INFO` epoch follows within about a second |
| `focus` | A number 0 (far) .. 1 (near): fixed focus at that distance. `"auto"`: autofocus. `"lock"`: fixed focus wherever autofocus has the lens right now, for when autofocus keeps hunting. Applied in the repeating request, so the stream does not restart. Switching lens returns to autofocus |
| `virtualCamera` | `true` while the PC's virtual camera is on, so the phone can skip drawing its own preview (GL mode). The PC repeats it on every connection; a disconnect counts as `false` |

The phone answers every `CONTROL` with a `DEVICE_STATE`, also when it ignored an unknown key or
value, so the PC never waits on a request.

## Discovery (UDP)

So nobody has to type the phone's address, the PC finds phones on the local network itself. While
streaming is on, the phone listens on **UDP port 47474** (the same number as the TCP stream).

1. The PC sends a **query** as a broadcast, once per LAN adapter, to that subnet's broadcast
   address and to 255.255.255.255.
2. Every phone answers with an **announce**, unicast to the sender. The PC takes the phone's
   address from the reply's source address: whatever the phone's DHCP lease says right now.
3. Windows lets unicast replies to a broadcast through its firewall for a few seconds by default,
   so the PC needs no firewall rule.

Both datagrams start with the same 8 bytes:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | magic `0x414C4E53` ("ALNS") |
| 4 | 2 | `version` (= 1) |
| 6 | 1 | `type`: `0x01` query (PC → phone), `0x02` announce (phone → PC) |
| 7 | 1 | reserved, 0 |

A query is just those 8 bytes. An announce continues with UTF-8 JSON (unknown fields ignored):

```json
{ "id": "3f9a0c1d2b4e5f60", "device": "HUAWEI P30", "port": 47474, "busy": false }
```

| Field | Meaning |
|---|---|
| `id` | Stable per phone and app install: the first 16 hex digits of SHA-256 over the Android ID. Not the Android ID itself |
| `device` | Name to show |
| `port` | TCP port of the stream |
| `busy` | A PC is connected right now. Connecting anyway takes the stream over (see Handshake) |

`DEVICE_STATE` carries the same `id`, so a PC that met the phone over USB recognises it on Wi-Fi.

## Golden vectors

`VIDEO_FRAME`, flags `KEYFRAME`, payload 5 bytes, sequence 7, timestamp 1 000 000 µs:

```
02 01 00 00  00 00 00 05  00 00 00 07  00 00 00 00 00 0F 42 40
```

`HELLO` packet (header + payload), timestamp 0, sequence 0, capabilities 3:

```
80 00 00 00  00 00 00 08  00 00 00 00  00 00 00 00 00 00 00 00
41 4C 4E 53  00 01 00 03
```

Discovery query (UDP, 8 bytes):

```
41 4C 4E 53  00 01 01 00
```

`CONTROL` packet `{"lens":"3"}` (12-byte payload):

```
84 00 00 00  00 00 00 0C  00 00 00 00  00 00 00 00 00 00 00 00
7B 22 6C 65  6E 73 22 3A  22 33 22 7D
```
