# Event awareness — design

Status: proposed, 2026-09-11. Not yet built.

## 1. Problem

The model only ever sees text that comes back from a call it made. `wait` blocks
on real debugger events, but only for stops, module loads and operations, and only
while the model is inside a `wait`. Everything else that happens in Visual Studio
— a build finishing, someone pressing F5, the debuggee exiting while the model
edits a file, the window closing — reaches the model only if it thinks to ask, so
it asks: `status` in a loop, `operation_status` in a loop, `output` in a loop.

The host already pushes most of this to the shim. The shim drops output and
workspace changes on the floor (`HostLink.OnOutputAsync`,
`OnWorkspaceChangedAsync`), never hears about operations completing, and keeps
what it does hear inside `wait`.

## 2. What is being built

Three doors, all reading from one log:

1. **A digest at the top of every reply.** When something notable happened since
   the model's previous call, the reply begins with it. Nothing is added when
   nothing happened.
2. **`wait` widened** so there is no remaining reason to poll: `for='output:REGEX'`,
   `for='any'`, and a stop wait that wakes when the stop can no longer come.
3. **`vsdbgmcp --follow`**, a stream of one line per notable event on stdout, for a
   client that can watch a process (Claude Code's `Monitor`) while the model does
   other work.

A fourth — pushing events into a Claude Code session through its channel
extension — is not built, but the seams it needs are (§5.10).

## 3. Principles

These decide every detail below. They come from how a model reads tool output.

- **Silence costs nothing.** No digest when nothing happened. A fixed line on every
  reply is a tax on hundreds of calls and trains the model to skip the block.
- **Invalidation first.** The digest goes at the top. Long results are truncated
  by clients; a note under a 30 KB listing is never read. And "the debuggee
  stopped after this was read" changes how the model reads what follows.
- **Never echo the model's own actions.** `go` must not produce "run started";
  `stop` must not produce "debugging ended"; a `build` whose reply carried the
  outcome must not produce "build done". The model has the reply.
- **Task terms, one vocabulary, one renderer.** The digest line, the `--follow`
  line and a future channel message are the same function's output.
- **Data, not instructions.** Lines describe what happened. The one fixed hint
  ("state changed") is the server's, never derived from debuggee text.
- **Cheap.** The digest is computed from memory. It never adds a round trip to
  the host.

## 4. Event catalog

| Always logged (digest, `--follow`, `for='any'`, `status` recent) | Only through `wait for=` | Never |
|---|---|---|
| stopped — breakpoint, exception, step, pause, entry | module loads | first-chance exceptions that do not stop |
| exited, with code | Debug-pane output lines matching a regex | break→run transitions (every `go`/`step`) |
| debugging started — design→run not caused by this session | operations, by id (existing) | tracepoint records (`trace_read`) |
| debugging ended — →design not caused by this session, and not the tail of an exit | | thread create/exit |
| operation done — build, launch, bp_set, any other kind, reaching a terminal state that this session has not already been told about | | output line counts |
| Visual Studio instance gone | | instance appeared (routing handles it) |
| solution changed | | console stdout (scraped, not streamed; see §10) |

Which entries *invalidate* a reply — meaning the state the reply was read from
has moved: stopped, exited, debugging started, debugging ended, instance gone,
solution changed. Operation done does not.

## 5. Components

### 5.1 Host: two pushes and a contract bump

`IShimEvents` gains one method:

```csharp
Task OnOperationChangedAsync(OperationInfo operation);
```

sent once when an operation's `Terminal` goes from false to true. `OperationRegistry`
is the only place that flips it (`Mutate`, on the `terminal` flag or on
`ObserveIdle`), so it raises a `Completed` callback there, after its locks are
released. `VsDbgMcpPackage`, which owns the pipe server, subscribes and
broadcasts.

`OnWorkspaceChangedAsync`, already in the contract, is now actually sent from
`OnAfterOpenSolution` and `OnAfterCloseSolution`, next to the existing `PublishRecord`.

`Names.ContractVersion` goes 9 → 10, with the note: "10: operations completing
and the solution changing are pushed to the shim". Compatibility is unchanged in
both directions: an older shim without the method fails the call and `Broadcast`
already swallows faults; a newer shim on an older host never hears about
operations or solution changes and everything else works.

Timestamps are stamped by the shim on arrival. The pipe adds milliseconds; no DTO
grows a field for it.

### 5.2 Shim: `EventLog`

`Session/EventLog.cs`, one per `SessionManager`, beside `EventBus`. The bus keeps
what it has — the blocking semantics of `wait` for stops and modules, generations,
the "already stopped" floor. The log is the journal of notable events.

An entry:

| Field | Meaning |
|---|---|
| `Seq` | log-wide, monotonic |
| `At` | UTC arrival at the shim |
| `InstanceId` | which Visual Studio |
| `Kind` | Stopped, Exited, DebuggingStarted, DebuggingEnded, OperationDone, InstanceGone, SolutionChanged |
| `Stop` | the `StopEvent`, for Stopped and Exited |
| `Operation` | the `OperationInfo`, for OperationDone |
| `Hidden` | true once this session has been told by other means (§5.2.3) |
| `Seen` | true once this session has been shown the entry |

Bounded at 256 entries; oldest fall off.

**Readers.** `TakeUnseen()` returns the unseen, non-hidden entries and marks them
seen under one lock, so two tool calls running at the same time never carry the
same event. `Recent(instance, n)` returns that instance's last n non-hidden
entries, seen or not. `MarkSeen(instance = null)` marks one instance's entries
seen, or every instance's. `WaitForAsync(instance, kinds, timeout, ct)` returns
the first unseen entry of those kinds, or blocks until one arrives. `Subscribe(Action<Entry>)` is called
synchronously for every non-hidden entry as it is added; `--follow` uses it, and a
channel pusher would.

A flag on each entry rather than one cursor, because `status` answers for a
single Visual Studio and must mark only that one seen (§5.7).

#### 5.2.1 Feeding it

`HostLink` forwards every push to both bus and log:

- `OnStopAsync` → `log.Stopped(stop)`: kind Stopped, or Exited when
  `stop.Reason == StopReason.Exited`.
- `OnModeChangedAsync` → `log.ModeChanged(id, mode)`. The log tracks each
  instance's previous mode, seeded from the instance record on connect the way
  the bus is.
  - design→run: DebuggingStarted, unless a RunStart expectation for this
    instance is open (§5.2.2), which it consumes.
  - →design: consumed by an open End expectation; otherwise suppressed when the
    instance's newest entry is an Exited within the last 5 s (the exit already
    said it); otherwise DebuggingEnded.
  - →break, and break→run: nothing. Stops cover break; every `go` and `step`
    would otherwise be a line.
- `OnOperationChangedAsync` → `log.OperationDone(info)`, subject to §5.2.3.
- `OnWorkspaceChangedAsync` → SolutionChanged. The push carries no name and the
  host republishes its record asynchronously, so the entry names only the
  instance; `status` tells the rest.
- Instance gone, from two places, once per connection: `HostLink` hooks
  `JsonRpc.Disconnected`, ignoring the disconnects its own `Teardown` causes;
  `SessionManager.RefreshAsync` logs it when it disposes a link whose record
  vanished. A per-link flag makes the second of the two a no-op.
- `OnOutputAsync` does not touch the log. It feeds the bus (§5.3).

#### 5.2.2 Attribution: the session's own actions

`Expect(instance, RunStart | End, operationId = null)` registers that a mode
transition is coming because this session asked for it. `Unexpect(instance,
kind)` withdraws it. An expectation is consumed by the first matching transition
and expires otherwise:

- 15 s after registration, when there is no operation id;
- when there is one — `launch` may build for minutes before the run starts — 15 s
  after that operation's terminal push, or immediately when the operation ends
  in failure.

An open End expectation also hides the Exited stops of that instance until it is
consumed or expires: terminating the debuggee is what `stop` does, and the model
was told "Stopped."

Where expectations are registered — the same places that already keep the bus's
run count honest, so no tool does half of it:

| Tool | Registers | Withdrawn on |
|---|---|---|
| `attach`, `dump_open` (through `StartRun`) | RunStart | call failure (`RunNotStarted`) |
| `launch` (its own body; `StartRun` is not on its path) | RunStart, re-registered with the operation id when the reply is pending | call failure; `noDebug` registers nothing, because Ctrl+F5 starts no debug session |
| `restart` | End then RunStart | call failure |
| `stop`, `detach` | End | call failure |
| `go`, `step`, `run_to`, `pause` | nothing — break↔run is not logged | |

A second agent on the same Visual Studio sees this session's `launch` as
"debugging started". That is correct: it is external to that agent.

#### 5.2.3 What the model was already told

Two ways an entry is hidden from the digest because the model has it from the
reply that caused it:

- `Delivered(StopEvent)`: called by `wait`, `step`, `pause` and `run_to` when
  they return a stop. The entry holding that stop is hidden.
- `OperationReported(id)`: called by `build`, `launch`, `bp_set`,
  `operation_status`, `operations` and `wait for='operation:'` for every
  terminal operation whose outcome appears in their reply. An existing entry
  with that id is hidden; otherwise the id is remembered (bounded set of 64) so
  the push, when it lands, is dropped. A `build` that returned pending and
  finished later is therefore in the digest; one that finished inside its own
  wait is not.

### 5.3 Shim: `EventBus` gains an output stream

Modelled on the module stream: kept apart so a stop wait is never woken by a
line.

- `PublishOutput(OutputEvent)`: split on newlines, trim `\r`, drop empty
  lines, append `{instance, text, reported}` to a buffer of the last 2000 lines.
- Lines belong to a debug session, so a line from the previous run must never
  satisfy a wait on this one. The instance's buffered lines are cleared when a
  tool of this session starts a run (`StartingRun`, before the command goes
  out, so nothing of the new run is lost) and on a design→run transition. The
  mode notification arrives late, so an externally started run can lose its
  first lines; the cheaper error.
- `WaitForOutputAsync(instance, Regex, timeout, ct)`: the first unreported line
  matching, from the buffer or the next to arrive; marks it reported. A line is
  reported once, whatever pattern it matched — the same rule as modules.
- Regex options: `IgnoreCase | CultureInvariant`, match timeout 1 s. A pattern
  that fails to compile is a tool-level error naming the parse failure.

### 5.4 Renderer

`EventLines` (shim, one static class): `Line(entry, withInstance)` returns one
line. Wording, in task terms:

| Kind | Line |
|---|---|
| Stopped, breakpoint | `stopped at breakpoint 3, main.cpp:42 in Mesh::Upload (thread 15224)` |
| Stopped, exception | `stopped on Access violation (0xC0000005) at mesh.cpp:218 in Mesh::Upload (thread 15224)` |
| Stopped, step / pause / entry | `stopped after step at …` / `paused at …` / `stopped at entry point, …` |
| Exited | `exited with code 3 (myapp.exe)` |
| DebuggingStarted | `debugging started` |
| DebuggingEnded | `debugging ended` |
| OperationDone, build | `build done: 2 errors, 1 warning (operation ab12)` / `build succeeded (operation ab12)` / `build cancelled (operation ab12)` |
| OperationDone, launch | `launch succeeded: myapp.exe (operation ab12)` / `launch failed: <message> (operation ab12)` |
| OperationDone, breakpoint | `breakpoint 3 bound at main.cpp:42 (operation ab12)` / `breakpoint 3 could not bind: <reason> (operation ab12)` |
| OperationDone, other | `<kind> done: <state> (operation ab12)` |
| InstanceGone | `Visual Studio App#42696 closed` |
| SolutionChanged | `solution changed in Visual Studio App#42696` |

Kinds are the host's own strings — `build`, `launch`, `breakpoint`, `profile-start`,
`profile-stop` — not tool names, and an unknown one falls to the last row. Nothing
outside `OperationInfo` is available to a line: there is no pid on it, so a launch
names its executable and no more.

Entries are listed oldest first wherever they are listed, so the last line is
always the freshest state, and a line carries `N min ago: ` only when the entry
is more than a minute old. The digest, `status` recent and a future channel
message are then the same rendering with no second format to learn; `--follow`
stamps wall clock instead, because that stream is read beside other logs.

Frame parts are omitted when unknown, the way `Render.Stop` does. `withInstance`
prefixes `[App#42696] `; the digest sets it when more than one Visual Studio is
connected, `--follow` when watching `any`. InstanceGone and SolutionChanged
name the instance in the line itself and are never prefixed.

"debugging started" and "debugging ended" are worded neutrally on purpose: in the
digest they are by construction not this session's doing; in `--follow` they may
be.

### 5.5 The digest

A call-tool filter in `Program.cs`, registered outside the existing one so it
sees a `ToolFailure` after that filter has turned it into an error result. After
the tool has run — success or failure — it calls `log.TakeUnseen()`; when that
is non-empty it inserts a `TextContentBlock` at index 0 of the `CallToolResult`:

```
Since your last call, state changed:
- stopped at breakpoint 3, main.cpp:42 in Mesh::Upload (thread 15224)
- build done: 2 errors (operation ab12)
```

- Header `Since your last call, state changed:` when any entry invalidates (§4),
  else `Since your last call:`.
- Always header plus `- ` lines, even for one entry: one shape to learn.
- At most 6 lines, then `- and N more; call status for the current state`. The
  rest are still marked seen. Stops cannot pile up between calls without the
  model resuming in between, so the cap is a guard, not a feature.
- The block ends with a blank line. Content blocks arrive concatenated, and
  without the break a `vars` reply reads as though `vars` had reported the stop.
- Structured results (`operation_status`, `debug_state`, `build_log`,
  `operations`) keep their `StructuredContent`; the block goes in front of their
  text block.
- A failed call carries the digest too. `eval` failing with "no frame" next to
  `exited with code 3` is the pairing that explains it.
- The panel report inside `ToolBase.Answer` is unchanged: the panel shows the
  tool's own answer.

Parallel calls: `TakeUnseen` is atomic, so two replies never carry the same
entry; the first to finish takes them.

### 5.6 `wait`

`for` grammar, enumerated in the argument description:

| `for` | Returns on |
|---|---|
| omitted | the next stop (existing) — or, new, on the stop becoming impossible |
| `module:NAME` | existing |
| `operation:ID` | existing |
| `output:REGEX` | the first unreported Debug-pane line matching REGEX in the current session; straight away if one is buffered |
| `any` | the next notable event; straight away with everything unseen if any is |

**Stop waits wake when the stop cannot come.** The stop wait races
`log.WaitForAsync(instance, {DebuggingEnded, InstanceGone})`. Outcomes
`session-ended` and `instance-gone` return at once with one sentence: "Debugging
ended in Visual Studio before any stop; nothing is running to stop." / "Visual
Studio 42696 closed." The timeout path, with its observations, stays for the
case where nothing at all happened. With `instance='any'`, one window ending or
closing while another could still stop is a digest line, not an answer; the wait
ends early only when no connected window is left in run mode.

**`output:`** reply: `output matched: <line>`. Timeout: `timeout: no Debug-pane
line matched /REGEX/ in N s.` The description says this is the Debug pane
(OutputDebugString, engine messages), not the debuggee's console.

**`any`** reply: the event lines, oldest first, and the entries are marked seen
so the next reply does not repeat them. Timeout: `timeout: nothing notable
happened in N s.` The description says what `any` does not cover — module loads
and Debug-pane lines have their own `for` forms — so it is not reached for in
their place.

`structured=true` keeps the existing shape for both new forms:
`{eventReceived, outcome}` with `outcome` of `event` or `timeout`, plus `line`
for `output:` and `events` for `any`.

`wait`, `step`, `pause` and `run_to` call `Delivered(stop)` for the stop they
return.

### 5.7 `status`

`status` is the "I do not know where I am" tool, called after context loss, when
"since your last call" is empty by definition. Its reply gains a section:

```
Recent:
- 5 min ago: build done: 0 errors (operation ab12)
- 2 min ago: exited with code 3 (myapp.exe)
```

That instance's last 5 non-hidden entries, omitted when there are none. `status`
answers for one Visual Studio, so it calls `log.MarkSeen(link.Id)` before
returning: its own window is never reported twice on one reply, while a stop in
another window still reaches the digest, where the instance prefix says which.
This closes the open item in `design.md` §15.

### 5.8 `vsdbgmcp --follow`

```
vsdbgmcp --follow [--cwd DIR] [--instance ID|any] [--match REGEX] [-v]
```

A second shim process, not an MCP server: it connects to the same Visual Studio
windows as a subscriber and prints one line per notable event to stdout, flushed
per line:

```
14:03:11 stopped at breakpoint 3, main.cpp:42 in Mesh::Upload (thread 15224)
14:03:40 build done: 2 errors (operation ab12)
```

- Routing is the shim's own: `--cwd` and `--instance` mean what they mean for the
  server. `--instance any` watches every window and prefixes each line with the
  instance id.
- `--match REGEX` also prints Debug-pane lines matching REGEX, as
  `14:03:12 output: <line>`. This is the "tell me every time it prints ERROR"
  use, and the model is responsible for a selective pattern as it would be with
  `tail -f | grep`.
- Discovery runs every 2 s (a directory scan, not a query to Visual Studio),
  through `SessionManager.KeepConnectedAsync(ct)`, added with this work — the
  request-driven server has never needed to refresh on its own — so a window opened
  later is picked up. Connection changes go to stderr (`connected 42696 App.slnx`,
  `lost 42696`), which a monitor does not turn into notifications.
- Lifetime: until stdin closes, the parent exits (`ParentWatch`, as today) or
  Ctrl+C. It never ends on its own. An extension update retires it with every
  other shim process from that installation (`docs/shim-updates.md`); the monitor
  reports the exit and the model can start it again.
- It has no expectations to register, so it prints this session's `launch` as
  `debugging started`. The wording is neutral for that reason.
- Times are local wall clock, `HH:mm:ss`, because the stream is a log read next
  to other logs.

The server instructions announce it with the exact command, built from
`Environment.ProcessPath` and the server's own `--cwd`/`--instance`:

> While the program runs and you have other work, run
> `"C:\…\vsdbgmcp.exe" --follow --cwd "D:\repo"` under a background monitor if
> your client has one; it prints one line per stop, exit, build completion or
> session change until stopped.

### 5.9 Server instructions

Added to `Program.Instructions`, after the waiting paragraph:

> Events: when something happened in Visual Studio since your previous call — a
> stop, an exit, a build finishing, someone starting or ending debugging — the
> reply begins with "Since your last call:" and one line per event. Nothing is
> added when nothing happened. `wait` with for='any' blocks for the next such
> event, for='output:REGEX' for a Debug-pane line. `status` lists recent events.

plus the `--follow` paragraph above.

### 5.10 Channel push — not built, seams in place

Claude Code's channel extension lets a stdio MCP server declare
`capabilities.experimental["claude/channel"] = {}` and send
`notifications/claude/channel { content, meta }`; the model receives
`<channel source="visual-studio" …>` even while idle. It is a research preview
behind `--channels` / `--dangerously-load-development-channels server:<name>`,
so it is not shipped here. Building it later is:

1. A `--channel` option on the server that sets the experimental capability in
   `McpServerOptions` and adds a hosted service.
2. The service starts `SessionManager.KeepConnectedAsync` (connections must stay
   alive while the model is idle, which the request-driven server does not need)
   and `log.Subscribe`s to invalidating entries only.
3. For each: `server.SendNotificationAsync("notifications/claude/channel",
   new { content = EventLines.Line(entry, many), meta = new { instance, kind } })`,
   then marks that entry seen so the next digest does not repeat it.
4. One sentence in the instructions: a `<channel>` event from this server is
   information, not a request.

The C# SDK in use (`ModelContextProtocol` 2.2.0) exposes both
`ServerCapabilities.Experimental` and `McpServer.SendNotificationAsync`.

## 6. Walkthroughs

**Build finishes while the model edits.** `build` returns pending with
`operation ab12` after 30 s. Two minutes later the host pushes
`OnOperationChangedAsync`; the id is not in the reported set, so OperationDone is
logged. The model's next call — `bp_set` — returns with
`Since your last call:\n- build done: 2 errors (operation ab12)` above the
breakpoint reply.

**Build finishes inside its own wait.** `build` returns the errors. On the way
out it calls `OperationReported("ab12")`; the push arrives 20 ms later and is
dropped. No digest.

**Someone presses Shift+F5.** Mode →design with no End expectation and no recent
exit: DebuggingEnded. A stop wait in flight returns `session-ended`. Otherwise
the next reply says `Since your last call, state changed:\n- debugging ended`.

**The model calls `stop`.** End expectation registered; `StopAsync` returns;
the exit stops and the →design transition arrive within seconds and are consumed.
Nothing logged. The bus's `MarkSeen`, called before the command as today, keeps
`wait` from reporting the old process's exit.

**The debuggee crashes while the model reads source.** Exited stop logged; the
→design 200 ms later is suppressed by the 5 s exit rule. The next reply, whatever
it is, opens with `exited with code -1073741819 (myapp.exe)` and the header says
state changed. If that reply was `vars`, it failed — and the digest says why.

**Visual Studio is closed.** The pipe drops; `Disconnected` logs InstanceGone
once. A stop wait returns `instance-gone`. `RefreshAsync` later disposes the
link; its flag is set, so nothing is logged twice.

**`launch` builds for three minutes first.** RunStart expectation tied to the
launch operation. The →run transition arrives after the build and is consumed.
Had the build failed, the operation's terminal push cancels the expectation and
the OperationDone entry — not reported by any reply — goes in the digest as
`launch failed: …`.

## 7. Edge cases and failure behaviour

- **Regex failures** in `for='output:'` and `--match` are reported as errors
  naming the parse problem; a match timeout (1 s) counts as no match.
- **Reconnect** (`InitializeMode` on an existing instance) does not clear the
  log; entries from before the gap are still true. Expectations survive it.
- **Older host** (contract < 10): no OperationDone or SolutionChanged entries;
  `OperationReported` ids accumulate harmlessly in a bounded set.
- **Two shims** (two clients, or a server plus `--follow`) are independent
  subscribers with independent cursors. The same event may appear in a monitor
  line and in a digest; the wording is identical so the model can tell.
- **Log bound** (256) can drop unseen entries during a very long absence; the
  digest cannot know, and `status` recent shows the latest state.
- **An expectation that expires unconsumed** logs nothing. "I asked it to stop
  and it did not stop" is an absence, and the log reports what happened, not what
  failed to; `status` is where that is visible.
- **Attribution misses** — an `attach` that joined rather than started leaves a
  RunStart expectation open for 15 s; an F5 in that window is swallowed. Accepted
  as the cheaper error.

## 8. Testing

Automated (`dotnet test tests/VsDbgMcp.Tests/VsDbgMcp.Tests.csproj -c Release`):

- `EventLogTests`: ordering and bound; `TakeUnseen` atomicity; `Recent`;
  mode transitions per §5.2.1 including the exit-then-design suppression;
  `MarkSeen(instance)` leaving another instance unseen; expectations — consume,
  expire by time, expire by operation, withdraw, End hiding Exited; `Delivered` and `OperationReported` in both orders (entry first,
  reply first); `Subscribe` sees non-hidden entries only; InstanceGone once.
- `EventLinesTests`: one test per row of §5.4, including missing frame parts,
  the instance prefix, and the `N min ago: ` prefix appearing only past a minute.
- `EventBusTests` additions: output buffering, multi-line split, per-session
  clearing, reported-once, regex options.
- `ShimIntegrationTests` (FakeHost over the real pipe) additions: FakeHost gains
  `RaiseOperationChanged`, `RaiseWorkspaceChanged`, `RaiseOutput` and `Drop()`
  (closes the pipe). Tests: a pushed operation appears in the next reply's
  digest, and not when the reply reported it; a stop wait returns
  `session-ended` on an external →design and `instance-gone` on `Drop()`;
  `for='output:'` answers from the buffer and from a later push; `for='any'`
  returns and marks seen; `status` carries Recent and no digest.
- Digest filter test through the MCP pipeline (an in-process client against the
  server over a stream pair): the block is at index 0, ends with a blank
  line, is present on an error result, absent when nothing happened, and leaves
  `StructuredContent` intact.
- `--follow` test: the shim executable started as a process against FakeHost;
  one pushed stop yields one stdout line; `--match` yields an `output:` line;
  the process exits when stdin closes.
- Host: `OperationRegistryTests` — `Completed` raised exactly once per
  operation, on both the terminal flag and `ObserveIdle` paths, outside the lock.

Live (`tests/live/run.ps1`, dedicated profile): a real build completing while the
shim idles; Shift+F5 in the IDE ending a stop wait; closing the window.

## 9. Documentation to update

- `README.md`: `wait` forms, the digest, `status` recent, `--follow`.
- `CHANGELOG.md`: under Unreleased.
- `docs/design.md`: §5 "Waiting is a first-class operation" gains the digest and
  the widened `wait`; §15 drops the recent-events question; §13 gains the
  channel push as deferred with a pointer to §5.10 here.
- `docs/marketplace.md`: one paragraph.
- Tool descriptions and `Program.Instructions` are part of the change itself.

## 10. Out of scope

- Console stdout in `for='output:'`. The host reads the debuggee's console by
  scraping its screen buffer on demand; there is no line-arrival hook. Adding one
  means a host-side poll of the buffer while an output wait is armed. Deferred;
  the tool description says the Debug pane is what is watched.
- The channel push itself (§5.10).
- Cross-process event ids. Identical wording and timestamps are enough for the
  model to correlate a monitor line with a digest line.
- Version bump in `Directory.Build.props`; that is a release decision.

## 11. Decisions made without prior discussion

Flagged for review rather than asked one by one:

1. Attribution is by expectations with deadlines, tied to the operation for
   `launch` (§5.2.2). The alternative — log everything and let the model sort
   it out — contradicts "never echo the model's own actions".
2. `status` marks its own instance's entries seen and carries `Recent` for that
   instance, so nothing is reported twice on one reply and another window's
   events still reach the digest.
3. No same-kind collapsing in the digest, only the 6-line cap: stops cannot
   accumulate without the model resuming between them.
4. `--match` on `--follow`. Small, and it is the use the monitor pattern is made
   for. Drop it if it reads as scope creep.
5. `SolutionChanged` kept in the always-on set; the host method already exists
   and the host change is two lines.
6. `restart` registers End then RunStart, so neither the old process's exit nor
   the new run is echoed.
7. One format for every list of entries — oldest first, `N min ago: ` only past a
   minute — so the digest and `status` recent are the same rendering.
