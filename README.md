# vsdbgmcp

Drives the Visual Studio debugger from an AI agent, over the Model Context Protocol.

C++ is a first-class target: data breakpoints, disassembly, crash dumps, symbol
diagnostics, sampling profiles, and the debuggee's own console. Several Visual Studio
windows can be driven from one agent session.

## What it does

`wait` blocks on the debugger's own stopping events and reports why execution stopped —
which breakpoint, which exception, a step completing, the process exiting. `build` and
`launch` wait for a bounded interval, then return either their result or a recoverable
operation ID. `operation_status` waits for or retrieves that result after a timeout or
reconnect. Reuse `requestId` when retrying build, launch or breakpoint requests.

The agent launches the shim in its working directory; the shim finds the Visual Studio
that has the matching solution open and connects. No ports and no per-project
configuration. When the match is ambiguous the error names the candidates and the exact
value to pass.

For C++: a breakpoint that cannot bind says so and why, `triage` collects a crash in one
call, `bp_set` can watch an address for writes, and `console_read` reads the debuggee's
own stdout.

A profile is something an agent can read rather than a file to open. `profile_start` and
`profile_stop` drive Visual Studio's own sampling collector against a process the
debugger is already holding. Aggregates and ownership metadata persist across client
restarts; `profile_export` writes reports, JSON and optionally retained raw traces.
`profile_report` answers from those counts — who calls a hot function, which of its source lines the
samples landed on, which binary the time went to, or what moved since the last
capture.

## Events

A reply already carries what changed. When something happened since the agent's previous
call — a stop, an exit, a build finishing, debugging starting or ending, a window closing
— the next reply opens with `Since your last call:` and one line per event. Nothing is
added when nothing happened, so there is no block to skip on the calls in between.
`status` carries the same lines for its own window, the last five, under `Recent:`.

An agent that starts a build or a long run and then goes off to edit files has nothing
watching in the meantime. `vsdbgmcp --follow` is a second process for a client that can
watch one:

```powershell
vsdbgmcp.exe --follow --cwd "D:\repo" --instance any
```

```
14:03:11 stopped at breakpoint 3, main.cpp:42 in Mesh::Upload (thread 15224)
```

One line per stop, exit, build completion or session change on stdout, until stdin
closes. `--instance any` watches every window and prefixes each line with its id;
`--match REGEX` also prints Debug-pane lines that match it.

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

**Updates:** when the updated extension first loads, it installs the new
shim and stops previous shim processes from that installation. The MCP client must
reconnect to launch the new version; restart its MCP server if it does not reconnect
automatically. Failed staging leaves existing processes alive and logs recovery guidance
in the Debugger MCP Server output pane. An interrupted tool call has an unknown outcome;
after reconnecting, inspect any known operation ID before retrying work.

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

60 tools.

| | |
|---|---|
| **session** | `instances` `use` |
| **lifecycle** | `status` `debug_state` `operations` `operation_status` `launch` `attach` `detach` `stop` `restart` `processes` `dump_open` |
| **execution** | `wait` `go` `pause` `step` `run_to` `set_next` |
| **breakpoints** | `bp_set` `bp_list` `bp_remove` `bp_enable` `trace_read` `exceptions_set` |
| **inspection** | `threads` `stack` `select` `freeze` `frame` `eval` `vars` `expand` `watch_set` `memory` `registers` `disasm` `modules` `symbols` `scratch` `scratch_free` |
| **profiling** | `profile_start` `profile_stop` `profile_report` `profile_status` `profile_recover` `profile_recover_stop` `profile_export` |
| **evidence** | `triage` `capture` |
| **debuggee I/O** | `console_read` `console_send` `output` |
| **build** | `build` `build_cancel` `build_output` `build_log` `build_diagnostics` `config` `startup_project` |

Notes on a few:

- **Compact IDs** — new operation, capture, and host-session IDs use
  6 lowercase alphanumeric characters, such as `7b4n9x`. Copy the complete
  returned ID into follow-up calls; retries and reconnects keep the same identity.
  Existing retained IDs still work. Instance IDs are VS process numbers such as
  `42696`; legacy `Name#PID` selectors and unique solution-name prefixes also work.
  Collector GUIDs stay internal. This change requires host/shim contract 9.

- **`capture`** — returns the debuggee window as native MCP PNG image
  content with a short dimensions summary. `region: "x,y,width,height"` requests a
  crop in window-relative pixels; the summary identifies the requested region and
  actual output dimensions. Image-capable clients can pass the screenshot to the
  model directly. Failures return text with `isError: true`.

- **`wait`** — `instance: "any"` returns as soon as any connected window stops, which is
  how to debug a client and a server at once. `for:` widens what it waits for beyond a
  stop: `module:NAME` for a module loading, which is how to arm breakpoints in a plugin
  before its host loads it without polling; `output:REGEX` for the first Debug-pane line
  matching it in this run; `operation:ID` for a build or launch to finish; `any` for the
  next notable event of any kind — a stop, an exit, debugging starting or ending, an
  operation finishing, a window closing. Where the debuggee has not run since it last
  stopped it says so at once instead of sitting out the timeout, because a stop cannot
  arrive from a program that is not running and reading that timeout as "this line is
  never reached" is a wrong answer the tool used to hand over. Debugging ending before any
  stop, or the window closing, end the wait the same way instead of sitting out a timeout
  that cannot end any other way.
- **`frame`** — the whole picture of where you are, in one call: the source around the
  line you stopped on, which binary that code came from and whether the file on disk
  still matches it, the arguments and locals, `this` expanded one level, and last the
  values that may be wrong with the reason for each. It takes the second reading itself
  first: a local the scope listing would not read is asked for again by name, and a
  container claiming to be empty is read with the visualizer off, which is how a vector
  that is not constructed yet stops reading as one that is empty. Only what will not
  settle is reported as doubtful, which on most frames is nothing. Each of
  those is readable one call at a time already; what this adds is that they are read at
  the same stop, and that the module and the source are read at all, which nobody does
  until a value has already misled them. Call it on landing somewhere unfamiliar rather
  than in a loop.
- **`eval`** — refuses to call functions unless `allowSideEffects` is passed, because the
  native evaluator really runs them and an agent inspecting `v.size()` should not change
  the program by accident. Format specifiers go in `format`, not spliced into the
  expression, and a type that lives in another module goes in `typeModule` for the same
  reason: `((T*)0xADDR)->Member` with `typeModule: "Foo.dll"` resolves without anyone
  having to know how the debugger wants that written.
- **`vars`** — in an optimized build, marks a variable the compiler kept nothing for as
  not readable, and marks variables reading the same address with each other's names.
  Without that, a slot the optimizer handed to two locals reads as an ordinary value of
  both.
- **`bp_set`** — with `dataExpression`, a data breakpoint: break when the memory at an
  address changes. With `logMessage`, a tracepoint: each `{expr}` in the message is
  evaluated once when the breakpoint is set, so an expression that will never work says
  so before it has logged a thousand records saying it.
- **`trace_read`** — a tracepoint set with `collect: true` gets a stream of its own,
  numbered and in order, with a rate over the time it has been collecting. That is what
  makes a 50 Hz callback readable, and what answers "how often does this run" without
  inferring it from how records interleave in the Debug pane. Visual Studio writes those
  records to the Debug pane itself, so where the pane cannot be watched as it fills they
  are recovered from it afterwards and carry no individual times; the reply says so.
- **`modules`** — says which binary each module actually is: the time stamped into the
  loaded image, its load path, size and load address, and the symbol file it found. It
  also marks a module whose source has been edited since it was built, and one the
  debuggee loaded an older build of than the one sitting beside its matched PDB. Those
  are the two breakpoints that bind nowhere for a reason neither the module list nor the
  PDB messages will show you, and the second is the one a rebuild does not fix: an engine
  that loads its plugins from a deployment directory keeps running what is there while
  the debugger matches the PDB you just built. The image's own time is the one that
  answers whether a binary deployed to another machine is the one you just built; the
  file time beside the path belongs to whatever sits at that path here.
- **`symbols`** — why one module's symbols are missing, and, with `load`, an attempt to
  fetch them. The report is Symbol Load Information: every path the engine tried and
  what each one turned out to be, which is where a PDB that is present and does not
  match the binary finally says so. A load does not survive the module unloading and
  loading again, because the Include/Exclude symbol setting is applied afresh each time;
  the reply says so.
- **`memory`, `eval`, `vars`** — a value that is nothing but an allocator's fill pattern
  is named where it appears, so `0xdddddddddddddddd` reads as freed heap without anyone
  having to remember the table.
- **`scratch`** — the expression evaluator will not invent a temporary, so a function
  with a reference out-parameter has no argument that can be written for it and calling
  one was simply refused. `scratch` takes a block of the debuggee's own heap and hands
  back the address together with the cast to paste into `eval`; `scratch_free` gives it
  back, and every reply lists what is still outstanding. Taking a block runs the
  program's own allocator, and a block is dropped when the session that owns the heap
  ends.
- **`profile_start` / `profile_stop`** — Visual Studio's sampling collector, attached to
  a process the debugger already holds, so a profile is taken *during* a session rather
  than instead of one. `profile_stop` reports where the samples landed, the path most of
  them went down, and what each thread was doing. Function names come from the symbols
  the debugger has already loaded rather than from a symbol server.
- **`profile_report`** — asks something else of a profile already taken, without
  collecting again: one function's callers and callees and the source lines inside it,
  the same samples as a call tree, a roll-up per binary, one thread on its own, or what
  moved since an earlier capture in percentage points. Module/thread filters compose
  with the selected view; conflicting views are rejected with a valid request example.
  Use `focus` for a subtree and `details` for full metadata. Everywhere a report stops short it says that it stopped, and every
  profile accounts for the processor time actually used against how long the clock ran,
  or says it cannot — because a program waiting on a lock is invisible to a CPU
  profiler, and silence there reads as nothing being wrong.
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
docs/releasing.md    how to cut a release
docs/marketplace.md  what the listing says and what to change when the product does
```

`build.ps1` drives two toolchains because the halves need different ones: the shim and
tests build with the dotnet CLI, and the extension needs the MSBuild inside Visual
Studio, since the VSIX packaging tasks are .NET Framework assemblies.

## Recoverable operations and profiles

[Iteration 4](docs/iteration_4.md) documents operation IDs, invocation-scoped build
output, bounded debugger snapshots, durable profiling, retention settings and exports.
[Iteration 5](docs/iteration_5.md) adds actionable operation status, structured
MSBuild binary-log import, and repeatable live tests in an isolated VS profile.
Version 0.9.2 uses host/shim contract 9; update both together.

## Status

585 automated tests cover routing, discovery, the event bus, and the whole shim path —
discovery file, named pipe, JSON-RPC, rendering — against a stand-in for the extension,
plus the pure decisions: which expression forms to try against a module, which values are
allocator fill, whether a source file outran its binary, whether a module was deployed
older than the build beside its PDB, which loaded name a mistyped module meant, and what
a tracepoint buffer keeps.

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

The eight changes in [docs/iteration_1.md](docs/iteration_1.md) were driven by hand the
same way, against the same fixture. One thing there has still not been seen happen: two
optimized locals sharing a slot, because no frame in the fixture produced one.

[docs/iteration_2.md](docs/iteration_2.md) records the next round the same way, and is
worth reading for what hand-testing caught that the test suite did not: a tracepoint
reporting `hits 0` while its records piled up, a symbol load reported as declining when
it had searched, a read after `pause` landing in `ntdll`, and — chasing that last one —
the discovery that the mode every check consulted came from a notification arriving
seconds late, so a read straight after `go` was being answered from the frame where the
program had last stopped.

Profiling was built the same way: the collector was proven to attach to a process
Visual Studio is already debugging before any of it was written, and every reading was
driven against a program whose call shape was known.

[docs/iteration_3.md](docs/iteration_3.md) is the third round, from a session debugging a
media engine and an Unreal application through two windows at once. Every one of its items
is the same fault — a tool answering a question it could not answer, where the answer read
as evidence. Driving it by hand caught two things the whole suite had passed: the
Immediate-window setting was being read by a name automation does not use, so the check
never fired; and a restart from a shim that had joined a live session labelled the old
process's exit with the new run's number, reporting two runs as one.

Known gaps:

- **A timeout cannot interrupt a COM call.** `operation_status` gives the retained
  outcome and next tool call, with a bounded live check. Unknown outcomes stay
  unknown even when VS becomes idle; inspect effects before deciding to retry.
- **Live build output parsing is incomplete.** `build_diagnostics` reads structured
  warning/error events from a separately recorded MSBuild `.binlog`, including
  project/configuration ownership. It does not automatically record VS builds or
  count tool messages emitted only as text.
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
- **`typeModule` needs an address, not a local.** `((T*)0xADDR)->M` resolves; naming a
  local instead of the address does not, because the qualifier sends every name in the
  expression to that module and the local is not in it. Read the local first, then pass
  the address it holds.
- **A function breakpoint must match how the symbol is actually named.** `Corrupt` in an
  anonymous namespace does not bind as `Corrupt`; the reply says it did not bind and
  where to look.
- **CMake and Open Folder workspaces** are not supported for build or launch. `attach`
  works regardless, so the inspection surface is available there.
- `capture` needs a window; a console program has none, and it says so.
- **Only clients in the same Windows session can use this**, because the client has to
  spawn the shim. WSL, dev containers and remote agents cannot. See the HTTP transport
  entry in [docs/design.md](docs/design.md#13-deferred).
- **A PDB's GUID and age are not reported.** AD7 does not expose them. `modules` answers
  the same question by a different route — the time stamped into the loaded image, and
  the verbose search text from `symbols`, which is where a PDB that is present and does
  not match says so.
- **Profiling is CPU sampling only.** Time spent blocked is invisible to it, which every
  profile says. Allocations and file I/O have collectors of their own that are not
  wired up.
- **`eval` refusing a nested call has never been reproduced** on Visual Studio 2026, so
  the advice written for that refusal has never fired. It costs nothing when it does not
  match, because an unrecognised message is passed through untouched.

## Licence

MIT — see [LICENSE](LICENSE).
