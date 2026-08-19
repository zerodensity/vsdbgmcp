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

The listing text is `marketplace/overview.md`; `marketplace/publishManifest.json` holds
the rest.

**The first time**, do it through the web form at
[marketplace.visualstudio.com/manage](https://marketplace.visualstudio.com/manage) — the
categories, overview and Q&A setting are only editable there, and the extension is not
public until you pick **Make Public** afterwards.

Take the `.vsix` from the GitHub release the tag produced.

**After that**, from a Developer PowerShell:

```powershell
& "${env:VSINSTALLDIR}\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe" publish `
    -payload  "src\VsDbgMcp.Host\bin\Release\VsDbgMcp.Host.vsix" `
    -publishManifest "marketplace\publishManifest.json" `
    -personalAccessToken $env:VSMARKETPLACE_PAT
```

## Things that cannot be undone

- **The version cannot be edited after upload, and must increase.** So can the display
  name, VSIX ID and supported versions — those are read from the manifest on first
  upload and fixed from then on.
- **`internalName` is the URL.** `ZeroDensity` + `vsdbgmcp` gives
  `marketplace.visualstudio.com/items?itemName=ZeroDensity.vsdbgmcp`.
- **Removing an extension is irreversible** and asks you to type its name to confirm.

## Signing

The package ships unsigned, which installs with a signature warning and is accepted by
the Marketplace. What is *not* accepted is a self-signed certificate, so signing means a
certificate from a real authority — then `sign code` from the
[Sign CLI](https://github.com/dotnet/sign); `VSIXSignTool` is deprecated.

## Publisher access

Members are added to the publisher account by **User ID**, not email — adding by email
fails with `TF14045`. The ID is shown by hovering over your name on the Marketplace, with
a button to copy it.
