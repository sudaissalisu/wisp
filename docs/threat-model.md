# Wisp threat model

## Scope

This document covers the Wisp MVP: a Windows 11 and Android client exchanging files directly on a local network. It assumes the two devices are on the same layer-2 subnet or otherwise directly routable.

Out of scope for now: internet relay, accounts, multi-user identity integration, enterprise device management, and protection from a fully compromised endpoint.

## Assets

- File contents in transit
- File contents at rest in the destination folder
- Peer identity private keys and certificate pinning state
- The user's decision about which device and which files to accept
- Trust relationships (fingerprints marked as trusted)
- Metadata: device names, peer IDs, file names, sizes, timestamps, transfer timing

## Trust boundaries

1. **Untrusted network**: Wi-Fi/Ethernet, including other clients, the access point, and anyone able to spoof discovery traffic.
2. **Peer device**: the remote application and operating system may be malicious or compromised.
3. **Local operating system and app data**: assumed not fully compromised during normal operation.
4. **Application boundary**: managed C# UI ↔ native C++ engine (a memory-safety boundary).

## Adversaries and mitigations

### Passive network observer

**Capability:** observes packets on the LAN.

**Mitigations:**
- All file data and control data travel inside TLS 1.3.
- TLS 1.3 provides forward secrecy and AEAD encryption.
- Discovery beacons contain only public metadata (name, platform, peer ID, TLS fingerprint, listening port), no file names or contents.

**Residual:** traffic analysis can reveal transfer timing and approximate volume. Display names and peer IDs are public on the LAN.

### Active man-in-the-middle on first contact

**Capability:** can intercept/spoof discovery and TCP/TLS traffic.

**Mitigations:**
- TLS 1.3 with certificate pinning. The sender rejects a server certificate whose fingerprint differs from the peer record.
- Both endpoints display a six-digit pairing code derived from the TLS exporter. An active MITM creates two different TLS sessions, so the codes shown on the two devices will not match unless the user fails to compare them.
- Manual peer entry requires the remote TLS fingerprint (64 hex characters) and pins it.

**Residual:** if the user accepts without comparing the code, a first-contact MITM is possible. The current UI does not enforce a "both codes matched" confirmation; it is a user judgement. Trusted-device auto-accept intentionally skips this check for previously pinned peers.

### Spoofed discovery beacon

**Capability:** broadcast a fake Wisp beacon with attacker-controlled name/fingerprint on the LAN.

**Mitigations:**
- Discovery data is never treated as proof of identity.
- Sender pins the fingerprint from discovery; a fake record causes a TLS fingerprint mismatch or a different pairing code.
- Receiver never auto-accepts an unknown peer solely because it appeared in discovery.
- Manual/QR entry can bypass discovery.

**Residual:** denial of service and misleading device names are possible. The user may see a fake peer that fails to connect or shows a mismatched code.

### Malicious sender / malicious manifest

**Capability:** sends crafted frames or a hostile file manifest.

**Mitigations:**
- Frame lengths and types are bounded and validated.
- Manifest limits: file count, total size, path length, duplicate names.
- Paths must be relative, without `..`, absolute paths, drive letters, NULs, reserved Windows names, trailing spaces/dots, or illegal characters.
- The receiver chooses the destination root; canonical path checks prevent traversal or pre-existing symlink escapes.
- The receiver writes to a unique part file and only renames after all chunks verify.
- The sender cannot request execution of files or write metadata outside the destination.
- Unknown/extra frames and protocol violations fail the transfer.

**Residual:** file names can still be confusing or socially engineered; the receiver displays the manifest, and users should inspect it before accepting. Very large file counts can consume UI time despite limits.

### Malicious receiver

**Capability:** receives files and can lie about completion or keep data.

**Mitigations:**
- Files are encrypted to the pinned receiver; a malicious receiver is an authorized endpoint.
- Sender's completion means the receiver acknowledged finalization, not that the receiver will not misuse the data.

**Residual:** fundamental to any receiver; sender must trust the destination device.

### On-path resource exhaustion

**Capability:** opens many connections or sends oversized/invalid requests.

**Mitigations:**
- 8 MiB frame cap, manifest caps, fixed 4 MiB chunks.
- Socket timeouts, offer wait limit (120 s), data drain timeout.
- TLS handshake requires a client certificate.
- Each connection is bounded in memory by fixed buffers; data channels wait on TCP backpressure.
- No unbounded per-chunk event callbacks.

**Residual:** no global connection-count or per-IP rate limiter yet. A LAN attacker can still consume CPU with many handshakes or offers. This is a TODO before a public/enterprise deployment.

### Local attacker / stolen device

**Capability:** can read application files on the device.

**Mitigations:**
- Android app-private storage and Windows user profile permissions protect identity/key files from other ordinary apps/users.
- Received files are written to the user's chosen destination.

**Residual:** identity private keys are currently stored as PEM files in app-private storage, not wrapped with Android Keystore or Windows DPAPI. A rooted/compromised device or backup extraction can recover the key. This must be fixed before calling the app production-grade. Trusted fingerprints and settings are ordinary JSON.

### Screenshot/shoulder-surfing and UI attacks

**Capability:** observes the screen or tricks the user into accepting.

**Mitigations:**
- The offer dialog shows sender name, file count, total size, sample file names, and the pairing code.
- Auto-accept is off by default and only available for explicitly trusted devices.
- The Android foreground notification identifies active transfers.

**Residual:** no screenshot protection, no biometric confirmation, no content preview, and no anti-tapjacking protections. The Windows app is unpackaged and does not use package identity for share targets.

### Native memory safety

**Capability:** malformed network input reaching the C++ engine.

**Mitigations:**
- C++17, bounds-checked Reader/Writer, no exception propagation across the C ABI, fixed-size operations, standard library containers.
- TLS parser is OpenSSL 3.5 LTS on Windows and 3.1.8 on Android (see below).

**Residual:** there is no fuzzing or external audit yet. The Android OpenSSL 3.1.8 tree is end-of-life and must be replaced with a maintained version before production.

## Cryptographic details

- Identity: Ed25519 self-signed X.509 certificate per install; CA validation intentionally disabled and replaced by fingerprint pinning plus pairing-code comparison.
- TLS: 1.3 only, mutual certificates, ALPN `wisp/1`.
- Pairing code: six decimal digits from the TLS exporter label `WISP-PAIR-1`. This is a short authentication string, not a password.
- Hashes: SHA-256 per 4 MiB chunk. TLS already protects transport integrity; the digest also catches local corruption and protocol bugs.
- Data-channel token: random 32 bytes generated by the receiver and delivered only on the accepted control channel; each data connection is also TLS-authenticated independently.

## Known gaps before production

1. Wrap identity keys with Android Keystore and Windows DPAPI/CNG.
2. Upgrade Android TLS to a maintained OpenSSL 3.5 LTS build (or BoringSSL) with 16 KiB page support.
3. Add connection/rate limits and a global offer cap.
4. Add resume state validation and protection against replay of old transfer tokens.
5. Add a strict "codes match" confirmation flow for first contact.
6. Avoid exposing names/metadata to every device on the LAN by default (visibility modes).
7. Add MSIX/package identity, signed releases, and auto-update integrity.
8. Independent cryptographic review, fuzzing, and a larger device matrix.
