# VS Debugger MCP

Drives the Visual Studio debugger from an AI agent, over the Model Context Protocol.

Debug-first. C++ is a first-class target: data breakpoints, disassembly, crash dumps,
symbol diagnostics, and the debuggee's own console. Several Visual Studio windows can be
driven from one agent session.

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
stop the program, read memory and registers, and evaluate expressions — including ones
with side effects, when the caller explicitly asks for them. Each Visual Studio listens
on its own named pipe, guarded by a token written to a file only your account can read,
and nothing is reachable from the network. The panel shows every call as it happens and
can stop them all.

## Tools

43 of them, grouped as: session, lifecycle, execution, breakpoints, inspection,
evidence, debuggee I/O, and build.

Worth knowing about:

- **`wait`** — pass `instance: "any"` to return as soon as *any* connected window stops,
  which is how you debug a client and a server at once.
- **`eval`** — refuses to call functions unless you pass `allowSideEffects`, because the
  native evaluator really runs them.
- **`bp_set`** — with `dataExpression`, breaks when the memory at an address changes.
  The best tool there is for finding what corrupts a value.
- **`triage`** — after a crash: exception record, faulting stack, registers, memory at
  the fault address, and which modules were missing symbols. One call.
- **`threads`** — every thread's top frames, grouped, across *every process in the
  session*, so a deadlock is one call away.
- **`select`** — switch to another thread or another process, by pid or part of its
  name. Inspection follows it across the process boundary.

## Requirements

Visual Studio 2022 or 2026, 64-bit, Community, Professional or Enterprise.

## Known limits

- `exceptions_set` does not work. `DTE.Debugger.ExceptionGroups` returns nothing on
  Visual Studio 2026, so there is no category to configure. The tool says so plainly
  rather than pretending.
- CMake and Open Folder workspaces are not supported for build or launch. `attach` works
  regardless, so the inspection surface is available there today.
- Two windows holding the same solution under different solution filters are told apart
  by process id, because Visual Studio reports the `.sln` a `.slnf` filters and exposes
  no property for the filter itself. Routing still refuses to guess between them.
- A function breakpoint must match how the symbol is actually named; one in an anonymous
  namespace does not bind under its bare name. The reply says it did not bind and where
  to look.

## Source

[github.com/zerodensity/vsdbgmcp](https://github.com/zerodensity/vsdbgmcp) — MIT.
