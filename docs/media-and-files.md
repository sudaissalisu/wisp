# Files and media playback

Wisp keeps every received file in its app-specific external folder
(`Android/data/com.wisp.app/files/Wisp`) and copies it to `Downloads/Wisp` on
Android 10+. The Files tab and the in-app players read the app-private copy; the
public copy is for other apps and file managers.

## Files tab

- `LibraryFile.ScanAsync` walks the Wisp folder on a background task, skips
  in-progress `.wisp-*.part` files, classifies by extension, and sorts newest
  first (capped at 500 entries).
- Filters are **All**, **Photos**, **Videos**, **Music**, and **Docs**. The
  summary shows the number of matching files and their total size.
- The list is refreshed when the tab is opened, after a received transfer
  completes, and from the Refresh button. The tab is not a general file manager:
  it deliberately lists the received-files folder only.
- Each row is one file. Tapping it opens the in-app viewer for images, video and
  audio, or hands the file to an installed app for everything else.

## Viewers

`MediaViewer` is a native Android dialog layer rather than a MAUI page. This
keeps decode/playback off the MAUI layout thread and avoids a second media stack.

### Images

- `BitmapFactory` with `InJustDecodeBounds` and a power-of-two `InSampleSize`
  caps the decoded image at 2048 px on the long edge.
- EXIF orientation is applied after decode through AndroidX `ExifInterface`.
- `ZoomableImageView` uses a `ScaleGestureDetector` for pinch zoom and a
  `GestureDetector` for pan and double-tap. The matrix is clamped so the image
  cannot be dragged completely out of view; double-tap toggles between fit and
  2.4×.

### Video

- `VideoView` plays the local file. The `OnPrepared` callback exposes the
  underlying `MediaPlayer`, which is used for speed (`PlaybackParams`) and mute
  (`SetVolume`) without replacing the surface.
- Controls: play/pause, −10 s, +10 s, seek bar with current/total time, speed
  cycling (1.0×, 1.25×, 1.5×, 2.0×, 0.75×), and mute. Controls auto-hide after
  three seconds while playing and return on tap. The dialog keeps the screen on.
- Completion rewinds to the start and shows the play button.

### Audio

- `MediaPlayer` with the same transport controls, a large track tile, and the
  same speed/mute behavior. Audio focus is requested on open and released on
  dismiss.
- Playback is foreground-only: backgrounding the app dismisses the viewer, so
  there is no hidden audio session or notification playback yet.

## Opening non-media files

Files live in app-specific storage, so a raw `file://` URI would throw on
Android 7+. A minimal `FileProvider` (`com.wisp.app.fileprovider`) exposes:

```xml
<external-files-path name="wisp" path="Wisp/" />
<cache-path name="outgoing" path="." />
```

`AndroidFileService.OpenWithSystem` builds a content URI, resolves the MIME type
with `MimeTypeMap`, grants read access for the intent, and starts `ACTION_VIEW`.
If no app handles the type, Wisp shows a readable message instead of crashing.

## History

History rows for completed received transfers are tappable. A single file opens
directly; a multi-file transfer shows an action sheet of relative paths. Sent
transfers show an explanatory message because the bytes live on the other
device. Entries whose folder was deleted show a message and refresh the library.

## Limits

- Platform codecs only; there is no bundled software decoder. Unsupported video
  or audio formats show an error and dismiss the player.
- No picture-in-picture, casting, subtitles, playlists, background audio, or
  media-session notification yet.
- The Files tab lists the app's received folder, not arbitrary device storage;
  public `Downloads/Wisp` files are managed by Android's MediaStore.

## On-device checklist

1. Receive one image, one video, one audio file and one PDF.
2. Open Files and confirm all four appear with the right type and filter.
3. Image: pinch, pan, double-tap, rotate; the image stays inside the viewport.
4. Video: play/pause, seek, ±10 s, speed, mute, controls auto-hide, completion
   rewinds; leaving the app stops playback.
5. Audio: same controls; audio focus returns after dismissal.
6. PDF: the system viewer opens through the FileProvider, with no storage
   permission prompt.
7. History: tap the single-file transfer and the multi-file transfer; both open
   the right files.
8. Delete a received file and confirm the library reports it is unavailable
   instead of crashing.
