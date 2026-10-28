# Browser link (zero-install receiving and web sending)

The browser link lets a device that does not have Wisp send files to, or
download files from, the Android phone using only a web browser. It works on
iPhone/iPad Safari, Android Chrome, Windows, macOS, and Linux browsers on the
same Wi-Fi.

## Flow

1. On the phone, tap **Browser link → Start browser link**.
2. Wisp finds the phone's private LAN IPv4 address, starts a local HTTP server
   on an ephemeral port, and shows a QR code and URL.
3. The other person scans the QR with the camera or opens the printed URL.
4. The page can upload files to the phone, download files the phone has
   received, and send a text/link to the phone clipboard.
5. **Stop** ends the server and clears the link. Closing the sheet only hides
   it; the link stays active until Stop or 30 minutes after the last browser
   request.

The page is served from the phone. There is no cloud service, account, or
external CDN, and the HTML/CSS/JS is embedded in the app.

## Web sender / iOS browser support

The same page is the web sender: iOS Safari and any other browser can upload to
the phone. A native iOS client is not part of this build (it needs Xcode/macOS
and a separate app); the browser path is the practical iOS entry point today.

Multiple browsers can use the link at the same time, so it doubles as a simple
group receiving/download room.

## HTTP surface

The server binds to one private IPv4 address and requires the random 32-hex
token from the URL on every request (query `t` or `X-Wisp-Token`).

| Route | Purpose |
|---|---|
| `GET /?t=…` | Embedded offline page |
| `GET /api/files?t=…` | JSON list of received files (path, name, size, modified, kind) |
| `POST /api/upload?t=…&name=…` | Raw request body streamed to a unique file in the received folder |
| `GET /api/download?t=…&path=…` | Stream a listed file, with `Content-Disposition` |
| `POST /api/text?t=…` | Copy a short text/link to the Android clipboard |

Implementation: `dotnet/src/Wisp.Core/BrowserLinkServer.cs`; page:
`dotnet/src/Wisp.Core/BrowserPage.html` (embedded resource).

Uploads are written to a `.wisp-upload-*.part` file and renamed only when the
full `Content-Length` body arrives. Names are sanitized, duplicates get a
numbered suffix, paths are resolved inside the received-files root, and request
headers are capped at 16 KiB. The server closes each connection after one
request.

## Security properties and limits

- **The link is a temporary password.** Anyone who has the URL while the link is
  open can upload, download, or send text. Treat it like the Wisp QR link.
- **Local HTTP, not Wisp TLS.** The browser page cannot use the app's pinned TLS
  transport, so traffic between the browser and the phone is not encrypted at
  the app layer. Use the browser link on a trusted Wi-Fi network, not public
  Wi-Fi. The page and the in-app sheet say this explicitly.
- The server binds to the phone's LAN IPv4 address, not all interfaces, and
  rejects requests without the token in constant time.
- The link expires after 30 minutes without a browser request; any page poll,
  upload, download, or text send resets that timer. Stop ends it immediately.
- Uploads go into the same app-private Wisp folder as native transfers and are
  exported to `Downloads/Wisp`. They appear in the Files tab.
- No arbitrary filesystem listing: downloads are limited to the received-files
  root and exact path containment is checked.
- No background browser server on Windows yet; Android is the host in this
  build.

## Device checklist

1. Connect the phone and another device to the same Wi-Fi.
2. Start the browser link, scan the QR with the other device, and confirm the
   page loads without installing anything.
3. Upload a photo and a large file; watch progress, confirm they appear in
   Files and `Downloads/Wisp`.
4. Download a received file; verify its hash on the browser device.
5. Send a URL from the page; confirm "Link or text copied to the clipboard"
   and that the text is on the Android clipboard.
6. Open the page on two browsers/devices; upload from both.
7. Stop the link; confirm the page now shows an expired link.
8. Leave a browser open for 30+ minutes with polling off; confirm the link
   closes and the QR/status update.
9. Confirm starting a temporary Wisp link while the browser link is active is
   refused (and vice versa).
