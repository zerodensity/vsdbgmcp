# vsdbgmcp — design

A Visual Studio extension plus a small command-line shim that exposes the VS
debugger to AI agents over MCP. Debug-first, with the build tools needed to
close the edit-build-run-observe loop. C++ is a first-class target.

Status: built and working. Sections 1 to 13 are the design as intended; section
14 records what building it actually changed, and the README says what has and
has not been exercised against a live debuggee.

## 1. Goals

1. **One global client config.** Configure it once per machine and it works in
   every repository and every Visual Studio window, with no per-project setup
   and no ports to pick.
2. **Debugging that behaves like debugging.** The agent waits on debugger
   events rather than polling for state changes. No tool returns "started, poll
   me."
3. **C++ past file:line.** Data breakpoints, disassembly, crash dumps, symbol
   and bind diagnostics, natvis, format specifiers, console and Debug-pane
   output.
4. **Multi-window as a feature, not a hazard.** Several VS instances are the
   normal case. One agent session can drive more than one of them.

### Non-goals

General code editing, code navigation, UI automation, browser automation, test
running, package management. Agents already have file and search tools; the
value here is the debugger, which they have no way to reach.

Build is in scope only insofar as it serves the debug loop.

### Name

`vsdbgmcp` throughout: repository, marketplace identity, executable, namespace
root, install directory, pipe prefix. One token that says Visual Studio,
debugger, and MCP, and that collides with nothing.

| slot | value |
|---|---|
| marketplace name | VS Debugger MCP |
| executable | `vsdbgmcp.exe` |
| suggested config key | `vsdbg` |
| namespaces | `VsDbgMcp.Core` / `.Shim` / `.Host.2022` / `.Host.2026` |
| install directory | `%LOCALAPPDATA%\vsdbgmcp\` |
| pipe | `vsdbgmcp-<pid>` |

The **suggested config key is deliberately shorter than the package name.** The
MCP tool prefix comes from the key in the client's config, not from anything we
ship, so documentation suggests `vsdbg` — the model then reads
`mcp__vsdbg__wait` rather than seeing "mcp" twice in every tool name. A config
key is not a filename, so it does not collide with Microsoft's `vsdbg.exe`
debugger backend the way naming our executable that would.

## 2. Architecture

```
MCP client (Claude Code, Cursor, ...)   one global config, spawned per workspace
      | stdio
vsdbgmcp.exe  (shim, self-contained)    MCP protocol, tool surface, discovery,
      |                                 routing, aggregation
      | JSON-RPC over named pipe, one per connected VS instance
VS extension (VSIX, inside devenv)      IDebugHost + IProjectSystem
                                        over DTE and IVsDebugger
```

### The client spawns the shim; the shim finds VS

Not the reverse. This is what makes the configuration global: there is no port
or URL for the client to know, it works with clients that speak only stdio, and
several clients can attach to the same VS instance independently.

It also keeps process lifetime simple. The shim is a child of the MCP client and
ends when it does; the extension lives and dies with devenv; the named pipe
drops when either end goes away.

Closing stdin is what normally ends the shim, but it is not enough on its own.
A client that is killed outright, or that replaces its own image during an
update, never closes it — one was found still running against a parent that had
become `claude.exe.old.<timestamp>`, holding its files open and a connection to
Visual Studio that nothing was driving. So the shim also watches its parent
process directly, which is the one signal that survives every way a client can
disappear.

### How the shim gets there

The shim rides inside the VSIX and the extension copies it to
`%LOCALAPPDATA%\vsdbgmcp\bin` when it loads. Installing the extension is the
whole installation.

The path matters more than it looks. An agent's configuration names the shim by
absolute path, written once and globally, and Visual Studio installs extensions
into a folder it regenerates on every update — so that path cannot be the one
inside the extension without breaking at the next version. It cannot be a
package manager either: `dotnet tool` would be the natural channel for a .NET
executable, but it needs the .NET SDK, and the audience this is built for is C++
developers whose Visual Studio has no .NET workload at all. For the same reason
the shim is published self-contained rather than framework-dependent: a machine
with Visual Studio and no .NET runtime still gets a working shim, at the cost of
about 35 MB of package.

Three rules keep the copy honest:

- **Never downgrade.** Two Visual Studio versions stage to the same directory, so
  the copy only happens when the bundled build is newer than what is there.
  Otherwise the older installation would clobber the newer one on every launch.
- **The executable goes last.** Until it does, an agent launching mid-copy finds
  either the previous shim or nothing, never a half-written one.
- **A running shim is renamed, not overwritten.** Windows will not overwrite a
  loaded image but will rename one, so the old file moves aside and is deleted on
  a later start once nothing holds it.

### MCP lives in the shim, not in devenv

- **Dependency isolation.** Anything loaded into `devenv.exe` competes with
  Visual Studio's own assembly versions, and the problem compounds across two
  VS majors. The in-proc payload here is COM interop and a named pipe. The shim
  is a self-contained executable free to use current .NET and any dependency it
  likes.
- **Iteration speed.** A tool-surface change ships by replacing one executable.
  In-proc it would mean rebuild the VSIX, reinstall, restart VS. The tool
  surface will change many times before it settles.
- **Two VS versions get cheap.** The only per-version code is the host adapter.

### Contracts

Two interfaces cross the pipe. Both are small and version-negotiated.

**`IDebugHost`** — everything about debugging. Project-system agnostic, because
`IVsDebugger` does not care how the process was launched.

    session      launch, attach, detach, terminate, restart, openDump
    execution    go, pause, step, runTo, setNext
    events       subscribe -> break, exception, moduleLoad, processExit, output
    breakpoints  set, remove, enable, list (with bind state)
    inspection   threads, stack, frames, eval, expand, memory, registers,
                 disasm, modules
    io           consoleRead, consoleWrite, outputRead
    capture      windowCapture

**`IProjectSystem`** — build, launch targets, startup selection, configuration.
This is the only interface a non-solution workspace ever needs a second
implementation of.

At handshake the instance advertises a contract version and a capability list
(`native`, `managed`, `dataBreakpoints`, `disasm`, `dumps`, `consoleIo`,
`windowCapture`). The shim filters and annotates the tool surface from it, so
an older extension keeps working against a newer shim, degraded rather than
broken. People update a command-line tool far more often than an extension.

## 3. Discovery and routing

Each VS instance writes `%LOCALAPPDATA%\vsdbgmcp\inst-<pid>.json`:

```json
{
  "pid": 42696,
  "pipe": "vsdbgmcp-42696",
  "token": "<random>",
  "vsVersion": "17.14.3",
  "contract": 1,
  "workspace": {
    "kind": "slnx",
    "root": "D:\\Repos\\proj\\Project",
    "file": "D:\\Repos\\proj\\Project\\App.slnx",
    "filter": null
  },
  "projectDirs": ["..."],
  "capabilities": ["native", "managed", "dataBreakpoints", "disasm"],
  "debugMode": "break",
  "startedAt": "2026-08-18T10:12:03Z"
}
```

There is no broker daemon. The shim reads the directory on demand, probes
liveness, and prunes dead entries. A daemon would be one more thing to install,
upgrade, and crash; a directory of files is self-healing at no cost.

Reconnection is keyed on identity, not port or pipe name, so a VS instance that
restarts is picked back up on the next call without the agent noticing.

### Routing rules

The shim is spawned by the client in the workspace root, so **the working
directory is the routing key**.

1. Longest-ancestor match of the shim's cwd against each instance's
   `workspace.root` and `projectDirs`. The nearest enclosing workspace wins.
2. Exactly one match — bind silently. This is the common case and needs no
   configuration at all.
3. Failing that, the reverse: a workspace that sits *under* the working
   directory. An agent started at a repository root whose solution lives in a
   subdirectory is common enough to handle, but only when exactly one instance
   qualifies.
4. Several matches at either tier — **never guess.** The error is the fix: it
   names the candidates with the literal values to pass.
5. No match — report which instances are running and what they have open.

The ambiguity error is part of the design, not an afterthought, because it is
what lets an agent recover in a single round trip:

```
Several instances match this directory. Pass instance= to choose:
  Engine#42696   D:\repo\Engine   Engine.sln    break
  Editor#51120   D:\repo\Editor   Editor.slnx   run
```

### Workspace identity

**Match on directory, never on solution file path.** This single rule sidesteps
`.sln` / `.slnx` / `.slnf` identity entirely and is the most important detail
in this section.

The failure it avoids: a directory holds both `App.sln` and `App.slnx` — what a
solution mid-migration looks like — and VS has the `.slnx` open. Any scheme
that walks the filesystem for a solution file and compares paths for equality
finds the `.sln`, fails to match, and silently routes somewhere else. Comparing
directories cannot fail this way.

Supporting rules:

- Glob `*.sln`, `*.slnx`, `*.slnf` as **three explicit patterns**. .NET's
  three-character-extension quirk, where `*.sln` also matches `.slnx`, depends
  on 8.3 short names existing on the volume. Measured on Windows 11 with
  .NET 10: it does not fire. It is a coin flip across machines and must never
  be relied on.
- Collapse `App.sln` and `App.slnx` in one directory into a single candidate.
  They are the same solution. **The instance is the authority on which file is
  actually open, not the filesystem.**
- **Solution filters.** Large C++ repositories use filters heavily, and two
  windows can hold the same `.sln` under different filters — a legitimate
  multi-window case. Instance identity carries the filter where it can be
  found, and routing matches any cwd under the parent solution's directory to
  both, which lands in the ambiguous rule and asks.

  Measured limitation: Visual Studio reports the `.sln` a filter was opened
  from, and the 17.x SDK exposes no property for the filter path itself. So the
  two windows are told apart by process id rather than by filter name. Routing
  still refuses to guess between them, which is the property that matters; only
  the label is missing.

### Addressing from the tool layer

- `instances()` lists them. Ids are human-typable and stable: `App#42696`.
  Unambiguous prefixes are accepted.
- `use(instance)` sets a sticky default **for the MCP session**, not globally
  and not on disk.
- Every tool takes an optional `instance` to override for a single call.

Because the shim holds several connections at once, one session can drive more
than one VS window — breakpoints in an editor process and an engine process,
with `wait()` racing across both and reporting which one stopped.

## 4. Tool surface

Two principles.

**Every tool returns a decision-complete answer, not a raw API dump.** A tool
surface that mirrors the debugger UI one control at a time is expensive to
drive: the agent spends its context reassembling a picture the debugger already
had. Aggregate where the question is an aggregate one.

**Tools that exist only because nothing blocks or nothing aggregates are not
built.** No status-polling tool, no build-progress tool, no per-thread variant
of a tool that should take a thread argument.

Names are short and unprefixed; MCP clients namespace by server already.

**Session** (2)
`instances()`, `use(instance)`

**Lifecycle** (8)
`status()` — instance, workspace, mode, current thread and frame, top frames,
pending exception, breakpoints hit, pinned watch values. One cheap call.
`launch(project?, args?, env?, stopAtEntry?, noDebug?)`,
`attach(pid | nameRegex)`, `detach(process?)`, `stop(process?)`, `restart()`,
`processes()`, `dump_open(path)`

**Execution** (6)
`wait(timeout, for?, instance?)`, `go()`, `pause()`, `step(kind, count?)`,
`run_to(file, line)`, `set_next(file, line)`

**Breakpoints** (6)
`bp_set(...)` — file:line, or function with optional module, or data address
and size; plus condition, hit count, and log message for tracepoints.
`bp_list()`, `bp_remove(id)`, `bp_enable(id, on)`,
`trace_read(id, tail?)`, `exceptions_set(category, code, breakOn)`

**Inspection** (12)
`threads(depth?)`, `stack(thread?, count?)`, `select(thread?, frame?)`,
`freeze(thread, on)`, `eval(expr, opts)`, `vars(scope, depth, filter)`,
`expand(ref, depth)`, `watch_set(exprs[])`, `memory(addrOrExpr, size, format)`,
`registers(group?)`, `disasm(addr?, count)`, `modules(filter?)`

**Evidence** (2)
`triage()`, `capture(region?)`

**Debuggee I/O** (3)
`console_read(tail?)`, `console_send(text | keys)`,
`output(pane, pattern?, tail?)`

**Build** (5)
`build(mode, project?, config?, platform?)` where mode is build, rebuild, or
clean. `build_cancel()`, `build_output(pattern?)`, `config(get|set)`,
`startup_project(get|set)`

**44 tools.** The ceiling is 50 — past that, an addition has to displace
something. The count is a constraint, not an outcome.

## 5. Debug semantics

### Waiting is a first-class operation

`wait(timeout)` blocks on real debugger events and returns **why** execution
stopped:

```json
{
  "instance": "Engine#42696",
  "reason": "exception",
  "exception": { "code": "0xC0000005", "name": "Access violation",
                 "address": "0x7ff6...", "firstChance": false },
  "thread": 15224,
  "frame": { "function": "Mesh::Upload", "file": "mesh.cpp", "line": 218,
             "module": "engine.dll" },
  "watches": { "m_state": "Uploading", "count": "0" }
}
```

Other reasons are `breakpoint` (with id, file, line), `step`, `entry`, `pause`,
`exited` (with exit code), and `timeout`.

Mode-level events (`OnEnterBreakMode` and friends) are enough to know that
execution stopped, but not why. The richer reason comes from
`IDebugEventCallback2`, which is the reason the extension implements the event
callback rather than settling for DTE events.

Every long operation follows the same rule. `build` blocks to completion.
`launch` blocks until the process is up or has already stopped. Polling is a
failure of the tool, not a technique for the agent.

The one thing worth waiting for that is not a stop is a module arriving:
breakpoints in a plugin sit unbound until its host loads it.
`wait(for: "module:NAME")` returns on that instead, and answers straight away if
the module is already loaded. Module loads are buffered in a stream of their own,
so a `wait` for a stop is never woken by one — the whole agent loop reads a
returning `wait` as "the debuggee stopped".

### Evaluation must not silently run the program

The native expression evaluator will call functions inside an expression if
allowed to. `eval` defaults to **no function evaluation**. Side effects require
an explicit `allowSideEffects: true`, which keeps the risk visible at the call
site rather than buried in a separate tool.

### Pinned expressions

`watch_set(exprs[])` replaces the whole pinned set in one idempotent call, and
those values come back inside `status()` and inside every `wait()` result. One
call at the top of a debugging loop removes several evaluations per stop.

### Mutating vs read-only

Read-only tools are unrestricted. `set_next`, `stop`, `terminate`, variable
assignment, and `eval` with side effects are marked destructive and are the
candidates for an approval mode later.

## 6. C++ as a first-class target

Not a longer tool list — a different one.

- **Natvis is the point.** Evaluate through the visualizer path by default and
  expose `raw: true` for `,!`. Pass format specifiers (`,x`, `,d`, `,[n]`,
  `,su`) as a `format` parameter rather than making the model splice them into
  the expression string.
- **Data breakpoints.** `bp_set` with an address or expression and a size. C++
  only, and the best single tool for memory corruption.
- **Tracepoints with a stream of their own.** `bp_set(logMessage: ..., collect:
  true)` marks its records with the breakpoint's id, and `trace_read` returns
  that one breakpoint's records, numbered and in order, with the rate they
  arrived at. In the shared Debug pane a tracepoint on a hot path buries the
  program's own logging and leaves cadence to be guessed at from how the two
  interleave.

  Visual Studio prints a tracepoint's record itself rather than raising it as
  debuggee output, so the debug event callback never sees one; the pane is the
  only place a record exists. Where that pane is a text buffer it is watched and
  each record is timed as it lands. Where it is not — Visual Studio 2026 among
  them — the records are recovered from its text afterwards: complete and in
  order, but undated, and the reply says which of the two it is rather than
  presenting a guess as a measurement.

  Each `{expr}` is also evaluated once at bind time, because an expression that
  will not evaluate otherwise announces itself only after a thousand records have
  said so — and only where the tracepoint sits, since anywhere else the answer
  would be about the debugger's position rather than the expression.
- **Allocator fills are named where they appear.** `0xdddddddddddddddd` in a
  value, or a run of it in a `memory` dump, is called freed heap, and the same
  for the rest of the table. Only a whole number that is nothing but the fill
  counts: a wrong "this was freed" sends the reader further off than silence.
- **Symbol truth.** `modules()` reports PDB load state, search path, and when each
  binary was built. Unbound breakpoints report *why*: module not loaded, no
  symbols, or a source file written after the module was built. This is where
  native debugging actually fails, and reporting "breakpoint set" when it will
  never bind is worse than reporting nothing.
- **Crash dumps.** `dump_open(path)` and every inspection tool works unchanged.
  Cheap to support, and it makes the server useful for triage with no live
  process at all.
- **`triage()`** — one call on an unhandled access violation: exception record,
  faulting thread stack, relevant registers, memory around the fault address,
  module and symbol state. Decision-complete in one page.
- **`threads(depth=3)`** — every thread's top frames, grouped and deduplicated.
  A deadlock becomes visible in one call instead of forty.
- **Concurrency.** `freeze(thread, on)` isolates a race by running one thread
  at a time. `eval(expr, allThreads: true)` compares one expression across a
  worker pool.
- **The Debug output pane is a diagnostic surface.** Module loads, PDB
  failures, first-chance exception notices, and every `OutputDebugString` land
  there. `output(pane, pattern)` treats it as a primary source.
- **Console programs need their stdio.** `console_read` and `console_send`
  reach the debuggee's console buffer. Without them a large share of C++
  debugging is done blind.
- **`capture()`** screenshots the debuggee window using Windows Graphics
  Capture, which keeps working while the process is stopped and while the
  window is occluded. This is evidence collection for a stopped process,
  adjacent to `triage()` — it is not the UI automation ruled out in section 1.
- **Several processes at once are the normal case**, not an edge case: a
  launcher and what it starts, a host and its workers, mixed-mode and
  child-process attach.

  Thread ids are the addressing primitive, because Windows makes them unique
  across processes — `select(thread: 4242)` needs no new concept and carries the
  process with it. `threads` spans every process by default and names the owner
  of each group, since that is the only way an id in another process becomes
  discoverable. `select(process: …)` takes a pid or part of a name for when you
  know which process you want but not a thread. `wait` says which process
  stopped, because in a two-process session "stopped: breakpoint" is half an
  answer.

  A selection lasts until the program next runs. Frames do not survive their
  thread resuming, so a pinned selection that outlived a resume would quietly
  evaluate later expressions in a process the caller had stopped meaning to look
  at — the worst failure available, because nothing about the answer looks
  wrong.

## 7. Build

Build serves the debug loop and nothing else.

`build()` blocks to completion and returns deduplicated Error List entries with
file and line, ranked, capped, and with a count of the remainder — not the raw
output pane. `build_output(pattern?)` is there for when the raw pane is what
you actually need. `build_cancel()` exists because a hung build with no cancel
is a stuck agent.

Configuration, platform, and startup project selection are included because
they change what `launch()` means.

## 8. Visual Studio 2022 and 2026

Both are targets, and **one VSIX covers both**. This supersedes the earlier plan
of shipping a package per version, on evidence gathered while building it:

- `devenv.exe` is still .NET Framework in 2026 as well as 2022. There is no
  runtime split to straddle.
- There is no 18.x `Microsoft.VisualStudio.SDK` metapackage. A second project
  would reference the same 17.x package and produce an identical assembly.
- The single package builds with the MSBuild from either installation, and
  `[17.0,19.0)` covers both.

Two packages were the hedge against a runtime or SDK split that turned out not
to exist. If one appears, the seam is still there: only the host implementation
would fork.

```
VsDbgMcp.Core   netstandard2.0, no VS references. Contracts, routing, discovery
VsDbgMcp.Shim   .NET exe, self-contained: MCP, routing, aggregation
VsDbgMcp.Host   VSIX. Compiles Core's sources in rather than referencing them,
                so one assembly loads into devenv instead of two
```

Keeping the in-proc part thin is still the hedge: if VS 2026 moves, only the
host moves with it. In-proc code stays .NET Framework compatible; the shim is
free to target current .NET.

The extension model must be the in-proc VSSDK. The out-of-process extensibility
model is pleasanter to work in but does not expose the debugger.

## 9. Reliability

- **COM re-entrancy is the main source of flakiness in VS extensions.** While
  the debuggee is in break mode, DTE calls throw `RPC_E_CALL_REJECTED` and
  `RPC_E_SERVERCALL_RETRYLATER`. Install an `IOleMessageFilter` and retry with
  backoff on every hop, from the first commit. This is not a later hardening
  pass; retrofitting it means touching every call site.
- The pipe listener runs on a background thread. Only the specific COM call
  hops to the UI thread, via `JoinableTaskFactory`. No long operation ever
  occupies the UI thread.
- COM event sinks must be rooted in a field. An unrooted `DebuggerEvents` sink
  is collected and the events stop arriving, intermittently and much later.
- **Focus stealing.** VS takes the foreground on launch and on break, which is
  miserable when an agent is driving and the person is working in another
  window. Note what held the foreground and put it back afterwards. Windows only
  grants the foreground to a thread that already has it, so the restore borrows
  the holder's input queue for the length of the call.

  **The guard has to know who caused the stop.** The first version armed on
  every agent call and never disarmed, so after any tool call it would fire on
  the next mode change whoever caused it — taking focus away from the person the
  moment they pressed F10, on every step. It now arms only on an agent command
  that resumes execution, fires once, and disarms when the program next stops. A
  launch brings the IDE forward twice, at start and at the following stop, so
  entering run mode restores but stays armed.
- If devenv exits, connections drop and the shim reports the instance as gone
  rather than hanging.

## 10. Being visible from inside

A server that drives your debugger from outside has to be visible and stoppable
from inside. Something running in your IDE that you can neither see nor switch
off is not something to leave enabled.

A docked panel under **Extensions → Debugger MCP Server** shows whether the
server is listening, which instance it is bound to, how many clients are
attached, and the last 200 calls with their durations. It opens by itself the
first time an agent attaches, without taking focus.

**Pause is a kill switch.** Every tool checks it and refuses with an explanation
saying a person stopped it, so an agent gets a reason rather than a hang.

The kill switch works because every tool already passes through one helper on its
way to the UI thread — the same choke point that switches threads and installs
the message filter, which is why there is one place to refuse.

**The audit trail is reported by the shim, not recorded in the extension.** The
text an agent was given only exists on the shim side, so anything the extension
assembled itself would be a second rendering of the same result — and would show
the reader something the agent never saw. The shim reports each call once it has
the reply, one way and never awaited, so a report that cannot be delivered costs
a panel entry and nothing else. Against an older extension with no method to
receive it, the tools keep working and the outputs simply do not appear.

That also removes a guess: the shim knows the tool name as the agent typed it,
so the panel no longer has to reconstruct `bp_set` from `BreakpointSetAsync`.

Each entry folds open to show the reply, and carries the one argument worth
reading beside the name — the expression, the file and line, the process. A row
without a reply stays a plain line rather than offering a chevron that opens on
nothing. Rows are added incrementally rather than redrawn, because a redraw would
collapse everything the reader had opened every time an agent made another call.

## 11. Security

The transport is a local named pipe, not a socket. The discovery file carries a
per-instance token and is ACL'd to the current user. Every call is
token-checked.

This server can execute code inside a debuggee. That is the entire point, and
it is also why nothing here binds a network interface at any point.

## 12. Milestones

Risky unknowns first, so the architecture is proven before the surface widens.

1. **Shim, discovery, directory-based routing (sln/slnx/slnf), `status()`,
   `wait()`. VS 2022 only.** This is where COM re-entrancy and the event
   callback either work or do not.
2. Breakpoints with real bind diagnostics, `stack`, `select`, `eval` and
   `vars` with natvis, `watch_set` feeding the stop report.
3. Build with structured errors, `build_cancel`, `output`, console I/O — the
   point at which the edit-build-run-observe loop closes without a human in it.
4. Memory, registers, disassembly, data breakpoints, dumps, `freeze`,
   all-threads `eval`, `triage()`, `capture()`.
5. VS 2026 host adapter, then CMake `IProjectSystem`.
6. Focus guard, status UI, audit log.

### Milestone 1 acceptance

| | |
|---|---|
| Two instances, different solutions: the right one binds with no configuration | covered by tests |
| Two instances, same solution under different filters: routing asks and names both | covered by tests |
| A directory holding both `App.sln` and `App.slnx` routes correctly | covered by tests |
| An agent started above the solution directory still finds it | covered by tests |
| `wait` returns on a pushed stop with the reason, and never polls | covered by tests |
| A stop that happened between two `wait` calls is still delivered | covered by tests |
| Resuming discards stops already reported | covered by tests |
| The token from the discovery record is checked at handshake | covered by tests |
| A dead instance's record is pruned; a corrupt one is ignored | covered by tests |
| VS restarts; the next call reconnects silently | needs a running Visual Studio |
| A call issued during break mode does not throw an RPC rejection | needs a running Visual Studio |
| A breakpoint that will not bind reports why | needs a running Visual Studio |

The tests reach as far as a stand-in for the extension: real discovery files, a
real named pipe, real JSON-RPC, real rendering. What they cannot reach is
Visual Studio's own behaviour, which is where the last three live.

## 13. Deferred

- **CMake and Open Folder.** Routing is free: keying the discovery record on a
  workspace root instead of a solution file costs one field and no branch. The
  debug core is free, because `IVsDebugger` is project-system agnostic. Only
  build and launch double, against `Microsoft.VisualStudio.Workspace` and
  `launch.vs.json`, which is thinner ground than the solution APIs. Deferring
  keeps a second adapter out of the highest-risk area until the core is proven.
  `attach()` works regardless, so a CMake user can build outside VS and still
  get the whole inspection surface on day one.
- **An HTTP transport on the shim, alongside stdio.** Today a client has to be able
  to spawn a Windows executable, which rules out WSL, dev containers and agents
  on another machine. The shim is where this belongs rather than the extension:
  it is net10, so `ModelContextProtocol.AspNetCore` loads there and cannot load
  into devenv at all, and the tool classes are transport-agnostic — `Program.cs`
  names the transport in one line and the tools in eight more. `--cwd` and
  `--instance` already exist, which is what an HTTP server needs, having no
  working directory of its own to route from.

  Three things have to come with it. A bound port is reachable by every local
  process including browsers, so 127.0.0.1 only, `Origin` validation, and the
  discovery token required in a header — not optional, and not defaulted on.
  Nothing spawns an HTTP server, so `ParentWatch` no longer bounds its life and
  it needs an idle timeout or it becomes the orphan problem again. And ASP.NET
  Core inflates a self-contained publish, so the HTTP-capable build probably
  ships separately rather than enlarging the VSIX for everyone.

  Deferred rather than dropped: it is purely additive. No contract changes, no
  tool changes, and nothing about it gets harder by waiting.
- Managed-specific tooling: TPL task lists, async call stacks.
- Approval mode for destructive tools.
- Memory writes.
- Remote and WSL debugging targets.

## 14. What building it changed

Recorded so these are not rediscovered later.

**The extension uses the legacy project format.** The VSIX packaging tasks are
.NET Framework assemblies, so only the MSBuild inside Visual Studio can run
them. That MSBuild can only resolve `Microsoft.NET.Sdk` when the .NET SDK
component is installed *into* Visual Studio, which the extension development
workload does not bring with it. Requiring it of anyone who builds this would
be a poor trade for project-file tidiness.

**Core is compiled into the extension rather than referenced.** A consequence
of the above, and an improvement: one assembly loads into `devenv.exe` instead
of two. Core stays a real project for the shim and the tests.

**Core has no JSON dependency at all.** The discovery record is written by hand
from inside `devenv.exe`, where every dependency competes with Visual Studio's
own assembly versions, and read with System.Text.Json in the shim, which is
free to use anything. A test round-trips the hand-written output through a real
parser, because the two halves are otherwise only assumed to agree.

**`ProductArchitecture` is a child element, not an attribute.** The packaging
task rejects the manifest without it and the error does not say which form it
wants.

**VSTHRD010 is suppressed, with the reason recorded in the project file.** It
fires on lambdas because it analyses each as its own context and cannot see
that the enclosing helper already switched threads. Every automation call goes
through one of two helpers that await `SwitchToMainThreadAsync` and install the
message filter first; nothing reaches the DTE by another path. The rest of the
threading rules stay on.

**Expression evaluation goes through the debug engine, not the automation
model.** `EnvDTE.Debugger.GetExpression` always permits function evaluation,
which is what makes inspecting `v.size()` able to change the program. Parsing
through `IDebugExpressionContext2` and evaluating with `EVAL_NOFUNCEVAL` and
`EVAL_NOSIDEEFFECTS` is what makes the safe default possible at all.

**Expansion is stateless.** `expand` re-evaluates the full name a previous reply
returned rather than holding a handle, so there is no table to grow, invalidate
across stops, or leak.

**One program is not the session.** The first version kept the program the last
event came from and enumerated threads only from that, which made every thread
in every other process invisible — a caller could see a launcher listed by
`processes` and still be told its thread ids did not exist. The engine reports
programs one event at a time, so they have to be accumulated and forgotten
individually; a program ending must not clear the others, because the rest of
the session may still be stopped and worth inspecting.

**An id that does not resolve should say which ones do.** `select` and `stack`
now append every thread in the session grouped by process when they cannot find
what was asked for. That single line of output is the difference between a dead
end and the next call, and it is the same principle as the routing errors.

**Ask the engine whether an event stops execution.** The first version decided
for itself which events were stops: it treated any non-first-chance exception as
one and the entry point as one. Both were wrong in practice. An unhandled access
violation came through flagged first-chance and so was never reported, meaning
`wait` timed out on the very crash the agent was waiting for, while the entry
point *was* reported and made every launch look like it halted on the first line
of `main`. The `attributes` argument the engine passes to the callback says which
events stop, and using it fixes both. The entry point is then excluded
separately, because the engine raises it as stopping and immediately continues.

**Registers are read as `@rax` pseudo-variables.** The property enumeration that
is meant to expose register groups returns nothing from the native engine.

**Exception settings are not reachable through automation.**
`DTE.Debugger.ExceptionGroups` is empty on VS 2026, so `exceptions_set` cannot
work as written and reports that rather than guessing. Doing it properly means
going through the debug engine, as expression evaluation already does.

**`Breakpoints.Add` sometimes returns an empty collection having created the
breakpoint anyway**, so the result is confirmed by looking for it rather than
trusted.

**C++ debugger arguments live on the VC configuration**, reachable only through
`VCProject.Configurations`, not through the automation `Configuration` or its
`Properties`. Setting them silently failed until that was found, which is why
`launch` now fails loudly when arguments cannot be applied: launching a debuggee
down a different path than the caller asked for is worse than not launching.

**Error handling.** Catches guarding pure logic were removed; the debug
interfaces report failure through HRESULTs and have nothing to catch. What
remains is confined to three kinds of place: file and pipe I/O, the boundary
that turns a failure into text for the agent, and one helper that reads a single
automation property and logs when the shell refuses — because `status` is more
useful degraded than failed. Nothing swallows an error without saying so.

## 15. Open

- Tool naming convention. Short and unprefixed reads well, but it is worth
  revisiting once there are real transcripts to look at.
- Whether `status()` should carry a compact recent-events tail, or whether that
  belongs only in `wait()`'s return value.
