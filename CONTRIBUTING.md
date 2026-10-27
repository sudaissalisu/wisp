# Contributing to Wisp

## Ground rules

- Keep files local and private by default. Do not add analytics, cloud relays,
  or automatic uploads.
- Never bypass a receiver's accept decision or an operating-system permission/
  consent dialog.
- Treat the native C ABI as a compatibility boundary. Additive changes only;
  document breaking changes.
- Do not commit identity keys, keystores, tokens, captured traffic, or personal
  file paths.

## Workflow

1. Build the native tests first.
2. Make the smallest change that solves the problem.
3. Add or update tests where behavior is testable without hardware.
4. Validate on a real Android device when changing networking, storage, or
   permissions.
5. Update the relevant doc under `docs/`.

## Code style

- C++17, no exceptions across the C ABI, bounded parsing for all network input.
- C# uses nullable reference types, async I/O, and dispatcher marshaling for
  native callbacks.
- Keep UI wording short and avoid promising measurements or security properties
  the code does not verify.

## License

Wisp is a project by SSM Limited and is released under the [MIT License](LICENSE).
By submitting a contribution you agree that it is licensed under the same terms.
Third-party code must be compatible with MIT and recorded in
`THIRD_PARTY_NOTICES.md`.

## Commit messages

Use imperative, descriptive messages, for example:

- `Harden manifest path validation`
- `Add QR fallback for temporary links`
- `Fix peer expiry under Wi-Fi power save`

Avoid references to chat prompts or unrelated work in the same commit.
