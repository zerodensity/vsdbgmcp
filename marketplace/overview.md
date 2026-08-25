# VS Debugger MCP

Drives the Visual Studio debugger from an AI agent, over the Model Context Protocol.

Debug-first. C++ is a first-class target: data breakpoints, disassembly, crash dumps,
symbol diagnostics, sampling profiles, and the debuggee's own console. Several Visual
Studio windows can be driven from one agent session.

## What it is for

**Waiting works.** `wait` blocks on the debugger's own events and reports *why* execution
stopped — which breakpoint, which exception, a step completing, the process exiting.
Nothing here asks an agent to poll for a state change, and `build` and `launch` block to
completion for the same reason.

**One configuration, every window.** The agent launches the shim in its working
directory; the shim finds the Visual Studio that has the matching solution open and
connects. No ports, no per-project setup. When the choice is ambiguous the error names
the candidates and the exact value to pass, so the next call succeeds.

**C++ gets real tools.** A breakpoint that will never bind says so and says why. `triage`
answers a crash in one call. `bp_set` can watch an address for writes. `console_read`
reaches the debuggee's own stdout.

**A reply never claims more than it found.** A breakpoint that cannot report a hit count
says so rather than printing zero. A profile with too few samples refuses to rank them.
A module without symbols names the call that would name its functions. Where an answer
is folded or cut short, the reply says which argument unfolds it — an agent is given one
reply and cannot expand a tree, so a dead end costs it the whole question.

## Profiling an agent can read

`profile_start` and `profile_stop` drive Visual Studio's own sampling collector against
a process the debugger is **already** attached to, so a profile is something taken
during a debug session rather than instead of one.

What comes back is not a file to open. The trace is read and thrown away, and
`profile_report` answers from what is left, as often as you like and without collecting
again: who calls a hot function and which of its source lines the samples landed on, the
same samples as a call tree, a roll-up per binary — the first question to ask of a host
with plugins in it — one thread on its own, or what moved since an earlier capture, in
percentage points.

Function names come from the symbols the debugger has already loaded for the session, so
there is no wait on a symbol server for modules nobody asked about. And because CPU
sampling cannot see a thread that is blocked, every profile accounts for the processor
time actually used against how long the clock ran, or says it cannot: a program stuck on
a lock reads as a program stuck on a lock, not as a program with nothing slow in it.

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

50 of them, grouped as: session, lifecycle, execution, breakpoints, inspection,
profiling, evidence, debuggee I/O, and build.

Worth knowing about:

- **`wait`** — pass `instance: "any"` to return as soon as *any* connected window stops,
  which is how you debug a client and a server at once. `for: "module:NAME"` waits for a
  module to load, which is how to arm breakpoints in a plugin before its host loads it.
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
  profile says. Allocation and file I/O collectors exist and are not wired up.
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
