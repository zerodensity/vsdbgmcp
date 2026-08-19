# Releasing

## Cut the build

1. Bump the version in `src/VsDbgMcp.Host/source.extension.vsixmanifest` and in
   `Directory.Build.props`. They live in two files that cannot see each other, so
   `build.ps1` fails the build when they disagree.
2. Add the entry to `CHANGELOG.md`.
3. `.\build.ps1`

   It runs the tests, publishes the shim self-contained for `win-x64`, builds the VSIX,
   and then looks inside the package for `shim/vsdbgmcp.exe`, both images and
   `LICENSE.txt`. All of that arrives through packaging metadata that otherwise fails
   silently, which is why it is checked rather than assumed.
4. Tag, and attach `src/VsDbgMcp.Host/bin/Release/VsDbgMcp.Host.vsix` to a GitHub
   release.

## Publish to the Marketplace

The listing text is `marketplace/overview.md`; `marketplace/publishManifest.json` holds
the rest.

**The first time**, do it through the web form at
[marketplace.visualstudio.com/manage](https://marketplace.visualstudio.com/manage) — the
categories, overview and Q&A setting are only editable there, and the extension is not
public until you pick **Make Public** afterwards.

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
