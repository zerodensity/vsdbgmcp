# Changelog

## 0.4.0

- `profile_start` and `profile_stop` sample the debuggee's CPU use through Visual
  Studio's own collector, which attaches to a process the debugger already holds, so a
  profile is taken during a debug session rather than instead of one. `profile_stop`
  reports where the samples landed, the path most of them went down, and what each
  thread was doing.
- `profile_report` asks anything else of a profile already taken, without collecting
  again: one function's callers, the functions it called and which of its source lines
  the samples landed on; the same samples as a call tree; a roll-up per binary, which is
  the first question to ask of a host with plugins in it; one thread on its own; or what
  moved since an earlier capture, in percentage points. Every folded line says which
  argument unfolds it.
- A profile says what sampling cannot see. Too few samples to rank refuses to rank them;
  a process that was mostly blocked says what share of the wall clock it was on a
  processor, because a lock is invisible to a CPU profiler and saying nothing there
  reads as nothing being wrong; a module without symbols is one row naming the call that
  would name its functions.

## 0.3.0

Nine issues from a second agent-driven session, and what driving a real debugger
afterwards turned up, recorded in [docs/iteration_2.md](docs/iteration_2.md). The first
three change what existing calls return.

- Reads are refused while the debuggee is running, rather than answered out of the frame
  where it last stopped. The mode they check is read from the shell as they ask; it used
  to be taken from a notification that arrives seconds after the change it announces, so
  a read issued straight after `go` was served from the previous stop, and `pause` was
  refused for nothing running while the program ran. `stack` says the same rather than
  showing no frames.
- After a pause, reads no longer land on the row Visual Studio puts on top of the stack.
  `eval`, `vars`, `memory` and the rest step past it to the innermost frame that can be
  read and say which one that was. A frame pinned with `select(frame: N)` or named in
  `eval` is still used exactly as asked, and its failure names the nearest frame that
  would have worked. `vars` with nothing to show says why instead of coming back empty.
- `modules` says which binary each module actually is: the time stamped into the loaded
  image, the load path, the size and the load address, and the symbol file it found.
  The image's own time is the one that survives a deployment; the file time is labelled
  as belonging to whatever sits at that path on this machine, which for a remote
  debuggee is a different copy or nothing. A list of more than forty stays one line per
  module and says to filter for the rest.
- `status` names the machine a debuggee is running on and how the debugger reaches it,
  so a remote session stops reading as a dead one. `processes` marks the remote rows for
  the same reason: their pids are in no local process list.
- `bp_list` reports the hit count its description always promised. The hits are counted
  on the places a breakpoint bound rather than on the pending breakpoint the listing
  walks, which is why the number was missing. A breakpoint that breaks carries one even
  at zero, so `hits 0` says the line was never reached; a tracepoint carries none,
  because only hits that broke are counted at all.
- `registers` and `disasm` say why they have nothing instead of returning an empty list
  that read as "native debugging only". A frame pinned with `select(frame: N)` that
  cannot be read names the nearest one that can, and both say which frame they read in
  when it is not the one the call started from. `memory` says so too.
- `wait` no longer answers with a stop left over from a debug session that has ended.
  Attaching to a debuggee that had been restarted returned the previous process
  exiting, which reads as the current target having died. A process that exited a
  moment ago is still reported.
- `symbols(module)` returns the Modules window's Symbol Load Information: every path the
  engine tried and what each one turned out to be, which is where a PDB that is present
  and does not match the binary finally says so. `symbols(module, load: true)` is Load
  Symbols, reports the state after the attempt rather than what the call returned, and
  says that the load will not survive the module unloading and loading again.
- `expand` takes `index` or `key` and returns one element of a container together with
  the reference that reaches it, so the visualizer's own two hundred character
  expression never has to be carried from one call to the next. The key is a walk down
  the elements comparing what each renders as, not a lookup, and it sees only the first
  200 an expansion reads; a refusal says which of those it compared.
- `scratch(type)` takes a block of the debuggee's own heap and hands back its address
  with the cast to paste into `eval`, which is what makes a call with a reference
  out-parameter possible: the evaluator will not create a temporary, so the argument had
  nowhere to live. `scratch_free` gives a block back and every reply lists what is still
  out. Blocks come from the process heap, arrive cleared, and are forgotten when the
  session ends. Taking one runs the program's own allocator. A type the frame's own
  module does not name has to be given a size in bytes, because the module qualifier is
  refused in a type position; `expand(typeModule)` reads the block back.
- Two refusals that come from the native expression evaluator rather than from this
  server now say so, and say what to do instead: evaluate a nested call's inner half on
  its own, and for a reference out-parameter take a block with `scratch` and pass a
  dereference of its address. Where the evaluator will not name the type at all, which
  happens to a type in an anonymous namespace, the advice is to cast the function instead
  and hand it the raw block. Nothing else the evaluator says is touched.

## 0.2.0

Eight changes from a long agent-driven debugging session, recorded in
[docs/iteration_1.md](docs/iteration_1.md). The first two change what existing calls
return.

- `vars`, `expand`, `memory`, `registers`, `disasm` and `eval` refuse to read a debuggee
  that is not stopped, instead of answering from the frame where it last stopped. `pause`
  now blocks until the stop lands and reports where.
- A breakpoint that will not bind because its source file was written after the module
  was built says so, rather than reporting no code at that location. `modules` carries
  each binary's build time, and a filtered result says how many modules it filtered.
- Tracepoints can be read on their own: `bp_set(logMessage: ..., collect: true)` keeps a
  breakpoint's records apart from everything else the program logs, and `trace_read`
  returns them numbered, in order, with the rate they arrived at.
- `bp_set` evaluates each `{expr}` in a tracepoint message once and reports which will
  work, or says it could not check because the debuggee is not stopped there.
- `bp_set` takes `everyNthHit`, which is the debug engine's own hit filter, and
  `maxPerSecond`, which only keeps the collected stream readable.
- `vars` marks a variable the engine could not read, and marks variables that read one
  address, which is how an optimized build reuses a slot for two names.
- `eval` and `expand` take `typeModule`, so a cast to a type from another module can be
  written the natural way round.
- `wait(for: "module:NAME")` returns when a module loads, so breakpoints in a plugin can
  be armed before its host loads it without polling.
- Allocator fill patterns are named where they appear: `0xdddddddddddddddd` in a value,
  or a run of it in a `memory` dump, reads as freed heap.
- Staging the shim no longer installs a new executable beside files it could not
  replace. That left a folder claiming to be current while running old code, and
  because the executable is what the next startup checks, it never corrected itself.

## 0.1.2

- The icon keeps the D's square corners, which is how the Zero Density mark draws them,
  and widens the arrow's shaft.

## 0.1.1

- The icon is now drawn from the Zero Density mark: a breakpoint in the counter of the
  O, and a step arrow running from behind it into the wedge the D already carries.

## 0.1.0

First public release.

- 43 MCP tools over the Visual Studio debugger: lifecycle, execution, breakpoints,
  inspection, crash evidence, debuggee I/O, and build.
- `wait` blocks on the debugger's own stopping events and reports why execution stopped,
  so nothing has to poll.
- Routing by working directory across several open Visual Studio windows, with errors
  that name the candidates and the exact `instance` value to pass.
- Multi-process sessions: `threads` spans every process, and `select` switches
  evaluation into another one by pid or name.
- C++ depth: data breakpoints, disassembly with interleaved source, crash dumps, natvis
  summaries, register and memory reads, symbol and bind diagnostics, console I/O.
- A docked panel showing every call with what it returned, a kill switch, and a focus
  guard that tells agent-caused stops from your own.
- The server executable ships inside the extension and is staged to
  `%LOCALAPPDATA%\vsdbgmcp\bin` on startup, so installing the extension is the whole
  installation.
