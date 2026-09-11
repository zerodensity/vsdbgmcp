# Recoverable operations and captures

This iteration addresses the September 9, 2026 tool-improvement feedback. It
changes contract version 6 to 7; deploy the host and shim together. Existing VS
windows keep their loaded extension until restarted.

## Builds

`build` accepts `requestId` and `waitSeconds` (default 30, bounded to 60). It creates
an operation owned by the VS host before dispatching the command. Client cancellation
only ends the observation. `operations` lists requests and `operation_status` reads
or waits for one without entering the VS UI thread. `wait(for="operation:ID")` also
waits for an operation. Retry an identical request with the same requestId; reusing
the ID with different arguments is rejected. One outstanding build is permitted
per host, and an existing IDE build is not replaced.

Completion uses `IVsUpdateSolutionEvents.UpdateSolution_Done`, including its success
and cancellation flags. The observer is registered before issuing the build. Rebuild
uses the build manager's force-update operation rather than separate clean/build
commands sharing a waiter. Project-scoped clean/rebuild uses the selected hierarchy.
Internal reconciliation checks the build manager after it has been observed busy.
If completion notifications are lost and cancellation cannot be established, the
result stays explicitly unknown rather than inventing success. No observed start
means pending, not a successful no-op inferred from an old LastBuildInfo value.

`build_cancel(operationId=...)` distinguishes a queued request, a request accepted
by the build manager, and an already completed build. It never releases a build
waiter by pretending cancellation was completion. Terminal results are immutable.

The global Error List no longer determines build success or warning counts. MSVC,
linker, and MSBuild diagnostics are parsed from captured output, labeled with the
operation, configuration, source and collection timestamp. These timestamps are
observations, not compiler emission timestamps. Unknown project attribution remains
null. Counts explicitly mean **parsed occurrences**, with unique warning counts
separate. Complete diagnostic counts remain unknown because custom/localized output
may not match the parser. Raw logs remain available even when parsing misses a line.
Error List and IntelliSense enumeration remain available through VS itself; this
iteration does not add a separate historical Error List catalog.

`build_log(operationId, offset, maxChars)` returns `nextOffset`, `hasMore`, text,
and the full saved log path. Offsets count UTF-16 characters. `build_output` retains
its existing live-pane tail/filter behavior. Saved logs belong to their invocation,
so a later build does not overwrite them. Output-pane resets or buffered output may
limit collection; this is why parsed counts are never advertised as complete.

Operations are journaled beneath `%LOCALAPPDATA%\vsdbgmcp\operations\HOST_EPOCH`.
Known IDs can be read after a host restart; unfinished historical records are
reported as unknown. Reissuing a requestId after a *host restart* is not an automatic
resume: inspect the saved operationId first. Journaling failures appear in
`persistenceError`; in-memory results remain queryable while the host lives.

## Launch, breakpoint and debugger state

`launch(requestId=...)` returns a confirmed run/break state with a registered
debug process, or an outstanding operation after 30 seconds. It records startup
project and configuration, plus evaluated native launch command, directory, and
arguments when the project exposes them. Unsupported project properties stay null.
It refuses to issue Start into an existing debug session. Observation is established
through the host operation before dispatch, and late state remains queryable.
No-debug launches finish with command state `issued`; external process creation
remains unconfirmed, and the completed command does not block later launches.
Launch retries leave buffered stops and generations intact. Reconnecting invalidates
identities from the connection gap before accepting new debugger events. There is no claim that a modal was detected when no
modal query was performed. Launch failures before any observed process can remain
pending; this is deliberately conservative and requires external reconciliation.

`bp_set(requestId=...)` bounds waiting to 15 seconds, records the breakpoint ID as
soon as creation returns, and retains request details before entering automation.
Retrying the same ID does not install another breakpoint. The terminal operation
describes installation (`bound`, `pending-symbols`, or `rejected`); `bp_list` reads
subsequent binding state. Neither an RPC timeout nor the operation registry aborts
a COM call stuck inside VS. `operations` and `operation_status` avoid that UI queue.
The tool never restarts the user's IDE to recover a breakpoint request.

`debug_state` provides a bounded snapshot without evaluating watches, walking
stacks, or pausing the target. Debugger mode, registered processes, current host
generation, last observed transition, active capture, and last capture are separate.
An unavailable snapshot reports unknown, and a detached process is not reported
as having exited. Host generations must be interpreted together with the host epoch;
legacy shim event generations remain a distinct session-local counter.

Quiet `wait` responses contain `eventReceived=false`, outcome, and timestamped
observations. `structured=true` emits their JSON representation; stop events are
likewise available in that representation, including already-stopped and module outcomes. `expectedGeneration` rejects stale
single-instance waits before waiting. Buffered module events say "already loaded".
A mode snapshot is not a measurement of useful application progress.

## Profiles and reports

A capture gets a stable ID when start is requested. Originally a GUID, new IDs are
12 lowercase alphanumeric characters in 0.9.2 (contract 9);
existing GUID IDs remain valid. Collector session GUIDs stay internal to persistence.
Metadata records collector
session ID, owner PID and process start time when readable, executable,
configuration, instance, host epoch/generation, UTC start/end times, module/symbol
information, and intervention observations. Collector start/stop and pipe draining
run asynchronously outside the VS UI thread with a whole-command timeout. The module
catalog merges observations for the capture PID on module loads and before stopping,
so late-loaded plugin symbols survive detach and recovery. Module-load bursts share
one pending UI query, including after observation timeouts. A failed or timed-out
catalog refresh preserves the raw package for export.

Entering design mode preserves the collection. Stops after a debugger transition
are labeled interrupted. `profile_status` lists saved metadata and checks raw file
availability; a saved collecting status alone is not proof the collector is alive.
`profile_recover(captureId)` aggregates a completed package without a debug session.
`profile_recover_stop(captureId)` explicitly stops the retained collector GUID first,
including after a host restart. It refuses to replace a different active capture.
Unknown collector outcomes retain ownership so that a retry cannot silently abandon
the original session. A readable, closed package can be recovered even when its
manifest still says stopping. Its end time is explicitly estimated from package
modification time. Offline recovery preserves raw evidence for later host
reconciliation. Concurrent stop requests share one capture-specific operation, whose
retained result cannot be replaced by a later capture.

Aggregates persist below `profiles\aggregates` and are loaded by a new shim. Integer
capture numbers are convenient aliases local to that shim; `captureId` is stable.
`profile_export(directory, captureId, includeRaw)` writes JSON and a readable report,
plus the original collector package when retained. Existing exports are not overwritten.
Raw evidence is deleted only after successful durable aggregation unless
`profile_start(retainRaw=true)` was requested. Parsing failures preserve the package.

Retention is configurable with positive integer environment variables:

| Setting | Default |
|---|---:|
| `VSDBGMCP_RAW_RETENTION_DAYS` | 7 |
| `VSDBGMCP_RAW_BUDGET_MB` | 2048 |
| `VSDBGMCP_AGGREGATE_RETENTION_DAYS` | 30 |
| `VSDBGMCP_AGGREGATE_BUDGET_MB` | 512 |

Completed raw packages are pruned oldest-first before starting another collection;
active/unknown collections are protected. If protected packages exhaust the budget,
another collection is refused. This is a retained-file budget, not an instantaneous
size limit on a running collector. Aggregate pruning runs when a persistent store
opens. Exports are outside these policies. Operation journals/logs are retained
until explicitly removed. `VSDBGMCP_DATA_DIR` overrides the data root for isolated
test instances; it also isolates shim staging.

Profiles record requested attach/detach, pause/resume/step, breakpoint changes,
thread freeze, and profile boundaries, plus observed mode changes. These timestamps
do not measure target suspension duration. Exported markers allow a consumer to
identify disturbed intervals; aggregates are still whole-capture counts, not an
interval-filterable time series. Unknown/lost sample and symbol coverage remains
bounded by what the collector/parser exposes.

`profile_report` is compact by default; `details=true` adds coverage, interventions,
and the capture catalog. Module/thread filters compose with self, module, or tree
views. Tree module filtering means subtrees rooted in the nominated module and keeps
their descendants. `focus` selects a captured function as subtree root; `rawTree=true`
keeps startup frames and disables percentage pruning. Row limits remain explicit.
Function misses offer related captured symbols with inclusive counts. Small positive
percentages render as `<0.1%`; denominators and inclusive overlap are explicit.

## Validation and remaining work

Automated tests cover operation races, early completion, cancellation of observation,
request deduplication, immutable snapshots, diagnostic parsing/ownership, durable
capture round trips, exports, composable filters and retention. The suite passes 563 tests. Host profile lifecycle tests compile the production
lifecycle and collector sources with a substituted VS boundary and collector command
runner. They cover concurrent stops, capture ownership, failed metadata persistence,
closed-package recovery, and late-loaded modules. Registry tests also verify that a
stalled update cannot block status reads. Retry/reconnect and structured wait
regressions are covered through the event bus and named-pipe shim. The actual stdio MCP catalog exposes all 59 tools,
including the structured operation output schema. The host and shim
are compiled and the release VSIX contents are checked by `build.ps1`.

Live VS validation was attempted in a separate `CodexValidation` experimental
instance using the native C++ fixture and an isolated data directory. The instance
stopped at first-run personalization; the computer-use helper returned
`coordinate input geometry is unavailable` on both the initial click and recovery.
The test process was closed. No live build/launch/profile result is claimed from
that attempt. The user's Nodos/UE5 instances were not modified or restarted.

Before release, validate no-op, failed and cancelled builds, queued/rejected launches,
late breakpoint completion, and interrupted profiling in a configured experimental
instance. Integrated scheduler/off-CPU tracing, nominated-output PDB/hash comparison,
automatic wrapper-chain folding, and a real-time policy mode remain deferred. CPU
sampling continues without elevation; no unrelated ETW session is touched.

SDK references: [build manager](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivssolutionbuildmanager2?view=visualstudiosdk-2022),
[completion events](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivsupdatesolutionevents2?view=visualstudiosdk-2022),
[build flags](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.vssolnbuildupdateflags?view=visualstudiosdk-2022).
