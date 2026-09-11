# VS Debugger MCP

Drives the Visual Studio debugger from an AI agent, over the Model Context Protocol.

Debug-first. C++ is a first-class target: data breakpoints, disassembly, crash dumps,
symbol diagnostics, sampling profiles, and the debuggee's own console. Several Visual
Studio windows can be driven from one agent session.

## What it is for

**Waiting works.** `wait` blocks on the debugger's own events and reports *why* execution
stopped — which breakpoint, which exception, a step completing, the process exiting.
A timeout reports that no event arrived, alongside a timestamped state observation;
it does not claim the program is running or making progress. Structured responses
also distinguish a new stop from one the debugger is still sitting at.

**Long operations keep their identity.** `build`, `launch`, and `bp_set` return a result
or a pending operation ID after a bounded wait. `operation_status` can read or wait for
that operation even when Visual Studio's UI is busy; a live check adds at most one
second. Retrying with the same
`requestId` and arguments reuses the request within the same host session. Build logs
remain available for each invocation, and diagnostic counts describe the captured build
output, so an old Error List entry cannot decide whether the new build succeeded.

**One configuration, every window.** The agent launches the shim in its working
directory; the shim finds the Visual Studio that has the matching solution open and
connects. No ports, no per-project setup. When the choice is ambiguous the error names
the candidates and the exact value to pass, so the next call succeeds.

**C++ gets real tools.** A breakpoint that will never bind says so and says why. `triage`
answers a crash in one call. `bp_set` can watch an address for writes. `console_read`
reaches the debuggee's own stdout.

**Native screenshots.** `capture` returns the debuggee window as MCP
PNG image content with a compact dimensions summary for image-capable clients.
An optional `region` requests a crop in window-relative pixels. Capture failures
return text with `isError: true`.

**A reply never claims more than it found.** A breakpoint that cannot report a hit count
says so rather than printing zero. A profile with too few samples refuses to rank them.
A module without symbols names the call that would name its functions. Where an answer
is folded or cut short, the reply says which argument unfolds it — an agent is given one
reply and cannot expand a tree, so a dead end costs it the whole question.

## Profiling an agent can read

`profile_start` and `profile_stop` drive Visual Studio's own sampling collector against
a process the debugger is **already** attached to, so a profile is something taken
during a debug session rather than instead of one.

The first report is compact. `profile_report` can then show who calls a hot function,
which source lines the samples landed on, a call tree, a roll-up per binary, or what
moved since an earlier capture. Module and thread filters combine with the selected
view; `focus` selects a subtree and `details` adds coverage and capture metadata.

Each capture has a stable ID, owner process, session information, timestamps, and
debugger intervention markers. Results persist across reconnects. `profile_status`
lists saved captures, `profile_recover` reads a completed package after an interruption,
and `profile_recover_stop` explicitly stops a retained collector session before recovery.
`profile_export` writes structured JSON, a readable report, and an optional raw trace.

Raw traces are deleted only after the aggregate is saved successfully, unless raw
retention was requested with `profile_start(retainRaw: true)` or recovery needs the
evidence. Retention limits are configurable. Late-loaded module symbols are added to
the capture's saved catalog when available.

Function names come from the symbols the debugger has already loaded for the session, so
there is no wait on a symbol server for modules nobody asked about. And because CPU
sampling cannot see a thread that is blocked, reports distinguish sampled CPU work from
elapsed time and expose available coverage information. Few samples do not establish
that nothing was slow, or identify what a thread was waiting for.

## Setup

Install the extension and restart Visual Studio. It copies a small server executable to
`%LOCALAPPDATA%\vsdbgmcp\bin` on startup — no .NET runtime needed, nothing else to
download.

Then point your agent at it, once, globally:

```powershell
claude mcp add -s user vsdbg -- "$env:LOCALAPPDATA\vsdbgmcp\bin\vsdbgmcp.exe"
```

**Extensions → Debugger MCP Server** shows the exact command with the path filled in,
and copies it to the clipboard. That is the whole setup; every repository and every
Visual Studio window works from it.

## Seeing what the agent is doing

Something driving your debugger from outside should be visible from inside, and
stoppable. The panel docks beside Solution Explorer and appears on its own the first
time an agent attaches, without taking focus.

Every call is listed with how long it took and the argument worth reading — which
expression, which file and line, which process. Each one folds open to show exactly what
the agent was given back, as selectable text.

- **Pause** is a kill switch. Every tool then refuses with an explanation telling the
  agent a person stopped it, until Resume.
- **Don't steal focus** puts the window you were using back in front when an *agent*
  starts, resumes or steps the program. Stops you cause yourself are never touched.

## What it exposes

Installing this lets a local process drive your debugger: set breakpoints, start and
stop the program, read memory and registers, evaluate expressions — including ones with
side effects, when the caller explicitly asks for them — take memory out of the
debuggee's heap, and sample its CPU use. Each Visual Studio listens on its own named
pipe, guarded by a token written to a file only your account can read, and nothing is
reachable from the network. The panel shows every call as it happens and can stop them
all.

## Tools

60 tools, grouped as: session, lifecycle, execution, breakpoints, inspection,
profiling, evidence, debuggee I/O, and build.

Worth knowing about:

- **`wait`** — pass `instance: "any"` to return as soon as *any* connected window stops,
  which is how you debug a client and a server at once. `for: "module:NAME"` waits for a
  module to load, which is how to arm breakpoints in a plugin before its host loads it.
- **`operation_status` / `operations`** — find a retained operation and follow its
  outcome after the original call stopped waiting. `wait(for: "operation:ID")` can
  wait for it too. `operation_status` returns a compact outcome and suggested next
  tool call; `details: true` adds dispatch evidence.
- **`build_log`** — read one build's saved output incrementally, even after another
  build has run. Parsed warning occurrences and unique warnings are reported separately.
- **`build_diagnostics`** — import structured warning/error events from an existing
  MSBuild `.binlog`, with project/configuration ownership and explicit completeness.
  This offline tool does not rebuild or require a running Visual Studio instance.
- **`debug_state`** — a bounded snapshot of debugger mode, registered processes,
  session generation, and active and retained captures, without evaluating watches.
- **`frame`** — the current location, source and module checks, arguments, locals,
  and suspect values in one reply. It takes a second reading of doubtful values before
  reporting what could not be established.
- **`eval`** — refuses to call functions unless you pass `allowSideEffects`, because the
  native evaluator really runs them.
- **`scratch`** — the evaluator will not invent a temporary, so a function with a
  reference out-parameter cannot be called at all. This takes a block of the debuggee's
  own heap and hands back the address with the cast to paste into `eval`.
- **`bp_set`** — with `dataExpression`, breaks when the memory at an address changes.
  The best tool there is for finding what corrupts a value.
- **`trace_read`** — a tracepoint gets a stream of its own, numbered and in order, with
  the rate it arrived at. That is what makes a 50 Hz callback readable.
- **`modules`** — which binary each module actually *is*: the time stamped into the
  loaded image, its path, size and load address, and the symbol file it found. The
  image's own stamp is what answers whether a binary deployed to another machine is the
  one you just built.
- **`triage`** — after a crash: exception record, faulting stack, registers, memory at
  the fault address, and which modules were missing symbols. One call.
- **`threads`** — every thread's top frames, grouped, across *every process in the
  session*, so a deadlock is one call away.
- **`select`** — switch to another thread or another process, by pid or part of its
  name. Inspection follows it across the process boundary.

## Requirements

Visual Studio 2022 or 2026, 64-bit, Community, Professional or Enterprise. Profiling
needs the profiling tools component, which most installations already have; where it is
missing, `profile_start` says so and names what to install.

## Known limits

- `exceptions_set` does not work. `DTE.Debugger.ExceptionGroups` returns nothing on
  Visual Studio 2026, so there is no category to configure. The tool says so plainly
  rather than pretending.
- Profiling is CPU sampling only. Time spent blocked is invisible to it, which every
  profile says. Scheduler/off-CPU tracing, allocation, and file I/O collectors are
  not wired up. Intervention markers do not measure how long the target was paused.
- An operation timeout does not cancel a build or interrupt a COM call stuck inside
  Visual Studio. `operation_status` retains uncertainty and suggests the next tool
  call. Fresh idle evidence may release duplicate protection, but does not prove
  success or make repeating the command safe. Historical records are read-only.
- Live build diagnostic counts cover recognized output, not every possible compiler
  or localized message. `build_diagnostics` can read a separately recorded binary
  log; counts cover structured events in that file. Text-only diagnostics remain
  outside those counts, and VS builds do not automatically record a binary log.
- A PDB's GUID and age are not reported; the debug interfaces do not expose them. The
  image's own time stamp and the engine's verbose symbol search answer the same question
  by another route.
- CMake and Open Folder workspaces are not supported for build or launch. `attach` works
  regardless, so the inspection surface is available there today.
- Two windows holding the same solution under different solution filters are told apart
  by process id, because Visual Studio reports the `.sln` a `.slnf` filters and exposes
  no property for the filter itself. Routing still refuses to guess between them.
- A function breakpoint must match how the symbol is actually named; one in an anonymous
  namespace does not bind under its bare name. The reply says it did not bind and where
  to look.
- Only clients in the same Windows session can use this, because the client spawns the
  server itself. WSL, dev containers and remote agents cannot.

## Source

[github.com/zerodensity/vsdbgmcp](https://github.com/zerodensity/vsdbgmcp) — MIT.
