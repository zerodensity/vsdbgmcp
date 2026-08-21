# vsdbgmcp

Drives the Visual Studio debugger from an AI agent, over the Model Context Protocol.

C++ is a first-class target: data breakpoints, disassembly, crash dumps, symbol
diagnostics, and the debuggee's own console. Several Visual Studio windows can be driven
from one agent session.

## What it does

`wait` blocks on the debugger's own stopping events and reports why execution stopped —
which breakpoint, which exception, a step completing, the process exiting. `build` and
`launch` block to completion for the same reason, so nothing has to poll for a state
change.

The agent launches the shim in its working directory; the shim finds the Visual Studio
that has the matching solution open and connects. No ports and no per-project
configuration. When the match is ambiguous the error names the candidates and the exact
value to pass.

For C++: a breakpoint that cannot bind says so and why, `triage` collects a crash in one
call, `bp_set` can watch an address for writes, and `console_read` reads the debuggee's
own stdout.

## Install

Install the extension and restart Visual Studio. It carries the shim and copies it to
`%LOCALAPPDATA%\vsdbgmcp\bin` on startup; there is nothing else to download and no .NET
runtime to install.

Then register that path with the agent once, globally:

```powershell
claude mcp add -s user vsdbg -- "$env:LOCALAPPDATA\vsdbgmcp\bin\vsdbgmcp.exe"
```

Restart the agent afterwards. Every repository and every Visual Studio window works from
that one entry. The panel shows the same command with the path already resolved, and
copies it to the clipboard.

The shim gets a path of its own rather than staying inside the extension because Visual
Studio regenerates an extension's folder on every update, and the path in the agent's
configuration has to outlive that.

### Building it

```powershell
.\build.ps1
```

Requires Visual Studio 2022 or 2026 with the *Visual Studio extension development*
workload, and the .NET 10 SDK. `build.ps1 -Install` copies the shim straight to where the
extension stages it, for working on the shim without reinstalling the extension.

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
That is what removes the port from the configuration, and it bounds the lifetimes: the
shim dies with its client, the extension with devenv.

MCP lives in the shim rather than inside `devenv.exe`, so the code loaded into Visual
Studio is COM interop and a pipe. No dependency of ours competes with Visual Studio's own
assembly versions, and the tool surface can change without reinstalling anything.

Each running instance publishes `%LOCALAPPDATA%\vsdbgmcp\inst-<pid>.json` with its pipe
name, a token, and the workspace it has open. There is no daemon; the directory is the
registry, and dead entries are pruned when anyone looks.

Full reasoning is in [docs/design.md](docs/design.md).

## The panel

**Extensions → Debugger MCP Server** opens a docked panel (also under View → Other
Windows). It appears on its own the first time an agent attaches, without taking focus,
and shows:

```
Listening, 1 client attached
DebugTarget#100424  ·  break
vsdbgmcp-100424

Agent setup:  copy command  ·  copy path

[Pause]  [x] Don't steal focus  [Clear]

> 16:02:59  stop
> 16:02:54  bp_list      2 ms
> 16:02:49  threads      5 ms
v 16:02:31  eval         5 ms   mesh.refCount
      mesh.refCount = 1  (int)
> 16:02:18  launch      84 ms
> 16:02:12  status       3 ms
  16:02:12  client connected
```

Each call folds open to show the text the agent was given back, not a re-rendering, and
carries the argument worth reading beside its name: which expression, which file and
line, which process. The reply is selectable.

- **Pause** stops every tool. They then refuse with an explanation saying a person
  stopped them, until Resume.
- **Don't steal focus** puts the window you were using back in front when an *agent*
  starts, resumes or steps the program, instead of letting Visual Studio come forward.
  Stops you cause yourself are not affected: the guard arms only on an agent command that
  resumes execution, fires once, and disarms when the program next stops. Without that
  distinction it would pull focus away every time you pressed F10.
- The list holds the last 200 calls, newest first, with how long each took; failures are
  marked in red. Rows left unfolded stay unfolded as new calls arrive.

There is also a `vsdbgmcp` pane in the Output window carrying the pipe name, client
connections, and anything that went wrong inside the extension.

## Tools

43 of them.

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

Notes on a few:

- **`wait`** — `instance: "any"` returns as soon as any connected window stops, which is
  how to debug a client and a server at once. `for: "module:NAME"` waits for a module to
  load instead of for a stop, which is how to arm breakpoints in a plugin before its host
  loads it without polling.
- **`eval`** — refuses to call functions unless `allowSideEffects` is passed, because the
  native evaluator really runs them and an agent inspecting `v.size()` should not change
  the program by accident. Format specifiers go in `format`, not spliced into the
  expression.
- **`bp_set`** — with `dataExpression`, a data breakpoint: break when the memory at an
  address changes.
- **`watch_set`** — pins expressions whose values then come back with every `wait` and
  every `status`, instead of several `eval` calls at each stop.
- **`triage`** — after a crash: exception record, faulting stack, registers, memory at the
  fault address, and which modules were missing symbols. One call.
- **`threads`** — every thread's top frames, grouped. It spans every process in the
  session, named and split, which is how to find the thread ids of a launcher when the
  editor it started is the one that stopped.
- **`select`** — switch to another thread or another process, by pid or part of its name.
  `stack`, `eval`, `vars`, `registers` and `memory` follow it across the process
  boundary. The choice lasts until the program next runs, because a frame does not
  survive its thread resuming.

## Layout

```
src/VsDbgMcp.Core    contracts and routing, no Visual Studio references
src/VsDbgMcp.Shim    the .NET 10 executable the agent launches
src/VsDbgMcp.Host    the extension; compiles Core's sources in rather than referencing
tests/               routing, discovery, events, and the shim end to end
marketplace/         listing text and publish manifest
docs/design.md       why it is shaped this way
docs/releasing.md    how to cut and publish a release
```

`build.ps1` drives two toolchains because the halves need different ones: the shim and
tests build with the dotnet CLI, and the extension needs the MSBuild inside Visual
Studio, since the VSIX packaging tasks are .NET Framework assemblies.

## Status

72 automated tests cover routing, discovery, the event bus, and the whole shim path —
discovery file, named pipe, JSON-RPC, rendering — against a stand-in for the extension.

The following were driven by hand against Visual Studio 2026 debugging a native C++
program (`tests/fixtures/cpp`):

- launch, breakpoint hit reported by `wait` with its id, step, run-to, set-next, exit
  with its code
- an unhandled access violation reaching `wait` as `stopped: exception 0xC0000005 …
  unhandled`, and `triage` answering it in one call, registers included
- a data breakpoint catching a buffer overwrite, stopping in `memset` with the offending
  line one frame up, and `select` then showing `0xdeadbeef` had become `0xdeadbe41`
- `eval` refusing `Upload(mesh, 1)` by default and running it with `allowSideEffects`,
  with `mesh.refCount` going 1 → 2
- natvis summaries (`{name="terrain" vertices={ size=4 } refCount=1 }`), format
  specifiers, `expand` on a `std::vector` showing its elements
- `console_read` returning the debuggee's own stdout, `output` showing the Debug pane's
  PDB messages, registers, memory, disassembly with source interleaved, thread grouping,
  freeze/thaw, build with structured errors, and routing by working directory
- two processes in one session: `threads` listing 8 threads across both with each group
  named, `stack` on a thread in the process that did not stop, `select` by pid switching
  evaluation into it, and an unknown id answering with every thread that does exist and
  which process it is in

Known gaps:

- **`exceptions_set` does not work.** `DTE.Debugger.ExceptionGroups` returns nothing on
  Visual Studio 2026, so there is no category to configure. The tool reports that rather
  than pretending. Making it work means going to the debug engine directly, the same way
  expression evaluation already does.
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
  works regardless, so the inspection surface is available there.
- `capture` needs a window; a console program has none, and it says so.
- **Only clients in the same Windows session can use this**, because the client has to
  spawn the shim. WSL, dev containers and remote agents cannot. See the HTTP transport
  entry in [docs/design.md](docs/design.md#13-deferred).

## Licence

MIT — see [LICENSE](LICENSE).
