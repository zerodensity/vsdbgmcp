# Changelog

## 0.10.1

- Operation, capture, and host-session IDs are 6 characters instead of 12. IDs
  issued by earlier releases still work.

## 0.10.0

- Replies open with what happened in Visual Studio since the previous call: stops,
  exits, builds finishing, debugging starting or ending, a window closing.
- `wait` accepts `for='output:REGEX'` and `for='any'`, and ends as soon as a stop
  has become impossible rather than sitting out its timeout.
- `status` lists recent events for its window.
- `vsdbgmcp --follow` prints one line per event for a client that can watch a
  process.
- Host contract 10: the extension pushes operations completing and the solution
  changing. An older extension keeps working without those two.

## 0.9.3

- After successfully staging an extension update, stop previous shim processes from
  that installation, including older releases without update support. Match executable
  paths and retain process handles so unrelated shims and newly launched replacements
  are preserved. Serialize staging across VS windows and publish the executable last.
- Failed staging keeps existing shims alive. Report process exits separately from
  failures to inspect or stop them, with MCP reconnect guidance. Clients that do not
  reconnect automatically still require an MCP server restart; interrupted tool calls
  do not establish cancellation or success.

## 0.9.2

- Use 12-character lowercase alphanumeric operation, capture, and host-session IDs.
  Keep IDs stable through retries and reconnects; accept existing retained IDs.
- Show Visual Studio process numbers as instance IDs, while accepting legacy
  `Name#PID` selectors and solution-name prefixes. Keep collector GUIDs internal
  to persistence and omit them from `profile_status` and `debug_state` responses.
- Host/shim contract 9 requires updating both together because older shims reject
  compact capture IDs. Native debugger numbers and caller-supplied request IDs are unchanged.

## 0.9.1

- Return `capture` screenshots as native MCP PNG image content with a short dimensions
  and requested-region summary, instead of base64 text. Capture failures now set
  `isError`; activity reports contain only the summary. Tool arguments and the
  host/shim contract are unchanged.

## 0.9.0

- Make `operation_status`, `operations`, and operation waits return compact outcomes
  and actionable next calls. Keep dispatch evidence behind `details=true`; refresh
  unresolved-operation protection from returned/idle evidence without inventing success.
- Add repository guidance in `AGENTS.md` prioritizing model-facing tool selection,
  schemas, concise responses, uncertainty, and recovery behavior.
- Add offline `build_diagnostics` for structured MSBuild `.binlog` events, including
  project/configuration ownership, occurrence counts, filters, and completeness.
- Add an isolated Visual Studio live validation harness. Fix first-build dispatch
  when a fresh VS profile's output pane does not yet expose its text document.
- Host/shim contract 8 requires updating both together. See [iteration 5](docs/iteration_5.md).

## 0.8.0

- Independent review fixes: rejected/no-debug commands release their operation slots;
  missing build completion events stay unknown; operation status stays responsive
  during stalled updates; launch retries and reconnects preserve generation safety.
- Capture-specific profile stops retain their own results, refresh late-loaded symbols,
  and recover closed traces despite interrupted metadata writes. Structured waits
  return JSON for repeated stops and module outcomes.

- Host-owned operation IDs for builds, launches and breakpoint installation, bounded waits, deduplicated retries, and retained status/logs.
- Build outcomes use VS completion evidence; diagnostic counts describe parsed current-build output rather than the global Error List.
- Durable CPU captures with owner/session metadata, intervention observations, recovery and export tools, and configurable retention. Collector commands no longer block the VS UI thread.
- Compact wait/profile output, composable profile filters and subtree focus.
- Contract version 7 requires updating the host and shim together. See [validation and remaining work](docs/iteration_4.md#validation-and-remaining-work).

## 0.7.0

- **`frame`**, a new tool: everything one stop can be told about the frame it is in, in
  a single call. Where it is, the source around the line it stopped on, which binary that
  code came from and whether the file on disk still matches it, the arguments and locals,
  `this` expanded one level, and last the values that may be wrong with the reason for
  each — a local the optimizer kept nothing for, two names sharing one memory slot,
  leftover allocator memory, a container claiming to be empty. Each part is readable one call at a time already; what this adds
  is that they are read at the same stop, and that the module and the source are read at
  all, which nobody does until a value has already misled them.
- Showing source is showing something the debugger did not say, so `frame` never shows it
  without answering whether it is what the process is running. A file written since the
  module was built says so with both times; a file this machine does not have says that
  instead of nothing.
- `vars` and `expand` mark a container whose whole summary claims it is empty, the way
  `frame` does. An empty list is the one wrong answer a reader cannot see, because it
  reads as an answer, and the check belongs wherever a value is printed rather than in
  one tool. A summary that merely mentions a member which happens to be zero is left
  alone: a struct with a name, a reference count and one empty vector inside it is not a
  container claiming to be empty.
- `frame` takes the second reading itself rather than handing back a doubt to chase. A
  variable the scope listing would not read is asked for again by name, which is a
  different path through the engine and sometimes answers. A container claiming to be
  empty is read again with the visualizer off: where its raw layout is allocator fill the
  answer is that the object is not constructed at all, which is a plausible summary and
  the wrong one. Only what will not settle is reported as a value that may be wrong, and
  on most frames that is nothing.
- Allocator fill and a value nothing could read are stated as facts on their own rows
  rather than listed as values that may be wrong. There is no truth there to be wrong
  about, and saying "may be" is weaker than what is known.
- `frame` takes a thread, so reporting on a worker does not need a separate `select`.
- `symbols` follows `select` the way `modules` and `eval` do. A module belongs to a
  process, and in a session holding a launcher and what it started it used to report on
  whichever process had stopped.

## 0.6.0

- `wait` answers at once when the debuggee has not run since it last stopped, instead of
  sitting out the timeout and then reporting that execution "did not stop within the
  timeout. Still running." Both halves of that were claims, and both were false in the
  case that printed it most often: a breakpoint removed, another set, and nothing
  resumed. A timeout now says only that no stop arrived.
- Every debug session carries a number, shown by `status` and on every stop. `launch`,
  `attach`, `restart` and `dump_open` say that the pids, thread ids and addresses read
  before them name nothing now. The same number means the same run; a different one means
  everything has to be read again rather than that something restarted.
- `trace_read` no longer answers an empty stream by saying the tracepoint has not been
  hit. It cannot tell a record that was never written from one that never arrived, so it
  says what an empty stream establishes and reports the evidence it has: whether the
  Debug pane is watched or recovered, whether any other tracepoint is receiving records,
  and whether the breakpoint is disabled, unbound or filtered.
- A collected record is written between markers at both ends, so one arriving proves the
  line was reached even when nothing in the message evaluated, and one arriving without
  its end is counted as cut short rather than passed off as a reading.
- A collecting tracepoint with nothing reports the "Redirect all Output Window text to
  the Immediate Window" setting, which sends every record somewhere the pane watch cannot
  see and leaves the stream empty forever with nothing saying why.
- `modules`, `bp_set` and a breakpoint that did not bind report a module the debuggee
  loaded an older build of than the one sitting beside its matched PDB. That is the
  failure a rebuild does not fix, and it showed as symbols loading, a PDB resolving, and
  breakpoints binding nowhere. The check reads one file time per module and never goes
  near a source.
- A container the visualizer renders as empty is read again with the visualizer off and
  both views are reported. No verdict is drawn from the raw fields, because a capacity is
  spelled the same way as a size and an empty `std::deque` would otherwise be reported as
  a disagreement.
- `eval` and `expand` refuse a `typeModule` naming a module that is not loaded, and list
  the closest loaded names. Passing one the debugger cannot find drops the qualifier, and
  the type then resolves in the frame's own module, where a same-named symbol answers
  with something that reads as data rather than as an error.
- `eval` takes `count` and `member` and returns one row per index of a raw array, with
  how many of the values were distinct. A repeated block is what a container's own view
  hides, and reading it a row at a time used to cost one call per element.
- `bp_set`'s per-second cap is no longer applied where records arrive without times, and
  `trace_read` says it was not. A record with no time belongs to no second, so the window
  never rolled: the first few records were kept and everything after them thrown away for
  as long as the program ran, while the reply called it a rate.
- `modules` follows `select` the way the other reads do, and names the process its list
  came from. In a session holding a launcher and what it started, it used to read the
  process that stopped while `eval` read the selected one, with nothing saying so.

## 0.5.0

- A call that failed comes back marked as failed, rather than as ordinary text a reader
  has to recognise a failure in. The reply is the reason and nothing else: the words the
  debugger or the engine used, with no wrapper naming the tool that was called. The panel
  inside Visual Studio marks the same calls, where a refused evaluation used to show as
  one that worked.
- `eval` says why an evaluation failed instead of "evaluation failed". The engine often
  hands back its own account of the failure even while failing, and that text now reaches
  the caller with the return code beside it. A parse error names the expression it is
  about, which is not what the caller wrote once a module qualifier or a format specifier
  has been added to it.
- A value the engine evaluated and then would not describe is no longer passed off as an
  empty value the program holds.
- An expansion that read only part of what was there says so. The engine refusing to list
  a value's contents, giving up partway through, and there simply being more elements than
  one read returns were all a short list that looked like the whole of it.
- Watched expressions that cannot be read say so instead of vanishing from `status` and
  `wait`, where a missing section read as no watches being set at all.
- A profile whose trace did not record how often it sampled now says so, rather than
  leaving out the one line that separates a program waiting on a lock from a program with
  nothing slow in it.

## 0.4.1

- `profile_report` answers one reading at a time and refuses two, rather than quietly
  answering whichever it looked for first. Asking for a call tree of one module returned
  a call tree of everything, under a header that mentioned neither the module nor the
  choice it had made.
- Everywhere a report stops short now says that it stopped: the call tree's row limit, a
  function's callers and callees, the modules or threads listed when a filter matched
  nothing, and a row count larger than will ever be printed. A function with no source
  lines says whether its symbols carry none or the reading stopped looking them up,
  which are different problems with different answers.
- Stacks deeper than the reader follows are counted and reported, so a profile missing
  its outermost callers does not read as one whose callers are the frames it kept.

## 0.4.0

- `profile_start` and `profile_stop` sample the debuggee's CPU use through Visual
  Studio's own collector, which attaches to a process the debugger already holds, so a
  profile is taken during a debug session rather than instead of one. `profile_stop`
  reports where the samples landed, the path most of them went down, and what each
  thread was doing.
- `profile_report` asks anything else of a profile already taken, without collecting
  again: one function's callers, the functions it called and, where the debuggee's own
  symbols carry line numbers, which of its source lines the samples landed on; the same
  samples as a call tree; a roll-up per binary, which is the first question to ask of a
  host with plugins in it; one thread on its own; or what moved since an earlier
  capture, in percentage points. Every folded line and every dead end says which
  argument leads out of it.
- A profile says what sampling cannot see. Too few samples to rank refuses to rank them;
  every profile says how many seconds of processor time were used against how long the
  clock ran, which is what tells a program that was waiting on a lock from a program
  with nothing slow in it; and a module without symbols is one row naming the call that
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
