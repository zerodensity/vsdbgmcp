# Releasing

## Cut the build

1. Bump the version in `src/VsDbgMcp.Host/source.extension.vsixmanifest` and in
   `Directory.Build.props`. They live in two files that cannot see each other, so
   `build.ps1` fails the build when they disagree.
2. Add the entry to `CHANGELOG.md`.
3. Push a `v`-prefixed tag:

   ```powershell
   git tag v0.1.0
   git push origin v0.1.0
   ```

   That is what cuts a release; nothing else does. `.github/workflows/release.yml`
   checks the tag against the manifest, builds, and publishes a GitHub release carrying
   `vsdbgmcp-<version>.vsix` with this version's changelog entry as the notes. It fails
   rather than publishing if the tag and the manifest disagree, or if `CHANGELOG.md` has
   no entry for the version.

To build locally instead, `.\build.ps1` does the same work: tests, the self-contained
`win-x64` shim, the VSIX, and then a look inside the package for `shim/vsdbgmcp.exe`,
both images and `LICENSE.txt`. All of that arrives through packaging metadata that
otherwise fails silently, which is why it is checked rather than assumed.

## Publish to the Marketplace

Publishing the listing is separate from cutting a release, and a release can ship
without touching it. See [marketplace.md](marketplace.md) for the listing itself, what
has to change in it when the product changes, and what cannot be taken back once
uploaded.
