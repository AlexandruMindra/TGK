# TGK — notes for contributors and coding agents

- Tests: `scripts/test.sh` (builds, then runs every test project in parallel). E2E SSH setup: `docs/DEV-TESTING.md`.
- Never modify `external/` (Blossom submodule).
- **Versioning and releases follow `docs/RELEASING.md` — mandatory:**
  - The only version is `<Version>` in `Directory.Build.props` = the NEXT release (never an already published one).
  - Release = tag `vX.Y.Z` equal to that version; CI rejects a mismatch. Published `v*` tags/releases are never
    moved, deleted or overwritten — fix forward with a new PATCH version.
  - After tagging a release, bump `<Version>` to the next version in a follow-up commit.
  - `nightly` is a rolling pre-release rebuilt on every push to `main` (`X.Y.Z-nightly.N`); it is not a release.
  - Every user-visible change goes into `CHANGELOG.md` under `## [Unreleased]` in the same change; a release renames that
    section to `## [X.Y.Z] - date` (its text becomes the release notes; CI refuses a release without it).
- Installed clients update themselves from the release assets: keep their names and layout (`docs/RELEASING.md`,
  "Self-update contract").
