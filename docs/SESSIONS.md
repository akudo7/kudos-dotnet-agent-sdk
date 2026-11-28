# Sessions

A session is one conversation with the agent: a work directory, a chat history held by
`CodingAgentSession`, and an append-only record on disk. This page covers what is recorded, how to resume
and fork a session, how the context is kept within the model's window, which guards stop runaway loops,
and what happens on failure and cancellation. Configuration keys are listed in
[CONFIGURATION.md](CONFIGURATION.md); the component map is in [ARCHITECTURE.md](ARCHITECTURE.md).

## Contents

1. [Lifecycle](#lifecycle)
2. [Rollout Recording](#rollout-recording)
3. [Resume and Fork](#resume-and-fork)
4. [Context Compression](#context-compression)
5. [Loop Guards](#loop-guards)
6. [Tool-Loop Iteration Cap](#tool-loop-iteration-cap)
7. [Verify](#verify)
8. [Audit Chain](#audit-chain)
9. [Failure and Cancellation](#failure-and-cancellation)

---

## Lifecycle

```csharp
await using var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(client)
    .UseResultsDirectory(resultsRoot)          // default: ./results and a new session id
    .UseApprovalHandler(AskUserAsync)
    .BuildAsync();                             // records session-start, starts MCP servers

await foreach (var evt in session.RunTurnEventsAsync("Count the lines of every file"))
{
    // AgentTextDelta / AgentToolCallStarted / AgentToolCallCompleted / AgentCompacted /
    // AgentGuardTriggered / ... and finally AgentTurnCompleted | AgentTurnFailed | AgentTurnCancelled
}
```

- `BuildAsync()` creates the session directory, records `session-start`, connects MCP servers and, if
  `ResumeFrom` was given, seeds the history from a rollout.
- Each turn (`RunTurnAsync`, `RunTurnStreamingAsync`, `RunTurnEventsAsync`) moves `status.json` from
  `running` to `complete`, `error` or `interrupted`.
- `DisposeAsync()` stops the MCP server processes. The session never disposes the chat client: the host
  owns it and may reuse it for the next session.
- With a streaming method the consumer drives the turn. The turn is finalised only when the enumeration
  runs to completion, and with `RunTurnEventsAsync` the next step (for example the approval prompt of a tool
  call) starts only after the previous event was taken.

---

## Rollout Recording

### File layout

```
<resultsRoot>/<sessionId>/
├── work/               # the agent's working directory (tool sandbox root)
│   └── attachments/    # attached images / PDFs and view_image copies (only when used)
├── rollout.jsonl       # one JSON event per line, append-only
├── audit-chain.jsonl   # hash chain mirroring rollout.jsonl line by line
├── status.json         # { sessionId, task, status, timestamp }
└── agents/             # only with sub-agents: agents/<name>/<childSessionId>/ — the same layout per child
```

- Session ids are generated as `yyyyMMdd-HHmmss-xxxx` (UTC, 4 random hex characters). A custom id must
  match `[A-Za-z0-9._-]+` and be at most 64 characters, otherwise `SessionLayout.Create` throws
  `Invalid session id: '<id>' (allowed: [A-Za-z0-9._-]+, max 64 chars)`.
- `status.json` values: `running`, `complete`, `error`, `interrupted`.
- Each event is written synchronously (open, append, close), so a crash loses at most the line being
  written. Lines are serialized with null properties omitted.

### Event types

The `type` field is the discriminator of `RolloutEvent`. Every event has a `timestamp` (ISO-8601 UTC with
milliseconds).

| `type` | Fields | When it is written |
|---|---|---|
| `session-start` | `sessionId`, `task`, `config` | When the session is built (again on every resume). |
| `user-input` | `content`, `attachments` | Live, at the start of a turn. `attachments` (only when files were attached) lists references, never the content: `path` (work-directory relative), `mediaType`, `sha256`, `bytes`, `name`, `width` / `height` (images), `referenceOnly` (a PDF the model could not read — only its path was sent). See [ATTACHMENTS.md](ATTACHMENTS.md). |
| `model-item` | `role`, `content` | At turn end: each assistant message with visible text (protocol tags stripped). |
| `tool-call` | `name`, `args` | At turn end: each tool call of the turn. |
| `tool-result` | `name`, `content`, `isError`, `attachments` | At turn end: each tool result. `isError` is true for a `{"ok": false, ...}` result. A result with images (`view_image`, host tools) records its text in `content` and the images as `attachments` references. |
| `safety-decision` | `operation`, `target`, `decision` (`auto-approve` / `ask-user` / `reject`), `rule`, `detail` (what the operation carries — e.g. the code of an `execute_code` call; absent when none) | Live, for every gate decision — before the operation runs, so the code survives a crash while it runs. |
| `human-intervention` | `approved`, `latencyMs`, `reason` | Live, when the approval handler answered. |
| `compaction` | `summary`, `trigger` (`backstop` / `tool` / `manual`), `outcome` (`summary` / `reset`), `compactedMessages`, `tokensBefore`, `tokensAfter`, `failureReason` | Live, when the context was compacted. |
| `guard` | `kind`, `detail` | Live, when a guard fired (see [Loop guards](#loop-guards) and [Verify](#verify)). |
| `turn-complete` | none | At the end of a successful turn. |
| `turn-interrupted` | `reason` | When the host calls `RecordInterrupted(reason)` or cancels `RunTurnEventsAsync` (reason `cancelled`). |
| `error` | `message` | When a turn failed. |
| `subagent-call` | `agent`, `childSessionId`, `query` (first 2000 characters), `depth`, `slot` (llama-server slot pinning only) | Live, when the model calls a sub-agent (`agent_<name>` / `spawn_agent`). |
| `subagent-result` | `agent`, `childSessionId`, `status` (`ok` / `empty` / `failed` / `timeout` / `cancelled`), `chars`, `truncated`, `durationMs`, `childAuditHead`, `error` | Live, when the sub-agent call ended. `childAuditHead` is the head hash of the child's audit chain. |
| `workflow-start` | `workflow`, `runId`, `kind` (`concurrent` / `sequential`), `participants`, `input` (first 2000 characters), `serialized` | Live, in the rollout given as `AgentWorkflowOptions.Recorder` (package `Kudos.Agent.Sdk.Workflows`), when a workflow run begins. `serialized` = participants share an in-process model. |
| `workflow-step` | `workflow`, `runId`, `participant`, `sessionId`, `status`, `chars`, `durationMs`, `error` | Live, when one participant's turn ended. The turn itself is recorded in the participant's own session (`sessionId`). |
| `workflow-end` | `workflow`, `runId`, `status` (`ok` / `partial` / `failed` / `cancelled`), `durationMs`, `missing`, `error` | Live, when the run ended. |
| `a2a-call` | `agent`, `url`, `contextId`, `taskId`, `state` (`completed` / `input-required` / `failed` / `rejected` / `canceled` / `auth-required` / `message` / `denied` / `error`), `query` (2 000 characters), `chars`, `durationMs`, `error` | The session called a remote A2A agent through `a2a_<name>` ([A2A.md](A2A.md)). |
| `a2a-task` | `taskId`, `contextId`, `state` (`submitted` / `working` / `input-required` / `completed` / `failed` / `canceled`), `requester` (`bearer` / `none`), `remote`, `depth`, `detail` | A state change of a task this session serves over A2A ([A2A.md](A2A.md)). |
| `approval` | `tool`, `decision`, `reason` | Part of the schema and read on resume; the SDK does not write it in 0.7.0. |
| `premature-termination` | `reason`, `autoRetry` | Part of the schema; the SDK does not write it in 0.7.0. |

The MSAF tool loop is internal, so model and tool messages are not seen live. The session replays them
from the difference between the history before and after the turn. Their `timestamp` is therefore the time
of the turn end, and **a crash in the middle of a turn loses that turn's messages** (the `user-input` line
and any live events remain). Resuming continues from the last completed turn.

To watch events as they are written, subscribe to `session.Recorder.Recorded`. `RunTurnEventsAsync` uses the
same hook to surface `compaction` and `guard` events as `AgentCompacted` and `AgentGuardTriggered`.

### Inspecting a rollout in code

```csharp
var events = RolloutResume.ReadEvents(sessionDir);        // file or directory; corrupt lines skipped
var state  = RolloutResume.ReconstructState(events);      // SessionId, Task, Messages, Summary, Approvals, Errors, Turns
var stats  = QualityMetrics.Compute(sessionDir);          // deterministic counters from rollout + status
```

### Redaction

Lines are redacted before they are written: known API-key formats and the credential values the SDK resolved
become `[REDACTED]`, and secret-named tool-call arguments (`api_key`, `password`, `access_token`, …) are masked.
The audit chain mirrors the redacted line. See [SECRETS.md](SECRETS.md#redaction-in-logs-and-session-records).

---

## Resume and Fork

### Resume

```csharp
var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(client)
    .UseResultsDirectory(resultsRoot, sessionId: "20261123-101500-ab12")  // the seed's id: keep appending to it
    .ResumeFrom(Path.Combine(resultsRoot, "20261123-101500-ab12"))         // directory or rollout.jsonl
    .BuildAsync();
```

What happens:

1. `RolloutResume.ReadEvents` reads the rollout. Lines that are not valid events (for example a truncated
   final line after a crash) are skipped. A missing file yields no events.
2. `ReconstructState` collects `user-input`, `model-item`, `tool-call` and `tool-result` in order.
3. `BuildChatHistory` turns them into chat messages: `user-input` becomes a user message; consecutive
   `model-item` and `tool-call` entries become one assistant message (text plus function calls with ids
   `seed_0`, `seed_1`, ...); each `tool-result` becomes a tool message answering the oldest unanswered call.
   A dangling call (turn interrupted mid-call) stays as a pending call; the model re-issues it or answers.
   Recorded attachments are loaded back from the work directory (user message: text + images / PDFs; tool
   result: `[TextContent, DataContent…]`). A file that is missing or no longer matches its recorded SHA-256 is
   replaced by `[attachment missing: <name>]`, and the logger gets a `[resume] … — replaced by a reference`
   warning.
4. The history is seeded into the new MSAF session and a new `session-start` line is recorded.

Things to know:

- **Use the seed's session id** if you want one continuous record. `ResumeFrom` only seeds the history; it
  does not choose where new events go. With a new id the new `rollout.jsonl` starts empty, so a later resume
  of the new session would not contain the seed's history. To branch on purpose, fork first (below).
- The rollout holds the full, uncompacted message stream. A resumed session therefore starts from the full
  history; if it is over the compression trigger, the backstop compacts it on the first model call.
- Guard nudges and steer messages are never stored (they are one-shot), so they are not restored.
- For the in-process model the first call after a resume is always a full prompt evaluation
  ([KV-CACHE.md](KV-CACHE.md)).

### Fork

```csharp
string newRollout = RolloutFork.ForkRollout("20261123-101500-ab12", "20261123-101500-ab12-alt", resultsRoot);
// then resume the copy under its own id
builder.UseResultsDirectory(resultsRoot, "20261123-101500-ab12-alt").ResumeFrom(Path.GetDirectoryName(newRollout));
```

`ForkRollout` copies `<resultsRoot>/<src>/rollout.jsonl` to `<resultsRoot>/<new>/rollout.jsonl` and rewrites
the `sessionId` of every `session-start` line. The source is untouched. It throws when the ids are equal
(`ForkRollout: newSession must differ from srcSession ("<id>")`) or the source is missing
(`ForkRollout: source rollout not found: <path>`). The fork has no `audit-chain.jsonl`; the first session built
on it backfills one from the copied lines.

---

## Context Compression

Long agent runs outgrow any context window. The `contextCompression` section controls three cooperating
mechanisms: a deterministic **backstop**, the model's own **`compress_context`** tool, and a visible
**context-usage suffix** on every tool result.

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Automatic compaction. When false no backstop, no `compress_context`, no usage suffix. A host's `CompactAsync` still compacts ([Manual compaction](#manual-compaction)). |
| `mode` | `autonomous` | `autonomous`, `handoff` or `backstop` (exact lowercase; anything else fails validation). |
| `trigger.fraction` | `0.7` | Backstop fires when the estimate reaches this fraction of the effective window. Must be in (0, 1]. |
| `keep.messages` | `6` | Most recent messages kept verbatim after a summary. |
| `keep.maxTokens` | `20000` | Budget for the kept messages; older kept messages are truncated with `…[truncated for context compression]`. |
| `modelContextWindow` | `262144` | Nominal window. `config.WithEnvOverrides(env)` replaces it with `models[0].config.contextWindow` when set, then with the `CONTEXT_WINDOW` environment variable. `KudosAgentConfig.Load` alone does not apply either override. |
| `overheadTokens` | `45000` | Fixed per-request overhead (system prompt, tool schemas) subtracted from the window. |

Effective window = `max(modelContextWindow - overheadTokens, 1)`. With the defaults the backstop fires at
0.7 x (262144 - 45000) ≈ 152,000 estimated tokens.

> For an in-process model, set the window to the `contextSize` you load with (`modelContextWindow`, or the
> model's `config.contextWindow` together with `WithEnvOverrides`) and pick `overheadTokens` well below it.
> The compression window is **not** derived from `contextSize`. See [TROUBLESHOOTING.md](TROUBLESHOOTING.md#compaction-runs-on-every-model-call-or-never).

### Modes

| Mode | Backstop | `compress_context` tool | Usage suffix | Prompt section |
|---|---|---|---|---|
| `autonomous` | yes | registered | size + directive | included, with the budget filled in |
| `handoff` | yes | registered | size + directive | included |
| `backstop` | yes | not registered | size only | removed |

In 0.7.0 `handoff` behaves exactly like `autonomous`; only `backstop` changes behaviour.

### Triggers

- **Backstop.** Before every model call `CompactingChatClient` estimates the size of the model's view
  (CJK character = 1 token, other characters ≈ 4 per token; tool-call arguments and tool results count). At
  or above the trigger it compacts before sending. The event has `trigger: "backstop"`.
- **`compress_context`.** The model calls it (optional `reason` argument); the tool only sets a pending flag
  and replies `Context compression requested — it will be applied before the next model call. Continue the
  task.` The compaction runs before the next model call, with `trigger: "tool"`. If a compaction happened
  right before the step in which the model called it, the call is a no-op and says so. With compression
  disabled the tool returns `compress_context is not available (context compression is disabled)`.
- **Manual.** The host calls `CompactAsync` between turns; the compaction runs at once, with `trigger: "manual"`.
  See [Manual compaction](#manual-compaction).

### What a compaction does

1. Keep the last `keep.messages` messages; summarise everything older except system messages.
2. Ask the model for a handoff summary: a direct call to the model client (bypassing guards and
   compaction) with the older messages, the previous summary if there is one, and a fixed instruction.
   Images and PDFs in those messages are sent as their reference sentences
   (`[image: plot.png 640x480 — attachments/plot.png]`), not as content. An
   unusable reply (shorter than 20 characters, or one that continues the task with tool calls) is retried
   once.
3. Replace history with `[system: "Previous conversation summary:\n\n" + summary, ...kept messages]`. Tool
   call / result pairs are never split; stale usage suffixes are removed from kept tool results. When the kept
   messages exceed `keep.maxTokens`, the oldest kept messages' images and PDFs become reference sentences first
   (the model can look again with `view_image`); text is truncated only after that. An image counts as
   `contextCompression.imageTokens` in every estimate, a PDF as its pages x `pdfPageTokens`.
4. If summary generation fails, fall back to a full reset: a single user message carrying a deterministic
   reset summary and the first 2,000 characters of the original task. The event has `outcome: "reset"` and a
   `failureReason`; the logger gets a `[compaction] handoff summary generation failed — full reset fallback:`
   warning.
5. Record a `compaction` event, and surface it as `AgentCompacted`.

During the turn the compacted view lives in `CompactingChatClient` (MSAF keeps its own uncompacted list). At
turn end the session stores the compacted view, so the next turn starts from it. A failed turn discards it.

### Manual compaction

A host can compact the conversation now, between turns — for a `/compact` command, or when the user changes topic
in a long conversation with a remote model (every call sends the whole history, so a shorter one costs less):

```csharp
var compaction = await session.CompactAsync("keep the decisions about the public API");   // the argument is optional
if (compaction is null)
{
    Console.WriteLine("Nothing to compact.");   // one turn only, or the summary would not be smaller
}
else
{
    Console.WriteLine($"~{compaction.TokensBefore} → ~{compaction.TokensAfter} tokens ({compaction.Outcome})");
}
```

- Everything before the latest turn (the last user message and what followed it — tool calls included) is summarized
  as in steps 2–3 above and the stored history is replaced, so the next turn starts from the summary and the latest
  turn. `keep.messages` is not used: it counts tool calls and results too, so on a short conversation it would keep
  almost everything. The event has `trigger: "manual"`; it is returned, not surfaced as `AgentCompacted` (no turn is
  running).
- It never resets the conversation: when the summary cannot be written it throws `InvalidOperationException` (the
  history is unchanged), and a summary that is not smaller than what it replaces is not applied (null).
- The optional text is added to the summary request ("The user asked this summary to pay particular attention to: …").
- It runs even when `contextCompression.enabled` is false — that switch is about automatic compaction.
- It throws `InvalidOperationException` while a turn runs (call it between turns — queue it in a UI), and a turn
  started while it runs throws the same exception. Cancelling it leaves the history unchanged and records nothing.
- With an in-process model the next model call processes the whole (shorter) prompt again, as after any compaction.
- The CLI REPL has `/compact [focus]`.

### Context-usage display

When compression is enabled every tool's string result gets a suffix such as

```
[context ≈ 41,230 / 217,144 tokens (18%)]
[context ≈ 47,900 / 217,144 tokens (22%) — consider calling compress_context to free space]
[context ≈ 118,000 / 217,144 tokens (54%) — context near limit: call compress_context now]
[context ≈ 153,000 / 217,144 tokens (70%) — automatic compaction will run before your next step; do not call compress_context]
```

The number is the projected size of the next request (last model call plus everything appended since).
Directive bands, for modes other than `backstop`:

- from 20%: "consider calling compress_context";
- from `ceil(trigger.fraction x 0.75 x 100)`% (53% with the default 0.7), or from 20% when the largest recent
  tool step would push the next request past the trigger: "call compress_context now";
- at or above the trigger: "automatic compaction will run before your next step".

In `backstop` mode only the size is shown. The suffix becomes part of the history (append-only), and the
repeat breakers strip it before comparing results.

### Cost with the in-process model

A compaction rewrites history, so the next agent call re-evaluates the whole prompt, and the summary request
itself is a separate full evaluation. Compaction is designed to be rare; a trigger that is too low makes
every run slow. See [KV-CACHE.md](KV-CACHE.md).

---

## Loop Guards

The `guard` section configures `GuardingChatClient`, which inspects every model call.

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Turns the whole pipeline (including the verify gate) off when false. |
| `retryOnCutoff` | `true` | Cutoff guard (below). `false` returns an answer cut off at the output limit as it is. |
| `maxToolArgsKb` | `64` | Largest serialized tool-call argument size, in KB. Must be > 0. |
| `maxToolCalls` | `0` | Total tool-call budget per session; 0 = off. Must be >= 0. |
| `artifact` | `null` | File (relative to the work directory, or absolute) that must exist and be non-empty before a final answer is accepted. Null or empty = off. |

### What each guard detects and does

The repeat breakers and the persistent counter count **per user turn**: a new message starts them over, so a loop
stopped in one question does not stop the next question of the same session (a guard used on its own, outside
`CodingAgentSession`, sees the whole message list). `maxToolCalls` stays a budget for the whole session.

Repeat breakers nudge at 3 and abort at 5 (`RepeatBreakers.NudgeAt` / `AbortAt`). A **nudge** is a user
message appended to a copy of the request for this call only. An **abort** sends one more request with
`[<label>] <reason>. Stop calling tools. Produce the final answer now, based on the results you already have.`;
if the model still calls tools the turn ends with `[<label>] Stopped after <summary>. Partial results only —
see the transcript above.`

| Guard | Detects | Action | `guard` event `kind` |
|---|---|---|---|
| Call repeat | The same tool with the same arguments (sorted JSON) anywhere in the current user turn | nudge at 3, abort at 5 | `repetition-breaker-nudge` / `-abort` |
| Result repeat | N consecutive identical, non-empty tool results | nudge at 3, abort at 5 | `result-repeat-breaker-nudge` / `-abort` |
| Command repeat | N consecutive `bash_command` calls that differ only in path-like arguments **and** return the same result | nudge at 3, abort at 5 | `command-repeat-breaker-nudge` / `-abort` |
| Empty run | N consecutive empty results (blank, `(no output)`, separators only, empty `output`, or `count: 0`) | nudge at 3, abort at 5 | `empty-run-breaker-nudge` / `-abort` |
| Empty result hint | The last result was empty (before the empty-run nudge applies) | one-line hint that an empty result is valid | `empty tool result-nudge` |
| Persistent counter | Same call key 5 times within the current user turn, counted so that compaction cannot reset it | abort | `repetition-breaker-abort` |
| Tool budget | Total tool calls reached `maxToolCalls` | abort | `tool-budget-abort` |
| Argument size | A tool call whose serialized arguments exceed `maxToolArgsKb` | the call never reaches the tool; one re-invoke asking to split the work; a second oversized call ends the turn with an `[arg-size-guard] Stopped ...` text | `arg-size-guard`, `arg-size-guard-stop` |
| Cutoff | A text answer cut off at the output limit (`FinishReason` `Length`): `maxTokens`, or — with a `LlamaCpp` model — a repetition loop ended early (`stopAfterRepeatedLines` / `stopAfterSameShapeToolCallLines` in [CONFIGURATION.md](CONFIGURATION.md)) | one re-invoke without the cut-off text (it never enters the history); a second cutoff ends the turn with a `[cutoff-guard] Stopped ...` text. Off with `retryOnCutoff: false` | `cutoff-guard`, `cutoff-guard-stop` |
| Completion | A final answer while `artifact` is missing or empty | one re-invoke asking to write the file | `completion-guard` |
| Single writer | At turn end, files under the work directory that changed or appeared but were not written by `write_file`, `edit_file` or `apply_patch` (only when the work directory is inside a git work tree) | warning only (logger + event); never fails the turn | `single-writer` |

Abort priority among the breakers: call > result > command > empty run. Every firing is also yielded as
`AgentGuardTriggered` from `RunTurnEventsAsync`.

When guards are enabled, streamed model output is buffered per model call (the guards need the complete
response before deciding), so `RunTurnStreamingAsync` / `RunTurnEventsAsync` deliver each model reply as one
update rather than token by token.

---

## Tool-Loop Iteration Cap

One turn may make at most `CodingAgentFactory.MaxToolLoopIterations` = **500** model round trips. This is a
compile-time constant, not a config key. The SDK wraps its own `FunctionInvokingChatClient` with this cap
inside MSAF because MSAF's internal loop defaults to 40 and, on reaching it, ends the turn silently with the
last tool call unexecuted and no final answer. The cap is a safety net far above measured runs; for a
budget that ends a turn cleanly with a final answer, use `guard.maxToolCalls`. See
[TROUBLESHOOTING.md](TROUBLESHOOTING.md#a-turn-ends-without-a-final-answer-after-many-tool-calls).

---

## Verify

The verify gate runs inside the guard pipeline when the model gives a final answer (no tool calls).

| Key | Default | Meaning |
|---|---|---|
| `verify.enabled` | `true` | Off = every check is skipped. |
| `verify.maxAttempts` | `2` | Checks per turn. A failure re-invokes the model while budget remains; the last failing check abandons. |
| `verify.timeoutMs` | `60000` | Timeout of one verification command. |

Checks, in order:

1. **Empty answer.** No visible text: fail with evidence `empty-final-message`; the model is told
   `Your previous response was empty (no visible text, no tool calls). Continue the task and produce a visible answer.`
2. **Test command**, detected in the verify directory (`UseVerifyDirectory`, default the session work
   directory):
   - a `*.sln` or `*.csproj` at the top level: `dotnet test --nologo -v q`;
   - `package.json` with `scripts.test`: `npm test --silent`;
   - `*.test.js` / `*.test.ts` files (depth up to 5, at most 20, skipping `node_modules` and dot directories):
     `node --test <files>`;
   - none: skipped (no event).

A failing command sends `Verification failed. Fix and re-run:` plus the last 50 lines of output (or
`timeout after <n>ms` / `exit code <n>`) back to the model. Events: `verify-fail` (re-invoked),
`verify-abandoned` (budget exhausted, the answer stands), `verify` with `converged: ...` (command passed). The
budget resets at each new turn.

---

## Audit Chain

`audit-chain.jsonl` is always written next to `rollout.jsonl`, one entry per rollout line:
`seq`, `channel`, `payload_hash`, `prev_hash`, `timestamp`, `hash`.

- `payload_hash` = SHA-256 of the canonical JSON (RFC 8785: sorted keys, no whitespace) of the rollout line.
- `prev_hash` links to the previous entry (genesis = 64 zeros); `hash` covers the entry itself.
- `channel`: `intent` (`session-start`, `user-input`), `reasoning` (`model-item`), `action` (`tool-call`,
  `tool-result`, `approval`, `safety-decision`, `human-intervention`), `meta` (everything else).

On construction the chain is checked against the rollout and fails closed:

- `audit chain is corrupt: <detail>` when a chain line cannot be parsed;
- `audit chain out of sync with rollout: <n> chain entries vs <m> rollout lines — refusing to continue (tampering or torn write)`.

Both are `InvalidDataException`s thrown from `BuildAsync()`. A rollout without a chain (a fork, or an older
session) is backfilled. Check a finished session with:

```csharp
VerifyReport report = AuditVerify.VerifySession(sessionDir);   // Ok, Entries, Channels, Issues
```

Verification also checks every attachment reference against its file in the work directory (a sub-agent's
session against its top-level session's `work/`): a missing file or a SHA-256 mismatch is an issue with check
`attachment`.

---

## Failure and Cancellation

### Failed turns

If the model client or the loop throws, the session records `error` with the exception message, sets
`status.json` to `error` and discards any compacted view of that turn (the next turn re-evaluates from the full
history).

- `RunTurnAsync` rethrows the exception.
- `RunTurnStreamingAsync` and `RunTurnEventsAsync` do not throw for a failed turn; `RunTurnEventsAsync` ends
  with `AgentTurnFailed(message)`.

Tool failures are not turn failures: they come back to the model as `{"ok": false, ...}` results.

### Cancellation

| Method | On a cancelled token |
|---|---|
| `RunTurnEventsAsync` | Stops at the next update, records `turn-interrupted` with reason `cancelled`, sets status `interrupted`, and yields `AgentTurnCancelled` as the last event. If the cancellation surfaced as an exception inside the loop, an `error` line is written first; the final status is still `interrupted`. |
| `RunTurnAsync` | Throws `OperationCanceledException`; the turn is recorded as `error`. |
| `RunTurnStreamingAsync` | A consumer that stops early leaves status `running` and records nothing more; call `RecordInterrupted(reason)` yourself. |

In every case the turn's model and tool messages are not written to the rollout (only `user-input` and the live
events). A process that exits during a turn should call `session.RecordInterrupted("...")` first, as the
reference CLI does on Ctrl+C; after a hard kill the status stays `running` and a resume picks up the last
completed turn.

With the in-process model, native decoding does not observe cancellation: a prompt evaluation already running
finishes before the turn stops, so cancelling during a long prefill can take a while. A cancelled generation
invalidates the KV state, and the next call is a full evaluation.

### Approvals and cancellation

The approval handler receives no cancellation token. While it waits for the user the turn waits too, and
cancelling the token does not dismiss the prompt. A host that cancels turns should make its handler return
`false` when the turn is cancelled (see [TROUBLESHOOTING.md](TROUBLESHOOTING.md#approval-prompts-block-the-turn)).

---

## See Also

- [CONFIGURATION.md](CONFIGURATION.md) — `contextCompression`, `guard`, `verify`
- [SAFETY.md](SAFETY.md) — approvals during a turn
- [ATTACHMENTS.md](ATTACHMENTS.md) — images and PDFs in records and resume
- [KV-CACHE.md](KV-CACHE.md) — how compaction and history edits affect the KV cache
- [API.md](API.md#kudosagentsessions) — the `Kudos.Agent.Sessions` types
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md#sessions) — session problems
