# Update checks

Wisp checks a small JSON manifest hosted with the GitHub repository. There is no
custom update server, account, or analytics endpoint.

## Manifest

`update.json` at the repository root (served over HTTPS from
`raw.githubusercontent.com/sudaissalisu/wisp/main/update.json`):

```json
{
  "latest": "0.5.0",
  "name": "Wisp 0.5.0",
  "published": "2026-10-28",
  "notes": "• …",
  "release_url": "https://github.com/…/releases/tag/v0.5.0",
  "android_url": "https://github.com/…/Wisp-android-0.5.0.apk",
  "windows_url": "https://github.com/…/Wisp-Setup-0.5.0.exe",
  "mandatory": false
}
```

`mandatory` is reserved and currently not enforced; keep it `false`. `UpdateService` in Wisp.Core fetches it with a cache-busting timestamp, parses
it, and compares the semantic version with the running app. The request carries
only a `Wisp/<version> (<platform>)` user agent; no device ID, file names, or
analytics are sent. URLs must be HTTPS; the app falls back to `release_url` when
a platform asset is absent.

## Behavior

- Automatic check at most once per 24 hours, after the app starts.
- If a newer version exists:
  - Android shows a compact **Update available** banner on Nearby and, once per
    version, a release-notes sheet with **Download update**, **Later**, and
    **Skip this version**.
  - Windows shows a ContentDialog with the same release notes and actions.
  - **Download** opens the signed APK (Android) or the Setup EXE (Windows) in the
    browser. Wisp never downloads and installs silently; the user confirms the
    browser/installer flow.
  - **Later** hides the prompt; the Android banner remains so the user can
    reopen it.
  - **Skip this version** remembers the version and suppresses automatic
    prompts for it. Manual **Check** still offers it.
- Manual **Check for updates** is in Settings → About on Android and in the
  About card on Windows. It bypasses the 24-hour throttle and skip list and
  reports “up to date” or the error.
- A failed automatic check is silent. A failed manual check shows a readable
  message.

## Release process

Before tagging a new release:

1. Bump `ApplicationDisplayVersion`/`ApplicationVersion` (Android) and the
   Windows/installer version.
2. Build and verify the APK/EXE assets and checksums.
3. Update `update.json` with the new version, notes, release URL, and the exact
   asset URLs.
4. Commit and push, create the GitHub release, upload the assets, then switch
   `update.json` to the final version if it was prepared early.
5. Install the previous release and run a manual check to confirm the prompt and
   download link; install, verify, and confirm the new version reports current.

Because the manifest is on `main`, updating it publishes the update to all
clients immediately. Keep asset URLs correct; the app validates HTTPS but does
not verify a signature beyond the APK/installer signing and Android's install
confirmation.

## QA override

Windows and the smoke tool honor `WISP_UPDATE_MANIFEST_URL` to point at a local
manifest for testing without publishing a release:

```powershell
$env:WISP_UPDATE_MANIFEST_URL = "http://127.0.0.1:8080/update.json"
```

The URL still must return valid JSON and the app still requires an HTTPS
download URL for the **Download** action. Android has no runtime environment
override; test the prompt by temporarily publishing a newer manifest version.

## Limits

- Sideload APKs are not Play Store updates. Android opens the browser; the user
  must allow installs from that browser and confirm the package installer. There
  is no delta update, background install, or mandatory-update enforcement.
- Windows opens the installer; there is no silent self-update.
- The 24-hour check is per app installation. Offline starts are silent and will
  retry on the next start after the last successful check interval.
- Manifest authenticity relies on the GitHub repository and HTTPS. A compromised
  repository could point users at a malicious installer; the same is true of a
  compromised release page.

## Checklist

1. Install the previous release; manual Check shows the new version and release
   notes, and Download opens the correct asset.
2. Automatic check appears once; Later hides it but leaves the Android banner;
   Skip suppresses the next automatic prompt.
3. Manual Check after Skip still offers the update.
4. Current build reports “up to date.”
5. Airplane mode: automatic check stays silent; manual check shows a readable
   offline message.
6. Install the new APK; the version and About copy update and a subsequent check
   reports current.
