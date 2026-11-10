# The Marketplace listing

The listing is two files in `marketplace/`, and they are the listing: what is published
is whatever they say at the moment `VsixPublisher` runs. Nothing is written in the web
form except the once-only fields below, so the copy lives in the repository, changes in
the same commit as the thing it describes, and is reviewed the same way.

| file | what it is |
|---|---|
| `overview.md` | the page body. Markdown, rendered by the Marketplace |
| `publishManifest.json` | identity, categories, publisher, price, Q&A, repo link |

## When the product changes

Every release tag publishes the listing as it is in the tagged commit, so check it
before tagging. What must not happen is the listing describing a version that no longer
exists; this is the check list for whether it still tells the truth.

- **A tool was added or removed.** `overview.md` says how many tools there are and
  groups them. Both are in the *Tools* section, and the count is also in
  `docs/design.md` and `README.md`; all three move together or one of them starts lying.
- **A tool worth knowing about was added.** The *Tools* section carries a short list of
  the ones that answer a question nobody expects to be answerable. It is a sample, not
  an inventory: something belongs there if a reader would not guess the product could do
  it.
- **A limit was fixed.** Take it out of *Known limits*. A limit listed after it is gone
  costs more than one never listed, because a reader believes it.
- **A limit was found.** Put it in, in the same voice as the rest: what does not work,
  why, and what the product does instead of pretending. This section is the reason the
  listing can be trusted about anything else.
- **A requirement changed.** Visual Studio versions, editions, and anything that has to
  be installed alongside — the profiling component is in *Requirements* for that reason.
- **The setup command changed.** It appears in `overview.md`, in `README.md`, and in the
  panel, which builds it from the real path at run time. The panel is the one that
  cannot be wrong; the other two are copies of it.
- **A capability was added that is not a new tool.** Events reaching the model without
  it asking — the digest atop a reply, `status`'s recent list, `vsdbgmcp --follow` —
  changed none of the counts above, but it is exactly what the *Tools* section's short
  list is for: a reader would not guess a debugger server tells an agent things without
  being asked.

## Publishing it

The version in the manifest is what the Marketplace shows. The `marketplace` job of
`.github/workflows/release.yml` uploads the `.vsix` of the GitHub release the tag
produced (see [releasing.md](releasing.md)), so what is published is what was built and
tested, not a local rebuild.

The job signs in by trusted publishing; no token is stored. It needs two settings
outside the repository:

- **A Trusted Publishing policy** in the publisher's management page on the Marketplace,
  for repository owner `zerodensity`, repository `vsdbgmcp`, workflow `release.yml` and
  environment `production`.
- **The `production` environment** in the repository's settings. Its required reviewers
  decide who approves each upload; without any, every tag publishes at once.

The job runs `VsixPublisher.exe` with `-oidc`: the runner's Visual Studio SDK copy if it
accepts the option, otherwise the one in `Microsoft.VSSDK.BuildTools` 18.12.465, the first
package with it. When the job fails, rerun it; a version cannot be uploaded twice, so do
not retag. A rerun uses the workflow file of the tagged commit, so a fix to the workflow
reaches the Marketplace only with the next tag.

**The first time**, use the web form at
[marketplace.visualstudio.com/manage](https://marketplace.visualstudio.com/manage). The
categories, overview and Q&A setting are only editable there, and the extension is not
public until **Make Public** is pressed afterwards.

## What cannot be taken back

- **A version cannot be edited after upload and must increase.** So can the display
  name, the VSIX id and the supported Visual Studio versions: those are read from the
  manifest on first upload and fixed from then on.
- **`internalName` is the URL.** `ZeroDensity` plus `vsdbgmcp` gives
  `marketplace.visualstudio.com/items?itemName=ZeroDensity.vsdbgmcp`. Changing it means
  a new listing and every existing link pointing at nothing.
- **Removing an extension is irreversible**, and asks you to type its name to confirm.

## Signing

The package ships unsigned, which installs with a signature warning and is accepted by
the Marketplace. What is *not* accepted is a self-signed certificate, so signing means a
certificate from a real authority, and then `sign code` from the
[Sign CLI](https://github.com/dotnet/sign). `VSIXSignTool` is deprecated.

## Publisher access

Members are added to the publisher account by **User ID**, not by email — adding by
email fails with `TF14045`. The id is shown by hovering over your name on the
Marketplace, with a button to copy it.
