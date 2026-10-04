# Versioning and releases

## Rules

1. **One product version, one place.** `<Version>` in `Directory.Build.props` is the version of the client and the
   server. It is always the **next** release, never one that is already published.
2. **Semantic versioning** `MAJOR.MINOR.PATCH`: PATCH for fixes only, MINOR for new features (the default next step),
   MAJOR for breaking changes (e.g. a sync protocol or vault format change that older clients can't read).
3. **A release is a tag `vX.Y.Z` on `main` whose X.Y.Z equals `<Version>`.** CI refuses the release otherwise.
4. **Published releases are immutable.** Never move, delete or re-push a `v*` tag and never replace its files.
   A broken release is fixed by a new PATCH release.
5. **Right after releasing, bump `<Version>`** to the next planned version (usually the next MINOR) and push.
6. **Every user-visible change is written down in [CHANGELOG.md](../CHANGELOG.md)** under `## [Unreleased]`, in the
   same commit or pull request as the change. The release's section becomes its GitHub release notes; CI refuses to
   release a version without one. The nightly's notes show the Unreleased section.
7. **`nightly` is not a release.** Every push to `main` replaces the `nightly` pre-release (tag and files) with that
   commit's build, versioned `X.Y.Z-nightly.N` (X.Y.Z = the upcoming version, N = CI run number). Pull request
   builds are `X.Y.Z-ci.N` and are only kept as run artifacts.

Every binary also records its commit (`X.Y.Z+<sha>` in the informational version).

## Self-update contract

Clients look for updates on GitHub and install them in place ("Update now" in the status bar), so every release (and
the nightly) must keep publishing:

- `TGK-linux-x64.tar.gz` and `TGK-win-x64.zip`, each holding one top-level `TGK/` folder with the self-contained
  publish output (the executable `TGK` / `TGK.exe` and `TGK.dll` among it, and `tgk-mcp` / `tgk-mcp.exe`, the MCP
  server agents run from that folder: users register its path, so it must stay next to `TGK`). The Windows installer installs the same
  folder (per user, so it stays writable) and those installs update from the zip;
- `TGK-osx-arm64.tar.gz` and `TGK-osx-x64.tar.gz`, each holding one top-level `TGK.app/` bundle (the publish output in
  `Contents/MacOS/`);
- `TGK-x86_64.AppImage`, which an AppImage replaces as a whole (it runs `tgk-mcp` when started with `--mcp`);
- a `TGK.dll` whose informational version equals the release (`X.Y.Z` for `vX.Y.Z`, the title's `X.Y.Z-nightly.N`
  for the nightly) — the client refuses an archive holding another version;
- the release title of the nightly as `Nightly X.Y.Z-nightly.N`.

The client verifies each package against the SHA-256 digest GitHub reports for the asset. Renaming the assets or
changing the archive layout breaks updating for every installed client (they fall back to "Download from GitHub").
The other assets — `TGK-win-x64-setup.exe` and `TGK-osx-*.dmg` — are for first installs only. Packaging scripts:
`packaging/` (macOS bundle and disk image, AppImage, Inno Setup script).

## Cutting a release

```bash
# 1. <Version> in Directory.Build.props already holds X.Y.Z (rule 1); main is green.
#    In CHANGELOG.md rename "## [Unreleased]" to "## [X.Y.Z] - YYYY-MM-DD", start a new empty "## [Unreleased]" above
#    it and update the compare links at the bottom; commit and push that first.
git tag -a vX.Y.Z -m "TGK X.Y.Z"
git push origin vX.Y.Z          # CI: tests -> Linux/Windows/macOS builds -> GitHub release "TGK X.Y.Z"

# 2. Bump to the next version (rule 5)
#    edit Directory.Build.props: <Version>X.(Y+1).0</Version>
git commit -am "Start X.(Y+1).0" && git push
```

## Downloads

- Stable: https://github.com/AlexandruMindra/TGK/releases/latest
- Latest `main` build: https://github.com/AlexandruMindra/TGK/releases/tag/nightly
  (stable file names, e.g. `.../releases/download/nightly/TGK-linux-x64.tar.gz`)
