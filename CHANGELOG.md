# Changelog

## Unreleased

- Tracepoints can keep their records out of the Debug pane: `bp_set(logMessage: ...,
  collect: true)` buffers them per breakpoint and `trace_read` returns that one
  breakpoint's records, each stamped with the time it arrived and which hit it was.
- `bp_set` evaluates each `{expr}` in a tracepoint message once and reports which will
  work, or says it could not check because the debuggee is not stopped there.
- `bp_set` takes `everyNthHit`, which is the debug engine's own hit filter, and
  `maxPerSecond`, which only keeps the collected stream readable.

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
