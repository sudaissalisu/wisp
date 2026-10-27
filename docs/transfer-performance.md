# Transfer performance

This note records what changed in the 0.3.0 transfer path, the measured effect,
and what is still on the table.

## Baseline path

The engine already used:

- TLS 1.3 over TCP with up to four data channels;
- a shared atomic `next_chunk` counter, so channels steal chunks instead of
  receiving fixed ranges;
- 4 MiB chunks with a SHA-256 digest per chunk;
- 4 MiB socket send/receive buffers;
- OpenSSL EVP SHA-256 (hardware accelerated where the CPU supports it).

The obvious remaining costs were per-chunk memory traffic and static channel
counts.

## 0.3.0 changes

1. **Zero-copy DATA framing.** `send_data_frame` writes the 20-byte frame
   header, the 44-byte chunk header, and the file buffer directly to TLS. The
   previous path built a second `std::vector` with `encode_data` and copied every
   4 MiB chunk into it before encryption.
2. **Receiver buffer reuse.** Each data channel keeps one `Frame` and reserves
   `kChunkSize + 64` bytes, so chunk reception does not allocate and free a
   4 MiB vector per chunk.
3. **Adaptive channel count.** When `data_channels` is 0, the sender chooses:
   - 6 channels on 8+ CPU cores,
   - 5 on 6–7 cores,
   - 4 on 4–5 cores,
   - 2 on fewer cores;
   - capped at 4 for transfers under 32 MiB and at 2 under 4 MiB,
   - capped by the chunk count.
   An explicit 1–8 in `WispConfig` still pins the count. The native log records
   `transfer … using N data channels for … bytes`.

Chunk scheduling itself was already work-stealing and is unchanged.

## Measurements

Same machine, same binary layout, `wisp_native_tests.exe`; the test transfers a
17.0784 MiB mixed set (large file, small files, an empty file, an image folder)
and includes TLS setup.

| Build | Runs (MiB/s) | Mean |
|---|---|---|
| Before (0.2.1) | 11.93, 11.74, 12.52 | 12.06 |
| After (0.3.0) | 14.71, 13.82, 15.29, 13.52, 14.27, 15.83 | 14.57 |

That is roughly a 20% improvement on this loopback test. The C# 8 MiB interop
test measured 25–28 MiB/s after the change (16.6 MiB/s in the earlier note).

These are loopback figures on a workstation. They show that the copy/allocation
work was on the critical path; they are **not** a prediction for phone Wi-Fi.
On a real link, the radio, channel width, AP, and thermals dominate, and more
TLS streams can help or hurt depending on the driver.

## Why not more channels always?

Each data channel is a separate TLS 1.3 connection with its own handshake,
buffers, and crypto work. On a small phone or a congested AP, 6–8 streams can
oversubscribe cores and increase latency. The heuristic keeps small transfers at
2–4 streams and only scales large transfers on CPUs with headroom.

## Reproduce

```bat
tools\build-native.bat
build\native\bin\wisp_native_tests.exe
dotnet run --project dotnet\tools\Wisp.Smoke\Wisp.Smoke.csproj -c Release
```

Run each at least three times and report the mean; loopback scores vary with
CPU frequency and antivirus activity.

## Next

- Pipeline disk read + hashing in a bounded producer queue so sender threads
  never wait on SHA-256.
- Keep per-file handles open instead of opening and closing the source file for
  every chunk.
- Scatter/gather TLS writes beyond the three-part data frame (for example,
  batching multiple chunks per write).
- Optional compression for text/manifest-heavy transfers, behind a negotiated
  protocol flag with an explicit CPU-versus-bandwidth decision.
- Measure on real Wi-Fi 5/6/6E links with two phones and on Windows, including
  thermals and screen-off behavior.
