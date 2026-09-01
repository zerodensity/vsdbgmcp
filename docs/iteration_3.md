# Iteration 3 — changes from a session debugging two processes

The report behind this one is a retrospective of a long session debugging a media engine
and an integrated Unreal application through two Visual Studio windows. It is unusual in
that it is honest about its own wrong turns: five confident mechanisms retracted, and a
ranked account of what the time actually went on.

Every item below is the same fault. A tool answered a question it could not answer, and
the answer read as evidence. Nothing here is a missing feature; they are replies that
claimed too much. The last two were found while fixing the others and are older than the
report.

## 1. Waiting for a stop that cannot come

Twice in one session a breakpoint was removed, another set, and `wait` called without
resuming. Zero hits followed, and that was reported as a function having stopped being
called. The person who wrote the system caught it both times.

`wait` went straight to the event bus, and the bus answers a timeout with nothing.
`Render.Stop(null)` turned that into "execution did not stop within the timeout. Still
running." Both halves are claims, and both are false in exactly the case that prints the
message most often.

`wait` now answers at once when the last thing an instance told the shim was a stop and
nothing has asked it to run since. The reply names where it stopped and that `go` is what
moves it.

**Only a stop establishes that the debuggee is stopped.** The bus is also told the
debugger's mode, and the mode is the obvious thing to read — it is also the thing
iteration 2 found arriving seconds after the change it announces. A break it reports can
belong to a stop the caller has already resumed from, so a `wait` straight after `go`
would have been answered "you are already stopped" while the program ran. A stop event is
pushed as it happens and reaches the shim ahead of the caller's own `go`, so that is what
sets the belief. Running and design take it away, because believing those costs nothing
worse than a wait that blocks the way it always did.

With `instance: "any"` every connected window has to be sitting still. One window in break
and another running is the case that option exists for, and the running one can still stop.

**It is the floor, not a proof, and the reply says so.** Break mode belongs to the whole
Visual Studio window rather than to one process — iteration 1's first item — so with a
launcher and an editor in one session, either one stopping puts the window in break while
the other keeps running.

The timeout claims nothing now. It says no stop arrived, that this is evidence neither
that the program is running nor that a breakpoint is never reached, and that `status`
reads the mode live.

## 2. Which run an address belongs to

Across the session the editor ran as five different pids and the launcher as four, and an
address was once compared across a restart without anyone noticing.

Every debug session now carries a number. `status` prints it, every stop line carries it,
and `launch`, `attach`, `restart` and `dump_open` say plainly that everything read before
them names nothing now.

**The count moves before the call goes out**, not when the debugger confirms it. Counting
on the mode notification alone gets it wrong in the dangerous direction: the notification
is seconds late, so every stop a restarted debuggee produced in the meantime would wear
the previous run's number while the reply told the caller to compare numbers before
comparing addresses.

**The number is read in one direction.** The same number means the same run. A different
one does not always mean a different run, so it reads as "everything has to be read
again", never as proof that something restarted. A session already under way when the
shim connected has no number: it reports `gen ?` and says nothing here saw it begin.

**Nothing tries to detect a stale address.** The tool cannot know when an address was
captured, and a check that fires sometimes sends a reader further off than a rule stated
every time.

## 3. An empty tracepoint stream

`trace_read` answered an empty stream with "Nothing collected yet: the tracepoint has not
been hit since it was set." It cannot know that. A record that was never written and a
record that never arrived look identical from the buffer, and the sentence turned three
collecting tracepoints — reporting `bound`, collecting nothing, one of them on a line a
thread had just been caught sitting on — into the conclusion that a function was never
executing.

`bp_list` deliberately reports no hit count for a tracepoint, because Visual Studio counts
only hits that broke, so the obvious cross-check does not exist. The reply instead says
what an empty stream establishes and reports the evidence that does: how long it has been
collecting; whether the Debug pane is being watched as it fills or its text recovered when
you read; whether any other tracepoint has received a record, since one collecting
normally rules the pipeline in and nothing arriving anywhere rules it out; and what the
breakpoint itself says — disabled, unbound, or filtered by a condition or a hit filter,
each of which keeps a line that really was reached from logging anything.

What would settle it is printed last, after any cause already found, because a cause found
makes the experiment pointless.

**Every collected record is now written between markers at both ends** rather than one in
front. Both are literal text, so the debugger copies them whatever the `{expr}` parts do:
a record arriving proves the line was reached even when nothing in the message evaluated,
and one arriving without its end did not finish. Those are counted and reported apart. The
end marker also tells a finished record from a line still being written, so the pane's last
line is no longer skipped on principle.

**Tools → Options → Debugging → "Redirect all Output Window text to the Immediate Window"
is reported** when a collecting tracepoint has nothing. With that on, Visual Studio writes
the records to the Immediate window, the pane watch reads only the Debug pane, and the
stream stays empty forever with nothing anywhere saying why.

## 4. The stale deployed binary

The most expensive item in the report: several hours, and one entirely wrong conclusion.
A plugin host loads its modules from a deployment directory. A rebuild writes the build
tree and leaves that directory alone, so the process goes on running the old binary while
the debugger matches the new PDB. The module listed as having symbols, the PDB path
resolved, and nothing bound.

**The machinery that existed could not reach it.** Modules were marked stale only once a
breakpoint sat in a file they owned, and the owning module was found through
`Solution.FindProjectItem`. A plugin loaded by a host whose solution does not hold the
plugin's project gets null back, so nothing fired and the bind failure fell through to the
generic answer. It was also comparing against the file at the module's path on this
machine, which for a deployed module is a different copy or nothing.

**What shipped needs no source at all.** For each loaded module, the time it carries is
compared against the build sitting where its symbols came from — the directory of the PDB
the engine matched. Two names are looked for there: the image's own, then the symbol
file's name with the image's extension, because a project can name its PDB differently
from its output. Where the symbols came from the image's own directory nothing is said,
because both names in one directory would then be some other project's output sitting
beside it.

This is O(modules), not O(sources), which is what iteration 1's "Not done" declined.

**The two times are kept apart and so is what they prove.** Where the loaded file can be
read here, two write times are compared. Where it cannot — a module deployed to another
machine — the image's own stamp is compared instead, at a much wider margin, because a
stamp is written when a binary is linked and a file's time when it is written, and one
build leaves two times however long the link took apart. Neither claims more than the pair
supports.

Said where it bites: in `modules`, in the reason a breakpoint gives for not binding — ahead
of the source times, because rebuilding is the advice those give and rebuilding is exactly
what does not fix this — and in `bp_set`'s own reply.

## 5. A container the visualizer renders as empty

`TMap` and `TSet` rendered `Empty` for maps that were demonstrably iterating entries, and
an empty list is the one wrong answer a caller cannot see, because it reads as an answer.

Where a summary claims there is nothing inside and the expansion produced no element rows,
the same value is read again with the `,!` specifier and both views come back.

**No verdict is drawn from those fields, and that is the point of the item.** A capacity is
spelled the same way as a size: an empty `std::deque` keeps `_Mysize` at zero beside a
`_Mapsize` of eight, and an empty `TSet` keeps an allocated hash. Calling either a
disagreement would be this inventing one. The fields are reported in two groups — not zero,
and zero — and the reader judges. The one thing said outright is that every field reading
like a count is zero, and even that is only said when the whole raw layout was read: rows
the depth limit did not reach are counted and reported, because silence about those is the
same failure one level down.

Nothing here knows one library's layout from another's. A field is called a count because
of its name, the reply says the name is the only reason, and the names that end in one of
those words and mean nothing of the kind — `bUnused`, `TypeEnum` — are listed as exclusions
rather than dressed up as a rule.

## 6. A cast resolving in the wrong module

The report calls this the dangerous one, because it looks like data rather than like an
error: an `AActor*` printed as `Name="STAT_ExplicitViewDescriptors"`.

`eval` and `expand` now refuse a `typeModule` naming a module the debuggee has not loaded,
listing the loaded names closest to it. Near misses are found by squashing the separators
nobody agrees on — `nosSysVulkan.dll` finds `nos.sys.vulkan.dll` — and by the loaded names
that contain what was asked for, in that direction only, since `user32.dll` is not what
anybody meant by `MyUser32Wrapper.dll`.

**It catches a name that is wrong or misspelled, and it does not catch the incident that
happened.** A cast written with no `typeModule` at all is the same wrong answer reached by
not asking, and nothing here can see it: there is no second reading to disagree with the
first. The doc comment, the refusal and the tool description all say so rather than
implying a check that does not exist. No attempt is made to judge whether a returned value
looks like nonsense.

## 7. Enumerating a raw array

What actually broke the real case open was dumping all 28 of `RawParams->Pins[i]->Name`
and seeing a duplicated block of twelve names the container view had already hidden. That
cost 28 calls.

`eval` takes `count` and `member`, and returns one row per index. The reply counts how many
of the values were distinct — the discriminating measurement, which is the difference
between seeing the repeated block and scrolling past it.

Capped at 256, and the run stops early when the first three indexes fail identically,
because a wrong expression fails the same way at every index and each read runs the engine
synchronously inside Visual Studio. Both reasons for a short run are named separately, so a
run that gave up is never reported as the cap.

It went on `eval` rather than `expand` deliberately. `expand`'s contract is that it picks a
row and writes no C++ — every element it returns is one a visualizer already produced.
Enumerating a raw array is the opposite: there are no rows to pick, and the tool has to
write `(base)[i]` and a member path itself.

## 8. A per-second cap that was never applied

Found while working on item 3, and older than any of this. A cap counts records into one
second and then the next, and a record with no time belongs to neither. On the
pane-recovery path every record arrives undated, so the window never rolled: the first N
records were kept and everything after them was thrown away for as long as the program
ran, under a heading calling itself a rate.

The cap is no longer applied to an undated stream. Everything is kept, the buffer holds
the newest 2000 as it always did, and the reply says the cap was not applied and points at
`everyNthHit`, which the debug engine counts and which therefore works whether or not a
record can be dated.

## 9. Which process a module list belongs to

`modules` read the process that stopped while `eval`, `vars`, `memory` and the rest follow
`select`. In a session holding a launcher and what it started, that is a module list and an
expression evaluation about two different programs, with nothing saying so.

`modules` now follows `select` like every other read, and the reply names the process the
list came from. The name is the more useful half: the inconsistency was invisible, and a
list that says whose it is cannot be silently mistaken for another's.

## Not done

**`symbols` still reads the process that stopped.** It has exactly the shape of item 9 and
was left alone deliberately, because changing what a second call returns was not what was
decided and the two can be settled together.

**`bp_set` does not carry the Immediate-window warning.** With the setting on, that is the
moment it is worth knowing, before any wrong conclusion is drawn.

## Verification

495 automated tests, up from 347. The shim, the extension and the VSIX all build.

Four things were built in parallel, in separate worktrees, each reviewed by an agent that
had not written it. The reviews earned their place: one caught a stale-deployment false
positive firing against this repository's own build output, where a PDB sits beside a
sibling project's binary of the same name; one caught a new run's first stops carrying the
previous run's number, which is the failure the item was built to prevent, in the direction
that misleads; and one compiled the container decisions into a throwaway harness, fed them
real layouts, and found the "the two views disagree" verdict firing on an empty
`std::deque` — a false alarm on exactly the family of types the item exists for.

Then all of it was driven by hand against Visual Studio 2026 debugging
`tests/fixtures/cpp` in the experimental hive, and the three things that were reasoned
rather than measured were the three worth driving. Two of them were wrong.

**`wait` answering from break was right.** A second `wait` with nothing resumed came back
in 2 ms naming the stop it was already sitting on, where before it blocked for the whole
timeout and then said the program was still running.

**The Immediate-window setting was being read by the wrong name.** Visual Studio's own
exported settings call it `OutputToImmediate`, which is where the name came from;
automation calls it `RedirectOutputToImmediate` and keeps it on the debugger's General
page. So the lookup found nothing and every empty tracepoint reported that the setting
could not be read — honest, and useless. With the name corrected and the checkbox turned
on, a tracepoint on a loop running at twenty hertz across four threads collects nothing
at all and the reply names the setting as the reason. That is the report's own §1.2,
reproduced and then diagnosed in one call.

**The generation number labelled two runs the same.** A shim that connects to a Visual
Studio already debugging reports `gen ?`, correctly. Restarting from there took the number
1 for the new run — and the old process's exit, which arrives afterwards, was stamped 1 as
well, so the process that had just died and the one now running were reported as the same
run. That is the single comparison the number exists to prevent, wrong in the direction
that misleads. An exit that lands while a run is starting now belongs to the run that
ended.

Both were found in the first twenty minutes of driving a real debugger, which is the same
lesson iterations 1 and 2 recorded and did not stop this round from needing again.
