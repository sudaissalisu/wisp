# Voice commands and Files search

Two local features help find and send received files without browsing a large
library. Neither uploads file contents to a server.

## Voice-intent sending

**Ask Wisp → Speak** uses Android's `SpeechRecognizer` with
`EXTRA_PREFER_OFFLINE`. Depending on the phone and its speech service, Android
may use an on-device model or a network-backed recognizer; Wisp does not record
or upload the audio itself and always requires a confirmation.

Permissions: `RECORD_AUDIO` is requested the first time Speak is used. Speech
recognition also requires a recognition service to be installed and available;
the button reports when it is not.

Recognized phrases are parsed by `VoiceCommandParser` (Wisp.Core), a local
rule-based parser. It understands:

- a send verb: send, share, give, deliver;
- a file descriptor: filename words, a type (video, photo, song, pdf, …), and
  date words (today, yesterday, last night, this week, last month, month names);
- a target after “to” or “for”, matched against online peer names.

Examples:

- “Send the video from yesterday to Sara.”
- “Share the invoice from June to John’s PC.”
- “Send the latest photo to Sara.”
- “Send the song to Sara.”

The matcher filters by type and date, requires filename words to actually occur
when the user names an object (“beach video” will not silently become an
unrelated newest video), and picks the newest best match. The parser only
proposes a command; Wisp shows the filename, target, and transcript in a
confirmation sheet before sending. It never auto-sends from voice.

Limitations:

- It is not an LLM and does not understand arbitrary conversation.
- It matches filenames, types, and dates, not image/video visual content.
- Device names and file names must be reasonably distinguishable.
- Android speech recognition availability and offline behavior vary by OEM.

## Files search

The Files tab search box matches names, types, dates, and extracted text from
text-like documents. It is built locally at scan time:

- file names and extensions;
- type labels (Photo, Video, Music, Document);
- month/year/day names from the modified date;
- up to 16 KiB of text from `.txt`, `.md`, `.csv`, `.json`, `.xml`, `.html`,
  `.log`, `.srt`, `.vtt`, `.ini`, `.cfg`, `.yml`, and `.yaml`, with a 2 MiB
  total budget per scan. HTML/XML tags are stripped.

Examples:

- `invoice june` — documents with “invoice” in the name/text modified in June.
- `video yesterday` — videos received yesterday.
- `photo` — all images.
- `beach` — files with “beach” in the name or text.

The search is intentionally not an image-understanding model: “beach video”
only matches filenames/text, not the visual content of a video. OCR, image
embeddings, PDF text extraction, and Whisper transcription require bundled
on-device models or platform ML services that are not included in this build.

## What is still model-dependent

These were requested and remain future work because they need model binaries,
additional runtime dependencies, and licensing decisions:

- OCR of images and scanned PDFs;
- image/video semantic embeddings and visual search;
- PDF summarization and extractive/abstractive document summaries;
- Whisper.cpp audio/video transcription;
- automatic tags and suggested names from content;
- duplicate detection across a library (hash-based duplicate checks are a
  possible local tool but are not wired into this build).

The roadmap keeps these grouped under “local AI file actions” with the rule
that they must run on-device, be opt-in, and be labeled as processed locally.
