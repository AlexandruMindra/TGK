# TGK — notes for contributors and coding agents

- Tests: `scripts/test.sh` (builds, then runs every test project in parallel). E2E SSH setup: `docs/DEV-TESTING.md`.
- Blossom (UI + windowing) comes from NuGet (`Blossom` in `src/TGK.Client/TGK.Client.csproj`); upgrade it there.
- **Versioning and releases follow `docs/RELEASING.md` — mandatory:**
  - The only version is `<Version>` in `Directory.Build.props` = the NEXT release (never an already published one).
  - Release = tag `vX.Y.Z` equal to that version; CI rejects a mismatch. Published `v*` tags/releases are never
    moved, deleted or overwritten — fix forward with a new PATCH version.
  - **Tags and releases only on the maintainer's word:** an agent creates or pushes a `v*` tag or a GitHub release only
    when the maintainer asked for that release or approved it explicitly (for that version); otherwise it may prepare
    one (CHANGELOG section, checks) and asks. Pushing to `main` (which rebuilds `nightly`) is not a release.
  - After tagging a release, bump `<Version>` to the next version in a follow-up commit.
  - `nightly` is a rolling pre-release rebuilt on every push to `main` (`X.Y.Z-nightly.N`); it is not a release.
  - Every user-visible change goes into `CHANGELOG.md` under `## [Unreleased]` in the same change; a release renames that
    section to `## [X.Y.Z] - date` (its text becomes the release notes; CI refuses a release without it).
- **Windows code signing** (Authenticode via Azure Artifact Signing, `.github/workflows/build.yml`; setup in
  `docs/RELEASING.md`, "Code signing") keeps SmartScreen from flagging TGK: keep it signing every Windows executable,
  TGK assembly and the installer, after the last change to them (sign, then zip/pack, then sign the installer).
  Never remove or weaken the signing or signature-check steps, never sign pull request builds, never put a
  certificate, key or secret in the repo (only GitHub secrets/variables).
- Installed clients update themselves from the release assets: keep their names and layout (`docs/RELEASING.md`,
  "Self-update contract").
