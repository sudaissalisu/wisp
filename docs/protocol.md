# Wisp protocol v1

Wisp is a direct, receiver-hosted file transfer protocol over TLS 1.3. This document describes the wire format implemented by the current native core (`wisp_core`).

## Transport

- TCP, one control connection plus up to four data connections
- TLS 1.3 only, ALPN `wisp/1`
- Mutual authentication with per-install Ed25519 self-signed certificates
- Client verifies the server certificate fingerprint against the discovery/manual peer record
- Server accepts the client certificate and records its SHA-256 fingerprint
- Pairing code: six decimal digits derived from 32 bytes of `SSL_export_keying_material` with label `WISP-PAIR-1`; both endpoints display the same code
- TLS cipher suites are left to OpenSSL's secure TLS 1.3 defaults (AES-GCM/ChaCha20-Poly1305, X25519)

Peer identity:

- Certificate fingerprint = SHA-256 of the DER-encoded X.509 certificate, 64 lowercase hex characters
- Peer ID = the first 16 bytes of that digest, i.e. the first 32 hex characters
- The pairing code is user-verification only; it is not used as an encryption key

## Frame format

All integers are unsigned and big-endian. Every frame has a 20-byte header:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | Magic `0x57535031` (`WSP1`) |
| 4 | 2 | Frame type |
| 6 | 2 | Flags (currently zero) |
| 8 | 4 | Payload length |
| 12 | 8 | Transfer ID |

Payload length is capped at 8 MiB. Unknown frame types and malformed lengths terminate the connection.

### Frame types

| Value | Name | Payload |
|---:|---|---|
| 1 | `HELLO` | `u16 protocol_version`, `string name`, `string platform`, optional `string link_token` |
| 2 | `HELLO_ACK` | `u16 protocol_version`, `string name`, `string platform` |
| 3 | `OFFER` | Manifest |
| 4 | `OFFER_RESPONSE` | `u8 accepted`, `string message`, optional 32-byte data token |
| 5 | `ATTACH` | Exactly 32-byte data-channel token |
| 6 | `ATTACH_ACK` | `u8 ok`, `string error` |
| 7 | `DATA` | `u32 file_index`, `u64 offset`, 32-byte SHA-256, chunk bytes |
| 8 | `FINISH` | Empty |
| 9 | `DONE` | `u8 ok`, `string message` |
| 10 | `ERROR` | `u32 code`, `string message` |
| 11 | `CANCEL` | `string message` |
| 12 | `PING` | Empty |
| 13 | `PONG` | Empty (reserved) |

Protocol version is currently `1`. Strings are `u32 byte_length` followed by UTF-8 bytes. The decoder bounds every length.

### Temporary-link token

An Android host that shows a temporary QR link arms a 32-hex-character one-time
token and expects it in the first `HELLO`. A scanning guest appends it after the
platform string:

```text
u16 version, string name, string platform, string link_token
```

- The receiver compares the token in constant time and binds it to the first
  peer certificate fingerprint. Later connections must present the same token
  from that same peer. A missing or different token, or a replay from another
  peer identity, receives an `ERROR` frame and the connection closes.
- `HELLO_ACK` never carries a token.
- The trailing field is optional in the decoder. Older peers reject it, but only
  a guest using a QR created by the new host ever sends it; that host is new.
- The token complements certificate pinning and the six-digit pairing code; it
  does not replace either. See [qr-temporary-link.md](qr-temporary-link.md).

### Manifest

`OFFER` payload:

```text
u32 file_count
u64 total_bytes
repeat file_count:
    string relative_path   (UTF-8, '/' separators)
    u64 size
    u64 mtime
```

Limits enforced by both sides:

- 100,000 files maximum
- 2 TiB total maximum
- 4,096 bytes per relative path
- 8 MiB per frame
- relative paths are rejected if absolute, contain `..`, drive letters, NULs, trailing dots/spaces, reserved Windows device names, control bytes, or illegal characters
- duplicate paths (case-insensitive) are rejected

The receiver chooses the destination root; the manifest's relative paths are joined below it and canonicalized. The sender cannot write outside that root.

## Transfer lifecycle

### 1. Discover

The receiver announces a beacon on UDP port `47778`:

```text
WISP1|1|peer_id|percent_encoded_name|percent_encoded_platform|tcp_port|fingerprint
```

It sends this to limited broadcast, directed subnet broadcast, multicast group `239.255.42.99`, loopback, and replies directly to unicast probes. Beacons are emitted every 1.5 seconds; a peer is considered gone after 25 seconds without a sighting, and the UI applies a 30-second grace window to absorb Wi-Fi power-save bursts.

A unicast probe is:

```text
WISP-PROBE|1
```

Any Wisp receiver that receives the probe replies with a beacon to the probe's source IP and source port. Both devices run this sweep every seven seconds for local /22–/30 interfaces. It is the fallback for APs that filter broadcast/multicast.

### 2. Connect and authenticate

1. Sender opens TCP to the receiver's advertised port.
2. TLS 1.3 handshake with mutual Ed25519 certificates.
3. Sender compares the server fingerprint to the one learned from discovery or manual entry.
4. Sender sends `HELLO`; receiver sends `HELLO_ACK`.
5. Both derive and display the six-digit pairing code.
6. A new sender places no trust on the receiving peer until the user compares the code (or has previously pinned the fingerprint). A trusted device skips the dialog according to user settings.

### 3. Offer and consent

1. Sender sends `OFFER` with the manifest.
2. Receiver validates the manifest and shows the sender, code, item count, total size, and a sample of file names.
3. Receiver chooses **Accept** or **Decline**.
4. On accept, the receiver creates the destination root, allocates `.wisp-<id>.part` files, generates a random 32-byte token, and sends `OFFER_RESPONSE` with `accepted=1` and the token.
5. On decline, the sender receives `accepted=0` and stops.

The receiver's consent wait is 120 seconds. After that, the offer is rejected.

### 4. Data channels

The sender opens up to four additional TCP + TLS connections to the same receiver port. Each data connection sends exactly one `ATTACH` frame containing the token. The receiver checks the token against the pending accepted session and responds with `ATTACH_ACK`.

The token is a bearer credential for that transfer only. It is delivered inside the already mutually-authenticated control channel.

Chunks are 4 MiB; the last chunk of a file may be smaller. Each `DATA` frame carries:

- File index from the manifest
- Absolute file offset
- SHA-256 of the chunk bytes
- Up to 4 MiB of payload

The sender assigns chunk indices from a shared atomic counter; up to four workers send concurrently. The receiver may write chunks out of order using positioned writes. It verifies each chunk hash before writing and records the chunk in an in-memory bitmap.

The control channel sends `PING` every two seconds while data is moving so the receiver's control socket does not expire. The receiver does not need to reply.

### 5. Finish and finalization

1. The sender waits for all data workers to complete, then sends `FINISH` on the control channel.
2. The receiver waits for data channels to drain, verifies that all bytes/chunks are present, and finalizes each file.
3. Finalization closes the part file, atomically renames it over the chosen target, and applies conflict policy.
4. The receiver sends `DONE`.
5. Both sides emit a completion event. The sender's completion means the receiver confirmed finalization.

Failure paths emit `ERROR` or `CANCEL` and close the session. A partially written `.part` file may remain after a crash; the current MVP does not resume it.

## Conflict policy

The receiver supports:

- `0`: rename on conflict (`name (1).ext`)
- `1`: overwrite
- `2`: reserved / currently treated as rename

## Error handling

Errors use a numeric code and a human-readable message. The current implementation primarily uses code `1` for transfer/session failures and relies on the message for detail. Errors are expected to be user-safe and never include remote-controlled file contents.

## Versioning

The `HELLO` version and the DNS/mDNS advertisement version are currently `1`. Frame types are append-only. Any incompatible change requires a new ALPN (`wisp/2`) and should negotiate capabilities before offering files.

## Not implemented in v1

- Resume from a persisted chunk bitmap
- Multiple files across multiple simultaneous receivers
- Compression
- QUIC transport
- Relay or internet traversal
- File previews or inline rendering
- Text/clipboard objects
