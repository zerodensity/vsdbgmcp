# vsdbgmcp

Drives the Visual Studio debugger from an AI agent, over the Model Context Protocol.

Debug-first. C++ is a first-class target: data breakpoints, disassembly, crash dumps,
symbol diagnostics, and the debuggee's own console. Several Visual Studio windows can be
driven from one agent session.

Not built yet as a release — see [status](#status).

## Why

Three things that are hard to get from an IDE automation server, and that this exists to
provide:

**Waiting works.** `wait` blocks on the debugger's own events and reports *why* execution
stopped — which breakpoint, which exception, a step completing, the process exiting.
Nothing here asks an agent to poll for a state change, and `build` and `launch` block to
completion for the same reason.

**One configuration, every window.** The shim is launched by the agent in its workspace,
finds the Visual Studio that has the matching solution open, and connects. No ports, no
per-project setup. When the choice is ambiguous the error names the candidates and the
exact value to pass, so the next call succeeds.

**C++ gets real tools.** A breakpoint that will never bind says so and says why. `triage`
answers a crash in one call. `bp_set` can watch an address for writes. `console_read`
reaches the debuggee's own stdout.

## Install

Build both halves and copy the shim somewhere stable:

```powershell
.\build.ps1 -Install
```

Install the extension by double-clicking `src\VsDbgMcp.Host\bin\Release\VsDbgMcp.Host.vsix`
and restarting Visual Studio, then register the shim with your agent once, globally:

```powershell
claude mcp add -s user vsdbg -- "%LOCALAPPDATA%\vsdbgmcp\bin\vsdbgmcp.exe"
```

That is the whole setup. Every repository and every Visual Studio window works from it.

`-Install` matters: an agent keeps the shim running, and a running executable cannot be
overwritten, so pointing your client straight at the build output means the next
rebuild fails while anything is connected.

Requires Visual Studio 2022 or 2026 with the *Visual Studio extension development*
workload to build, and the .NET 10 SDK.

## How it fits together

```
MCP client (Claude Code, Cursor, ...)   one global config, spawned per workspace
      | stdio
vsdbgmcp.exe  (shim, self-contained)    MCP protocol, tool surface, discovery,
      |                                 routing, aggregation
      | JSON-RPC over a named pipe, one per connected Visual Studio
VS extension (VSIX, inside devenv)      IDebugHost + IProjectSystem
                                        over DTE and IVsDebugger
```

The client spawns the shim and the shim finds Visual Studio, not the other way round.
That is what removes the port from the configuration, and it means nothing can be
orphaned: the shim dies with its client, the extension with devenv.

MCP lives in the shim rather than inside `devenv.exe`, so the code loaded into Visual
Studio is COM interop and a pipe — no dependency of ours competes with Visual Studio's
own assembly versions, and the tool surface can change without reinstalling anything.

Each running instance publishes `%LOCALAPPDATA%\vsdbgmcp\inst-<pid>.json` with its pipe
name, a token, and the workspace it has open. There is no daemon; the directory is the
registry, and dead entries are pruned when anyone looks.

Full reasoning is in [docs/design.md](docs/design.md).

## Seeing what the agent is doing

Something driving your debugger from outside should be visible from inside, and stoppable.

**Extensions → Debugger MCP Server** opens a docked panel (also under View → Other
Windows). It appears on its own the first time an agent attaches, without taking focus,
and shows:

```
Listening, 1 client attached
DebugTarget#100424  ·  design
vsdbgmcp-100424

[Pause]  [x] Don't steal focus  [Clear]

16:02:59  stop
16:02:54  bp_list      2 ms
16:02:49  threads      5 ms
16:02:31  eval         5 ms
16:02:18  launch      84 ms
16:02:12  status       3 ms
16:02:12  client connected
```

- **Pause** is a kill switch. Every tool then refuses with an explanation telling the
  agent a person stopped it, until Resume.
- **Don't steal focus** puts the window you were using back in front when an *agent*
  starts, resumes or steps the program, instead of letting Visual Studio come forward.
  Stops you cause yourself are never touched: the guard arms only on an agent command
  that resumes execution, fires once, and disarms when the program next stops. Without
  that distinction it would pull focus away from you every time you pressed F10.
- The list is the last 200 calls, newest first, with how long each took; failures are
  marked in red.

There is also a `vsdbgmcp` pane in the Output window carrying the pipe name, client
connections, and anything that went wrong inside the extension.

## Tools

43 of them. Every one answers a question rather than mirroring a debugger window.

| | |
|---|---|
| **session** | `instances` `use` |
| **lifecycle** | `status` `launch` `attach` `detach` `stop` `restart` `processes` `dump_open` |
| **execution** | `wait` `go` `pause` `step` `run_to` `set_next` |
| **breakpoints** | `bp_set` `bp_list` `bp_remove` `bp_enable` `exceptions_set` |
| **inspection** | `threads` `stack` `select` `freeze` `eval` `vars` `expand` `watch_set` `memory` `registers` `disasm` `modules` |
| **evidence** | `triage` `capture` |
| **debuggee I/O** | `console_read` `console_send` `output` |
| **build** | `build` `build_cancel` `build_output` `config` `startup_project` |

A few worth knowing about:

- **`wait`** — the one that matters. Pass `instance: "any"` to return as soon as *any*
  connected window stops, which is how you debug a client and a server at once.
- **`eval`** — refuses to call functions unless you pass `allowSideEffects`. The native
  evaluator will really run them, and an agent inspecting `v.size()` should not be able
  to change the program by accident. Format specifiers go in `format`, not spliced into
  the expression.
- **`bp_set`** — with `dataExpression`, a data breakpoint: break when the memory at an
  address changes. The best tool there is for finding what corrupts a value.
- **`watch_set`** — pins expressions whose values then come back with every `wait` and
  every `status`, instead of several `eval` calls at each stop.
- **`triage`** — after a crash: exception record, faulting stack, registers, memory at
  the fault address, and which modules were missing symbols. One call.
- **`threads`** — every thread's top frames, grouped, so a deadlock is one call away.
  It spans **every process in the session**, named and split, which is how you find the
  thread ids of a launcher when the editor it started is the one that stopped.
- **`select`** — switch to another thread *or another process*, by pid or part of its
  name. `stack`, `eval`, `vars`, `registers` and `memory` all follow it across the
  process boundary. The choice lasts until the program next runs, because a frame does
  not survive its thread resuming.

## Layout

```
src/VsDbgMcp.Core    contracts and routing, no Visual Studio references
src/VsDbgMcp.Shim    the .NET 10 executable the agent launches
src/VsDbgMcp.Host    the extension; compiles Core's sources in rather than referencing
tests/               routing, discovery, events, and the shim end to end
docs/design.md       why it is shaped this way
```

`build.ps1` drives two toolchains because the halves need different ones: the shim and
tests build with the dotnet CLI, and the extension needs the MSBuild inside Visual
Studio, since the VSIX packaging tasks are .NET Framework assemblies.

## Status

Working, and exercised against a real debuggee.

58 automated tests cover routing, discovery, the event bus, and the whole shim path —
discovery file, named pipe, JSON-RPC, rendering — against a stand-in for the extension.

Beyond that, the following were driven by hand against Visual Studio 2026 debugging a
native C++ program (`tests/fixtures/cpp`), which is where the interesting failures live:

- launch, breakpoint hit reported by `wait` with its id, step, run-to, set-next, exit
  with its code
- an unhandled access violation reaching `wait` as `stopped: exception 0xC0000005 …
  unhandled`, and `triage` answering it in one call, registers included
- a **data breakpoint** catching a buffer overwrite, stopping in `memset` with the
  offending line one frame up, and `select` then showing `0xdeadbeef` had become
  `0xdeadbe41`
- `eval` refusing `Upload(mesh, 1)` by default and running it with `allowSideEffects`,
  with `mesh.refCount` going 1 → 2 as proof it really executed
- natvis summaries (`{name="terrain" vertices={ size=4 } refCount=1 }`), format
  specifiers, `expand` on a `std::vector` showing its elements
- `console_read` returning the debuggee's own stdout, `output` showing the Debug pane's
  PDB messages, registers, memory, disassembly with source interleaved, thread grouping,
  freeze/thaw, build with structured errors, and routing by working directory
- **two processes in one session**: `threads` listing 8 threads across both with each
  group named, `stack` on a thread in the process that did not stop, `select` by pid
  switching evaluation into it, and an unknown id answering with every thread that does
  exist and which process it is in

Known gaps:

- **`exceptions_set` does not work.** `DTE.Debugger.ExceptionGroups` returns nothing on
  Visual Studio 2026, so there is no category to configure. The tool says so plainly
  rather than pretending. Making it work means going to the debug engine directly, the
  same way expression evaluation already does.
- **Solution filters cannot be named.** Visual Studio reports the `.sln` a `.slnf`
  filters and this SDK exposes no property for the filter itself, so two windows holding
  the same solution under different filters are told apart by process id. Routing still
  refuses to guess between them.
- **A data breakpoint listed by `bp_list` shows less than `bp_set` returned** — the
  address it watches is not readable back from the automation model.
- **A function breakpoint must match how the symbol is actually named.** `Corrupt` in an
  anonymous namespace does not bind as `Corrupt`; the reply says it did not bind and
  where to look.
- **CMake and Open Folder workspaces** are not supported for build or launch. `attach`
  works regardless, so the inspection surface is available there today.
- `capture` needs a window; a console program has none, and it says so.

## Licence

MIT.
