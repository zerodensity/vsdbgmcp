# Iteration 5 — actionable status and diagnostic evidence

Released as 0.9.0. This iteration adds one tool (`build_diagnostics`, 60 total) and
changes the host/shim contract to 8. Install both halves together when testing.
Repository guidance in `AGENTS.md` makes model-facing usability the primary design
criterion: tool selection, schemas, evidence, response size, and recovery guidance.

## One operation recovery path

After `build`, `launch`, or `bp_set` returns an operation ID, use `operation_status`.
`operations` finds IDs when the original reply is lost. `wait(for: "operation:ID")`
uses the same response format. There is no separate reconciliation or administrative
closure tool in the model-facing catalog.

The compact response contains `operationId`, `kind`, `state`, `message`, and, when
appropriate, `nextAction` with an actual tool name and arguments. A retained `result`
uses the original tool's result format. The list tool omits result bodies.

```json
{
  "operationId": "build-example",
  "kind": "build",
  "state": "pending",
  "message": "Request pending. Waiting does not repeat or cancel it.",
  "nextAction": {
    "tool": "operation_status",
    "arguments": {
      "instance": "Example#123",
      "operationId": "build-example",
      "waitSeconds": 30
    }
  }
}
```

The outcome vocabulary is `pending`, `succeeded`, `failed`, `cancelled`, or `unknown`.
It describes the request: a successful launch does not mean the program exited,
just as successfully installing a breakpoint does not mean the breakpoint was hit.
Unknown or newly encountered host states never default to success.

`details: true` adds bounded dispatch evidence: request identity, execution phase,
command timestamps, duplicate protection, historical-record status, and the mode /
build-busy observation. It does not expose the entire internal object graph in the
tool schema. Full journal evidence stays on disk. Routine responses do not expose
`terminal` or record-closure flags that the model must reconcile itself.

Status attempts a live observation only when needed, adding at most one second
beyond `waitSeconds`. Concurrent observations share one outstanding UI query; a
blocked UI cannot accumulate a new query for every poll. A timeout leaves the
retained outcome intact and reports unavailable evidence. Completed known results
need no UI query. This bound applies to the observation, not connection setup.

The host tracks dispatch and refreshes protection internally. It releases protection
on an unresolved `unknown` build/launch only after the command returned and a fresh
observation establishes idle. Idle cannot retire normally queued/pending commands.
Builds require the build manager idle; launches require design mode. The outcome
remains unknown, and the next action is inspection (`build_log`, `debug_state`, or
`bp_list`), never an automatic retry. An in-flight unknown command continues to
suggest waiting. Use `build_cancel` when cancellation is intended.

A retry with the original request ID still returns the original record in the same
host lifetime. A new request ID represents a new command and may repeat effects.
Historical records are read-only, and responses explicitly say that their request
IDs do not deduplicate commands in the new host. Collector uncertainty directs the
model to `profile_status` before choosing capture recovery or an explicit stop.

## Structured build diagnostics

Record a binary log using MSBuild, for example from a Developer PowerShell:

```powershell
msbuild .\Example.sln /t:Build '/bl:C:\Temp\example.binlog;ProjectImports=None'
```

Call `build_diagnostics` with that absolute `binlog` path. The shim replays events
using Microsoft.Build; it does not evaluate projects, execute embedded tasks, extract
project files, or require a running Visual Studio instance.

The report includes warning/error events, source locations, codes, project and
configuration ownership when present, event timestamps, total occurrences, and
unique warnings. `project` and `configuration` are case-insensitive substring filters
on returned rows. Counts cover the entire file; `matchingDiagnostics` and `truncated`
make the row limit explicit (default 25, maximum 1,000).

`eventStreamComplete` requires readable build-start and build-finish boundaries
without replay errors. `outcome` is `succeeded`, `failed`, or `unknown`. An incomplete/corrupt log has an unknown outcome,
even if partial diagnostics were recovered. Completeness refers to structured events;
messages emitted only as text are outside the counts.

This tool imports an existing log. It does not automatically collect binary logs from
VS builds or associate an arbitrary file with a live build operation. The normal
`build` and `build_log` path continues to retain and parse the VS output pane.
Unavailable initial pane text no longer prevents build dispatch; missing log evidence
is reported on the operation.

## Repeatable live checks

Run `pwsh -File tests/live/run.ps1` from the repository. PowerShell 7.4+, Python 3,
the .NET SDK, Visual Studio with C++ desktop tools, and the VSSDK build components
are required. Add `-Profiles` to exercise the installed CPU collector. Use
`-SkipBuild` only after building matching host/shim sources.

The harness copies the tracked C++ fixture, creates a fresh data directory, deploys
to the dedicated `CodexMcpValidation` experimental profile, and applies General
settings there to avoid first-run personalization. It refuses to reuse a running
test profile and closes only the VS process it started. Normal VS profiles and
workspace fixtures are not modified. Artifacts remain in `artifacts/live/<run-id>`.

The stdio MCP checks cover rebuild/no-op, intentional compiler failure, cancellation,
following the suggested next call while a build is active, detailed evidence, shim reconnect,
request deduplication, structured binary diagnostics, source breakpoints, launch,
structured waits, and detach. Optional profiling covers detach-interrupted collection,
stop, reconnect, recovery, and export. `results.json` stores tool replies without the
registration authentication token; `ActivityLog.xml` records VS startup diagnostics.

Automated tests cover idle/completion races, stale observation protection, retained
retry identity, model-facing outcomes and next calls, named-pipe status, and real binary logs (including filtering,
duplicates, corruption, and reading after the source project is removed).

Validated on 2026-09-09: the revised 60-tool catalog and isolated Visual Studio
2026 live suite passed, including executing `nextAction` verbatim and interrupted
profile recovery/export (`artifacts/live/17210032d66044b89302ad13f7c61ac1`). The
status output schema is approximately 1.7 KB. Release host/shim packaging and 585
automated tests pass. Registry tests also ensure unresolved protection honors
requested waits instead of causing an immediate polling loop.

## Remaining work

Live checks use Visual Studio 2026 and the small C++ fixture. Other VS versions,
project systems, modal/hung UI recovery, and late symbol binding still need coverage.
The registry tests establish queued/in-flight and unknown-outcome behavior without intentionally
hanging a live VS process. The optional profile test validates capture lifecycle,
not statistical sampling accuracy or host-crash recovery.

PDB GUID/age matching, debug-engine exception settings, CMake launch/build support,
scheduler/off-CPU tracing, and a remote bridge remain later work. A stuck in-process
COM call still cannot be safely cancelled by this protocol.
