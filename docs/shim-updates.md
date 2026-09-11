# Shim updates (0.9.3)

The VSIX update takes effect when its package next loads in Visual Studio. The
package stages the bundled shim at the stable configured path, then retires the
previous processes from that installation. This works with older shim releases;
they do not need an update watcher or a new RPC method.

Staging is serialized by a named mutex keyed by the full destination directory.
Equal or newer installed versions are left alone. Dependencies are copied first;
any replacement failure prevents executable publication and process retirement.
The new executable is copied to a temporary file, the old executable is moved aside,
and process handles are captured before the new executable is published by rename.
The old executable is restored if publication fails. A new shim launched after
publication is outside the captured set. Cached process handles protect against PID
reuse. Only the exact installed executable or its known `.superseded` filenames
qualify; matching a process name alone never authorizes termination.

After successful publication, only those captured shim processes are terminated.
No process tree is terminated. Exit observation has a three-second total bound;
inspection failures and unconfirmed exits are reported explicitly. Old files are
pruned when Windows permits deletion. Staging runs off the Visual Studio UI thread.

The MCP client owns the stdio transport and must launch a fresh shim. There is no
portable way for the extension to restart an arbitrary client's MCP connection.
Clients that do not reconnect automatically need an MCP server restart. The update
can interrupt tool replies and shim-local state; it does not establish whether
host-side work succeeded, failed, or was cancelled. After reconnect, inspect known
operation IDs with `operation_status`; host-session identity still bounds retry
deduplication. No command is replayed by staging. Development `build.ps1 -Install`
continues to be a direct file copy and requires a client restart.

## Validation

`ShimStagingTests` uses private temporary installations and real Windows processes
with redirected stdio. It covers previous/superseded processes, unrelated directories,
fresh launches after capture, equal/newer versions, locked dependencies/executables,
retry after a lock is released, and bounded concurrent staging.

`tests/live/run.ps1 -ShimUpgrade` builds a real older-version shim into its dedicated
data directory, initializes its MCP stdio session, starts the experimental Visual
Studio profile, checks retirement and preservation of an unrelated-directory shim,
then runs the normal named-pipe/debugger checks through the newly staged executable.
It does not upgrade or close normal Visual Studio instances. Results are saved beside
the existing live artifacts in `shim-upgrade.json` and `results.json`.

Validated on 2026-09-11: 626 automated tests and Release VSIX packaging passed.
The isolated live run `artifacts/live/d86a0bd78fb74f148500dada33bda29d` retired the
initialized old-version shim, preserved the other-directory shim, and passed the
build/cancellation/reconnect/deduplication/diagnostics/debugger suite through the
replacement. This exercises package startup in the experimental profile, not the
Marketplace installer UI or automatic reconnect behavior of individual MCP clients.
