# Iteration 1 — changes from an agent session's feedback

One long agent-driven debugging session (August 2026) drove an Unreal Engine 5.8 editor
and the Nodos engine together, two Visual Studio windows at once, C++ throughout, built
RelWithDebInfo or Development so most frames were optimized. The work was real — a
cross-process GPU synchronization protocol, a double free, an audio pipeline — and the
session produced eight findings, each one something that had cost calls, produced a wrong
conclusion, or both.

All eight are implemented. This records what shipped, where it differs from what was
asked for, and why.

| # | Asked for | Shipped |
|---|---|---|
| 1 | Refuse evaluation when the target is not stopped | One refusal at the frame lookup, and `pause` blocks |
| 2 | Report source/PDB mismatch as the bind failure it is | Reported from build times, not PDB checksums |
| 3 | Tracepoints get their own sink, timestamped and counted | `bp_set(collect: true)` and `trace_read` |
| 4 | Validate tracepoint expressions at bind time | Evaluated once, or told plainly it could not check |
| 5 | Mark which locals are readable in optimized frames | Unreadable marked, shared slots marked, liveness not attempted |
| 6 | `typeModule` for expressions | `typeModule` on `eval` and `expand` |
| 7 | `wait` on module load | `wait(for: "module:NAME")`, on a stream of its own |
| 8 | Annotate allocator fill patterns | Named beside the value and in memory dumps |

---

## 1. Reading a debuggee that is not stopped

The report blamed `eval`. `eval` was already guarded. The bug was one level down and
wider: `vars`, `expand`, `memory`, `registers` and `disasm` all reach the debuggee
through `CurrentFrame()`, which resolves the thread the sink last saw stop — and that
pointer outlives the stop. Reading through it while the program runs shows where that
thread used to be. Members come back null, pointers come back `???`, and nothing in the
reply says the values are old.

So the refusal went into `CurrentFrame()` rather than into each tool. Every read passes
through it, including whatever tool is added next.

Three refusals, in order: not in break mode, no current thread, or the current thread's
process has left the session. Each names the state and what to call instead.

**What it still cannot do.** Break mode is a property of the whole Visual Studio window,
not of one process. With a launcher and an editor in one session, either one stopping
puts the window in break mode. Neither DTE nor the debug interfaces expose per-process
break state, so a still-alive process that is running can still be read through its last
frame. The mode check is the floor, not a proof. The comment above the check says so.

`pause` now blocks until the stop lands and reports where, the way `step` already did.
Returning "Break requested." was itself an invitation to read a running process.

## 2. Source edited after the binary was built

The report asked for the PDB's recorded source checksums. That means driving `msdia` COM
from inside the extension: new interop, CLSID resolution, bitness concerns — a large new
failure surface for one signal.

**What shipped instead compares file times**: the source file's last write against the
owning module binary's last write. The report's own story is "I edited a comment after
the binary was built", and a timestamp is that fact directly rather than an approximation
of it. Nothing in the output claims a checksum was compared, and the field is named for
what it measures.

Bind failures are now explained in order of certainty — no owning module, then no
symbols, then a source file newer than the binary, then the old generic message. A
breakpoint in a file with no loaded module still gets the message it always got.

`modules` carries each binary's build time, and a filtered result says how many modules
it filtered: `1 of 483 loaded modules match 'vulkan'; more can load while the program
runs`. That was the Minor item where an empty filtered result looked authoritative while
the subsystem DLL was moments from loading.

**The accurate route, not taken.** The debug engine raises
`IDebugBreakpointErrorEvent2`, whose resolution info carries the C++ engine's own wording
for a failed bind — including the real "source is different from the version built into
the module" text. `DebugEventSink` already implements `IDebugEventCallback2`, so
listening costs one event case; the cost is correlating error breakpoints back to DTE
breakpoint objects by file and line. It would replace the file-time heuristic entirely.
It is the obvious next move if this one proves too coarse.

## 3 and 4. Tracepoints

Visual Studio writes tracepoint records to the Debug pane, mixed in with everything the
program logs. A collected tracepoint now marks its message with the breakpoint's id, the
event sink pulls the marked records back out of the output stream, and `trace_read`
returns that one breakpoint's records. No new Visual Studio API — the sink already
received those events.

Each record carries the time it arrived and which hit it was. The hit number is counted
**before** the rate cap, so a gap in the numbering is what makes a dropped record
visible. `trace_read` computes the rate, which is the question the report was reduced to
guessing at.

**Rate limiting, split honestly.** `everyNthHit` maps onto the debug engine's own hit
filter, so the message and its expressions are built one time in N — though the thread
still stops on every hit to be counted, so it cuts the overhead rather than removing it.
`maxPerSecond` drops records at the sink; the program has already paid for a record by
the time it is dropped, so it makes the stream readable and does nothing about
instrumentation distorting a measurement. Both descriptions say exactly that. The report
asked for a per-second cap as a way to instrument without distortion, and it cannot be
one.

Bind-time validation evaluates each `{expr}` once and reports the evaluator's own message
per expression. It refuses to claim what it did not establish: separate deferral reasons
for a data breakpoint, for not being in break mode, for having no current frame, and for
being stopped somewhere other than where the tracepoint sits.

## 5. Optimized frames

Two facts now reach the caller. A variable the engine could not read is marked as such
rather than looking like a value. Variables in a frame that resolve to the same address
are marked with each other's names — the case where `this` and `profile` both read as one
pointer and nothing said so.

**Liveness at the current IP was not attempted.** The report says both facts are in the
debug info, and they are, but reaching them means DIA. What shipped is "the engine could
not read this" and "these names read one address", which is what the session actually
needed.

One honest limit: the address comes from the value's memory context, so two genuinely
distinct pointers to the same object are flagged too. The wording is "same address", not
"aliased".

## 6. Module-qualified expressions

`typeModule` on `eval` and `expand`. Visual Studio's `{,,module}` qualifier only reaches
the token directly after it, so a type inside a cast never sees it.

Rather than one hardcoded rewrite, `ModuleQualifier.Forms()` returns the candidate
spellings best-first and the native parser picks: whichever form parses is used, and on
total failure the error belongs to the form the caller actually wrote. Two shapes
rewrite. Casts whose operand is not a plain literal or identifier — `((T*)p + 1)->m`,
`((T*)f(p))->m` — are deliberately declined, because dereferencing the front of those
binds to the wrong half. A wrong rewrite is worse than no rewrite.

## 7. Waiting for a module

`wait(for: "module:NAME")` returns when a matching module loads, and immediately if it
already loaded. It carries the module's path and whether symbols came with it, since an
unbound breakpoint in a freshly loaded module is usually a symbol problem.

**A plain `wait()` structurally cannot see a module load.** Stops and module loads are
separate buffers with separate waiter lists inside `EventBus`, and the stop path never
reads the module one. That is deliberate: the whole loop depends on `wait` meaning "the
debuggee stopped", and a filter is something that can be got wrong later.

The module stream does not use a moving cursor the way stops do. Each buffered load
carries a reported flag instead, because a cursor would mean that waiting for one plugin
skips past another that loaded a moment earlier — and arming breakpoints across several
plugins and waiting for each in turn is exactly what this is for.

## 8. Allocator fill patterns

`0xcd`, `0xdd`, `0xfd`, `0xfe`, `0xcc`, `0xab`, `0xbaadf00d`, `0xfeeefeee`, named beside
the value and as runs in a `memory` dump.

The judgement is in what does *not* match. A value is tagged only when every byte of it
is the fill, minimum two bytes wide: `0x00000000000000dd` is the number 221 and gets
nothing. Values are scanned token by token, so `{EventSemaphore=0xdddddddddddddddd {...}}`
is tagged on its inner literal — the report's own case — while nothing that merely
contains those bytes is. A wrong "this was freed" sends a reader further off than silence
does.

Beyond what was asked: the debugger prints integers in decimal, so an uninitialized `int`
reads `-842150451`, not `0xcdcdcdcd`. The exact signed and unsigned renderings are matched
as a closed set of whole tokens.

`0xed` and `0xdeadbeef` were left out — the first was not asked for, the second is a user
convention rather than an allocator's.

---

## Not done

**A default process filter.** The second Minor item asked for tools to default to "the
process that stopped" in a session holding several. That silently changes what existing
calls return, which is a decision for the repository's owner rather than a consequence of
this feedback.

**Per-module staleness without a breakpoint.** `modules` marks a module stale only once a
breakpoint exists in one of its source files. Enumerating every project's sources on each
`modules` call is too expensive on a solution the size of Unreal's. The build time is
there for an agent to read before it spends a breakpoint.

## What was explicitly protected

The report's closing section named four things not to regress. All four still hold:
`wait` blocking on real debugger events, `threads` grouping, the exception summary naming
the faulting expression, and `eval` calling functions under `allowSideEffects`.

## Verification

211 automated tests, up from 72. The shim, the extension and the VSIX all build.

None of this has been driven against a live debuggee yet. The parts that decide
something are pure and tested — which expression forms to try, which values are fill,
whether a source file outran its binary, what a trace buffer keeps, how the event streams
separate. The parts that need a real debug engine have not been seen happen: a tracepoint
record actually arriving, `dbgHitCountTypeMultiple` on a live tracepoint, a breakpoint
failing to bind on a stale file, two locals sharing a slot.
