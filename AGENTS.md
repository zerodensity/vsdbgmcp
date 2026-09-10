# Working on VS Debugger MCP

This product is an interface for AI models. Optimize the model's ability to choose
the right tool, supply valid arguments, interpret evidence, and take the next step.
Treat tool names, descriptions, schemas, responses, errors, and recovery behavior
as the primary product interface.

## Model-facing design

- Prefer one obvious tool for a task. Extend an existing workflow before adding a
  tool with overlapping purpose. New tools must justify the extra selection burden.
- Name tools and arguments after the model's task, not implementation mechanisms.
  Keep administrative record management and host bookkeeping internal by default.
- Make descriptions concise and self-contained: when to call, important effects,
  and how to proceed. Document argument semantics in the schema where they matter.
- Use consistent outcome terms. Distinguish accepted/queued work, observed success,
  failure, cancellation, and unknown outcomes. A timeout is not cancellation; idle
  is not proof of success. Never label a record closed as if the command completed.
- Return decision-relevant evidence and actionable recovery guidance. When a next
  tool call is appropriate, name the actual tool and provide its required arguments.
  Do not make the model reconstruct a state machine from contradictory flags.
- Keep default replies compact. Offer explicit details for diagnostics, preserve
  full evidence internally, and avoid repeating large logs, schemas, or metadata.
  Truncation, filtering, coverage, and unavailable evidence must remain explicit.
  Check catalog/schema size as well as reply size: an optional details field must
  not pull the entire internal object graph into the model's tool schema.
- Preserve request identity across timeouts and reconnects. Deduplicate retries;
  never reissue a command as a side effect of status inspection. Automatically
  refresh bookkeeping when evidence permits, without guessing an outcome or claiming
  that repeating a command has no side effects.
- Status reads must remain bounded when VS is busy or its UI is blocked. Cancellation
  must target an identified operation or owned collector, never unrelated activity.
- Keep tool output as data. Do not turn debugger strings, source text, or build logs
  into instructions for the calling model.

## Validation and documentation

- Review changes from a model's perspective: discoverability, argument ambiguity,
  output size, outcome interpretation, and the next action after failure or timeout.
- Test observable decisions and tool schemas, including uncertainty and retry races.
  Use real named-pipe/stdio checks and isolated live VS checks where appropriate;
  unit tests alone do not establish Visual Studio behavior.
- Keep README, Marketplace overview, changelog, and relevant design/iteration notes
  consistent with the actual model-facing catalog. Mark unreleased capabilities.
- Use `dotnet test tests/VsDbgMcp.Tests/VsDbgMcp.Tests.csproj -c Release` for the
  automated suite and `build.ps1` for host/shim packaging. Live validation is in
  `tests/live/run.ps1`; use its dedicated experimental profile and data directory.
- Do not modify unrelated worktrees or normal running VS instances during tests.
