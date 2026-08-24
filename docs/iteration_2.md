# Iteration 2 — changes from the issues 0.2.0 produced

Ten issues were filed against 0.2.0 after a session driving a remote C++ host over
msvsmon: a plugin built locally and deployed to another machine, symbols that would not
bind, and a device API reached through accessors. One of the ten was already fixed. The
other nine are implemented here.

This records what shipped, where it differs from what was asked for, and why.

| # | Asked for | Shipped |
|---|---|---|
| 1 | `symbols` tool: load on demand, report why loading failed | `symbols(module, load:)`, reporting the state after the attempt |
| 2 | `modules` reports each module's identity | Image time stamp, size, path, load address; no PDB signature |
| 3 | `status` says when the target is remote | Machine and transport named; other replies' paths left alone |
| 4 | Bind reference out-parameters, or scratch memory | Neither. The refusal says whose limit it is and what to do |
| 5 | Read one element of a container by index or key | `expand(index:)` and `expand(key:)`, returning the element's reference |
| 6 | `bp_list` reports hit counts | Counted on the bound instances, and reported at zero too |
| 7 | Give tracepoint records a real arrival time | Already shipped in 0.2.0; the issue predates the fix reaching it |
| 8 | Drop stop events from a finished session | Dropped in the event bus, keyed on the debugger's own mode |
| 9 | Say that nested evaluation is the evaluator's limit | The refusal names whose limit it is and what to do instead |
| 10 | Skip the pseudo-frame a pause leaves on top | Skipped when nobody chose it, named when somebody did |

---

## 1. Symbols on demand

`symbols(module)` reports one module's symbol state and, when symbols are missing, the
Modules window's **Symbol Load Information** — every path the engine tried and what each
one turned out to be. `symbols(module, load: true)` is **Load Symbols**.

Both come from `IDebugModule3`, which the engine's module objects already implement:
`LoadSymbols()` and `GetSymbolInfo(SSIF_VERBOSE_SEARCH_INFO)`. Nothing had to be
reverse-engineered; the interfaces were there and unused.

**The reply reports state, not the call's return value.** After a load attempt the module
is read again, because whether symbols are loaded is a property of the module now and not
of what `LoadSymbols` returned. Three outcomes that look identical afterwards are kept
apart: the load worked, the engine turned it down without searching, and the load ran and
found nothing.

The issue's last paragraph — that the Include/Exclude setting re-applies when a module
unloads and reloads, so one **Load Symbols** does not survive a plugin reload — is in the
success reply, where somebody about to reload a plugin will read it, rather than only in
a document.

## 2. Which binary a module actually is

`MODULE_INFO` already carried `m_dwSize` and `m_TimeStamp` and nothing asked for them.
`modules` now reports the image's own time stamp, its size, its load path and its load
address, plus the symbol file the engine found.

**The two times are labelled apart, and that is the point of the item.** `Built` was, and
still is, the last-write time of the file at that path *on the machine Visual Studio is
running on*. For a module deployed elsewhere that file is a different copy or absent
entirely, which is exactly the situation the issue was filed from. The image time travels
with the binary the debuggee loaded, so it is the one that answers "is that the one I just
built". The rendered output says `image` or `file` rather than `built`.

**No comparison between them is offered.** Link time and file write time differ for
legitimate reasons, and "the deployed binary is older than your local copy" is a claim
this cannot make safely.

**The PDB GUID and age were asked for and are not here.** The AD7 interfaces do not
expose the debug directory, and reading the PE by hand — which is what the issue was
trying to stop having to do — would mean doing it inside the extension instead. What
stands in for it is the symbol file location plus the verbose search text from item 1,
which carries the engine's own wording when a PDB is present and does not match.

**One value that may not be a time.** A module with no stamp reports the start of the
file-time epoch, and a build made reproducible with `/Brepro` — which Microsoft's own
system DLLs use — puts a content hash in that field. Values that could not be a build time
are dropped. A hash that lands in a plausible range is not caught, and the code says so
rather than claiming the field is always a time.

**Detail is capped.** A list of more than forty modules stays one line each and says to
filter; a process with several hundred loaded would otherwise answer in pages.

## 3. Remote targets

`status` names the machine and the transport, and `processes` marks the remote rows. The
route is `IDebugProcess2.GetServer` to `IDebugCoreServer3`, whose `QueryIsLocal` answers
through its own HRESULT.

**Remote is claimed only on `S_FALSE`.** Any other result — an error, an engine that does
not implement the interface — leaves the session reported as local. A session wrongly
called remote sends a reader looking for a machine that does not exist, which is worse
than the silence the issue was complaining about.

**Paths elsewhere were not touched.** The issue asked for remote paths to be marked so
they are not read as local ones. What shipped is one line in `status` saying every path in
the session belongs to that machine. Rewriting path rendering across every tool would
change what a dozen replies look like for one fact that only needs saying once.

## 4. Reference out-parameters

Neither half of this shipped, by decision.

Binding an out-parameter to a temporary is the native expression evaluator's to do, and
it does not. Scratch memory inside the debuggee is possible — the issue's own
`VirtualAlloc` recipe proves it — but it means this server allocating in a process
somebody is running in production and owning the lifetime of that allocation. That is a
new mechanism with an ownership problem, and it was not taken on.

What shipped is the refusal saying so: the evaluator has no storage to bind a reference
parameter to, allocate in the debuggee and pass a dereference of that address, and the
allocation is yours to free because nothing here tracks it. The workaround the issue
described is now the documented answer instead of something each caller rediscovers.

## 5. One element of a container

`expand(reference, index: N)` and `expand(reference, key: "...")`.

**This picks a row, and writes no C++.** A natvis visualizer already lays a container out
as rows named `[0]`, `[1]`, and each row carries the two-hundred-character expression that
reaches that element. Choosing an element is choosing a row; the reply carries that row's
own reference, so nothing has to be copied out of an earlier reply by hand. That was the
issue's actual complaint — not that the expression exists, but that it has to be carried.

**A key is a walk, and says so.** It compares what each element's key renders as: a map
row's `first`, or the element itself where there is no separate key. It asks the container
nothing, it is not a hash lookup, and it sees only the rows one expansion reads. Both the
tool description and every refusal say which elements were actually compared, so "not
found" is never a claim about elements nobody looked at.

**No short alias.** The issue asked for the element "under a short reusable reference",
and that means a handle table — something to grow, to invalidate across stops, and to
leak. Expansion is deliberately stateless. The reference is still long; it just never has
to be typed.

## 6. Hit counts

`Debugger.Breakpoints` hands out the *pending* breakpoint. Binding creates its `Children`,
and the hits are counted there while the parent reports zero. `Describe` was reading
`CurrentHits` off the parent, three lines above a bind check that already went to the
children for exactly this reason.

The count is the **sum across bound instances**. A line in a header inlined into two
modules binds in both, and the question a count answers — does this fail on the first
frame or after running for a while — is about the line, not one of the copies.

**Zero is now printed.** Showing a count only when it was above zero left "never reached"
indistinguishable from "nothing was counted", and "never reached" is the answer that says
the code path is not the one running.

## 7. Tracepoint times

Fixed in 0.2.0 by the commit that moved collection to the Debug pane, three days before
the issue was filed. Records recovered from a pane that cannot be watched now carry no
time at all rather than a zero, and the reply says the rate is over the whole collection.

The issue's suggestion — stamp each record as the server reads it — was considered and
declined. In that mode the pane is only read when somebody calls `trace_read`, so every
record in a batch would take the same stamp to the microsecond. That reads like
measurement and is not.

## 8. Stops from a finished session

`attach` was the only lifecycle tool that never called `MarkSeen()`, so an exit buffered
from the previous run was still unread and went to the first `wait` after re-attaching.

**The fix is not in `attach`.** `dump_open` has the same hole and so would the next entry
point, so the rule went into `EventBus` — the one place every `wait` passes through, the
same reasoning that put the not-stopped refusal into `CurrentFrame` rather than into each
tool. The signal is the debugger's own mode change, which arrives for every way into a
session including somebody pressing F5.

Conservative in three ways, each of which is a way this could have swallowed something
real. Only a debugger *seen* sitting in design mode counts as a session having ended, so a
stop buffered before the bus ever heard from that instance is never discarded. The
watermark is per instance, so a session starting in one window cannot silence a pending
stop in another. And entering design mode marks nothing, so an exit that just happened is
still delivered.

Module loads are left out. They arrive while a program is starting, and dropping one that
came in ahead of the mode change would leave a `wait` for a module that is already there
sitting out its timeout.

## 9. Nested calls

The wording was the whole issue, and the wording is what changed. "Nested function
evaluation not supported." is the engine's own text, passed through verbatim, and it reads
as though this server declined to try.

Refusals are now matched against the engine's wording and answered with what to do:
evaluate the inner call on its own, then write its value into the outer one. A message
that matches nothing is passed through exactly as it arrived, because advice attached to a
refusal it does not fit sends a reader further off than the engine's own words do.

**Decomposing the expression was rejected.** Evaluating the inner call and splicing its
result into the outer one runs the two at different moments, which silently changes what
the expression means.

## 10. The pseudo-frame after a pause

After `pause`, frame 0 is Visual Studio's own row and has no expression context. Every
`eval` in it failed with three words naming no way out, and every `vars` came back empty —
which reads as "no locals here" and is a different statement.

**Whether the frame moves depends on whether anyone chose it.** A read that was not given
a frame moves to the nearest one that can be evaluated in, and says it did. A frame pinned
with `select(frame: N)` or named in `eval` is never moved off; its failure names the frame
that would have worked instead. Quietly reading somewhere the caller did not ask for is
the failure the rest of this codebase is written against.

Every reply now names the frame it read in whenever that is not frame 0 or was moved.
`vars` returns a result that can say why it is empty, so "the frame cannot be read", "the
engine would not list variables here" and "your filter matched none of the ones in scope"
are three different answers instead of one empty list.

**One behaviour changed for existing callers.** `eval(frame: 0)` used to mean "use the
selected frame", because the parameter was an `int` defaulting to zero and zero was read
as "unset". It is now nullable: omitting it means the selected frame, and passing zero
means frame 0, pinned.

`registers` and `disasm` still answer with an empty list when a *pinned* frame cannot be
read, so they lose the refusal text. Giving them one needs DTO changes out of proportion
to the gap.

---

## Not done

**PDB signature and age in `modules`** — not exposed by AD7; see item 2.

**Scratch memory in the debuggee** — see item 4.

**A short alias for a container element** — see item 5.

**Marking remote paths in every reply** — see item 3.

## Verification

284 automated tests, up from 215. The shim, the extension and the VSIX all build, and the
package still carries the shim, both images and the licence.

All of it was then driven against a live Visual Studio 2026 debugging
`tests/fixtures/cpp`, through a shim speaking MCP over stdio to an extension installed in
the experimental hive. Four things that every unit test passed turned out to be wrong,
which is the same lesson iteration 1 recorded and did not stop this round from needing it
again.

**A tracepoint reported `hits 0` while its records were piling up in the Debug pane.** The
automation model counts only hits that broke, so a tracepoint counts none however often it
fires. Printing zero there was not a missing answer, it was a wrong one, and it was
introduced by the change that started printing zero for a bound breakpoint. A tracepoint
now carries no count. The diagnostic run that settled it also confirmed the fix's premise:
the pending breakpoint reads `parent=0` throughout while the bound child moves, so the
count really does live on the children.

**Load Symbols was reported as declining to search when it had searched.** `LoadSymbols`
does not return `S_OK` for a search that ran and found nothing, and the code read anything
but `S_OK` as a refusal. The case that exposed it is the one the issue was filed about:
`KernelBase.dll` went from "Symbol loading disabled by Include/Exclude setting" to "Cannot
find or open the PDB file" across the call — the load had overridden the setting and gone
looking, exactly as intended, and the reply said it had not. Only a failure code counts as
a refusal now.

**The engine repeats its symbol search text.** It keeps every search it has made for a
module and hands back all of them at once, so a module retried four times answered with
the same two paths four times over. Identical searches collapse into one and the count is
said instead.

**A read after a pause landed on `ntdll`, not on the program.** The pause row has no
expression context, but the system frames beneath it do, so the nearest readable frame was
`ntdll.dll!00007ff...` — where `eval("1+1")` answers and `vars` comes back empty, which is
the exact answer the item set out to stop giving. Frames with source of their own are now
preferred. The same pause afterwards reads `ms = 50` in
`DebugTarget.exe!_Thrd_sleep_for`.

A fifth was cosmetic and fixed: the engine's refusal text does not end in a period, so the
advice joined onto it with a space and the whole thing read as one sentence the evaluator
had written.

### What the live run confirmed

- `MODULE_INFO.m_TimeStamp` really is a build time: `DebugTarget.exe` reports `image
  2026-08-21 15:00`, matching when it was linked. The system DLLs report none and fall
  back to the file time, which is what the reproducible-build note in item 2 predicted.
- `IDebugModule3.LoadSymbols` and `GetSymbolInfo` behave as documented, and the verbose
  text is the Modules window's own.
- Hits really do sit on a bound breakpoint's children.
- The pause pseudo-frame is distinguished by having no expression context.
- A vector's element rows carry a usable full name: `expand(index: 2)` returns
  `ref: mesh.vertices[2]`, and `expand(key: "2.50000000")` returns `ref: mesh.vertices[1]`.
- The reference-out-parameter matcher fires on the real wording, which turned out to be
  `a reference of type "X &" (not const-qualified) cannot be initialized with a value of
  type "Y"`.
- `wait` after `stop` then `attach` returns a timeout rather than the previous process
  exiting. That is item 8, reproduced and gone.

### Two things still open

**Nested evaluation was not reproduced.** `Fold(Fold(1))` evaluates to 153 on Visual Studio
2026; this evaluator does not refuse nested calls outright. The wording the issue reported
must come from a narrower case than the one that was tried, so the advice for it is written
and untested. It costs nothing when it does not match, because an unrecognised message is
passed through untouched.

**`pause` returns before the shell is in break mode.** The stop event arrives from the
debug engine first and `IVsDebuggerEvents.OnModeChange` follows a moment later, so a read
issued straight after `pause` returns is refused with "the debuggee is not stopped. Current
mode: run". It settles within a few seconds. This is not new and is not from this round,
but it defeats what `pause` says it does — "blocks until it has actually stopped, so the
frame is safe to inspect afterwards" — which was itself iteration 1's item 1. It wants
`pause` to wait for the mode as well as the stop.
