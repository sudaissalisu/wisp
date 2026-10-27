# AdMob placement plan

Wisp is a free, open-source utility, not an ad-first app. Ads must never sit
between a user and a file transfer, and the open-source build must remain
ad-free and fully functional. This document is a plan only: **0.2.1 ships with
no ad SDK and no ad code.**

## Hard rules

- No ads in the send sheet, accept dialog, transfer sheet, onboarding, QR flow,
  share sheet, or foreground-service notification.
- No interstitial or app-open ad on launch, after a transfer, or after a failed
  transfer.
- No ad SDK initialization before the native engine is ready.
- Non-personalized ads by default; request consent where required (EEA/UK) and
  honor “do not sell/share” signals.
- Never request location, contacts, or file access from the ad SDK.
- The Windows desktop app stays ad-free. It is open-source utility software.
- A build with `WISP_ADS` unset must not reference the Ads SDK at all.

## Recommended placement

1. **Android Settings → About only.** A single adaptive banner below the project
   credits and the source/license buttons. This is the least disruptive surface,
   it never covers a primary action, and it makes the SSM Limited relationship
   visible in the same place. Load the ad when the About category is opened and
   destroy it when the user leaves the category or the app.

Do not place ads on Nearby, Transfers, the share sheet, the temporary-link
sheet, the offer sheet, or the transfer sheet.

2. **Optional future: “Support Wisp” card.** A separate card under About with one
   banner and a short note. Never on the main Settings menu.

3. **No rewarded ads for core behavior.** If a rewarded format is ever added, it
   must unlock a genuinely optional cosmetic feature (for example a theme pack).
   It must never gate visibility, speed, file size, receiving, or history.

## Implementation notes

- Add a small `IAdService` platform abstraction in `Wisp.Android`, implemented by
  a `WISP_ADS`-conditional Google Mobile Ads wrapper.
- Initialize the SDK lazily when the About category is first opened, never in
  `MauiProgram` or `MainPage` construction.
- Keep loading and presentation off the transfer threads; cancel loads when the
  user leaves About or a transfer starts.
- Use `AdRequest.Builder` with non-personalized ads by default; wire the UMP
  consent flow for regions that require it.
- Add a `WISP_ADS` MSBuild property and document `-p:WISP_ADS=false` for the
  F-Droid-style ad-free APK.
- Update the privacy policy, the Play Data safety form, and the store listing to
  disclose AdMob, its device-advertising identifier usage, and consent handling.
- Do not add the Google Mobile Ads NuGet package to `Wisp.Core`; keep ad code in
  the Android head so the shared engine and Windows app stay clean.
- Keep the About layout usable when the ad fails to load (no blank banner box).

## Measurement

- Track only aggregate fill/impression data from the AdMob console. Do not add a
  third-party analytics SDK to the app.
- Review the placement after a release: if the banner hurts session length or
  transfer completion, remove it rather than increasing its size.

## Privacy and policy checklist

- [ ] Privacy policy names Google AdMob and the advertising identifier.
- [ ] Consent flow implemented for EEA/UK/CH users.
- [ ] Children-directed settings are off; the app is not directed at children.
- [ ] `WISP_ADS` build flag documented in `docs/building.md`.
- [ ] Ad-free APK published alongside any ad-enabled APK.
