# Examples

## Contents

1. [The Sample Host (`samples/MinimalHost`)](#the-sample-host-samplesminimalhost)
2. [The Multi-Agent Sample (`samples/MultiAgentHost`)](#the-multi-agent-sample-samplesmultiagenthost)
3. [Custom Tools](#custom-tools)
4. [Approval Handler for a UI](#approval-handler-for-a-ui)
5. [Event Stream with Cancellation](#event-stream-with-cancellation)

---

## The Sample Host (`samples/MinimalHost`)

[`samples/MinimalHost`](../samples/MinimalHost/) is a complete console host that uses only the SDK's public API. It plays the core use case end to end: *instruction → the agent writes and runs a script → it answers with a Markdown table of the results*.

| File | Role |
|---|---|
| [`Program.cs`](../samples/MinimalHost/Program.cs) | Parses the command line, builds the config and the model client, defines the console approval handler |
| [`MinimalHostApp.cs`](../samples/MinimalHost/MinimalHostApp.cs) | The whole host in one method: build a session, run one turn, print the events |
| [`DemoChatClient.cs`](../samples/MinimalHost/DemoChatClient.cs) | A scripted `IChatClient` that stands in for a model (no GPU, no network) |

The project references the three SDK projects directly (not the packages) plus `LLamaSharp.Backend.Cuda13` as its native backend. `--demo` and remote models never load the native library; to run a local `LlamaCpp` model on a machine without an NVIDIA GPU, swap the backend for `LLamaSharp.Backend.Cpu`.

### Running it

```bash
# Scripted model: the whole loop without a model, GPU or network (needs python3 on PATH)
dotnet run --project samples/MinimalHost -- --demo

# A real model: models[0] of the config (in-process LlamaCpp or a remote model)
dotnet run --project samples/MinimalHost -- --config json/kudos-agent.json "Count the lines of each file in the current directory and report them as a Markdown table."

# Add --yes to approve every operation without asking
dotnet run --project samples/MinimalHost -- --demo --yes
```

| Option | Effect |
|---|---|
| `--demo` | Uses `DemoChatClient` and an in-code config; seeds the work directory with two small files |
| `--config <path>` | Loads `kudos-agent.json` from `<path>` and creates a client for `models[0]` |
| `--yes` | The approval handler answers "yes" to everything (prints `(auto)` instead of prompting) |
| `--license <file>` | Uses the License Key in `<file>` (`KudosLicensing.UseLicense`); without it, the key stored with `Host.Cli license set` |
| `"<instruction>"` | The last non-option argument; defaults to the line-count instruction above |

Every session needs a License Key, `--demo` included ([LICENSING.md](LICENSING.md)). Without `--demo` or `--config` it prints the usage line and exits with code `2`. It exits with `0` when the turn produced an answer, `1` otherwise, and `3` when there is no valid License Key (the reason is printed). Session files go to `./results/<session-id>/` in the current directory.

Output of `--demo --yes`:

```
session 20261123-113553-1834 — workdir /…/results/20261123-113553-1834/work
[tool] execute_code
approve exec: execute_code(python)? (auto)
import os
for name in sorted(os.listdir('.')):
    if os.path.isfile(name):
        with open(name, encoding='utf-8') as f:
            print(f'{name}\t{sum(1 for _ in f)}')

[tool] done
| file | lines |
|---|---|
| one.txt | 3 |
| two.txt | 2 |
```

Note the order: the tool call is reported (`[tool] execute_code`) **before** the approval is requested, and the full generated script is shown to the approver before it runs.

### `Program.cs` — config, model client, approval

**Config.** With `--demo` the config is built in code — one placeholder model entry and skills turned off:

```csharp
var config = configPath is null
    ? new KudosAgentConfig { Models = [new ModelConfig { Id = "demo" }], Skills = new SkillsConfig { Enabled = false } }
    : KudosAgentConfig.Load(configPath);
```

The placeholder model is never used to create a client; it only satisfies "at least one model". With `--config`, `KudosAgentConfig.Load` parses and validates the file. The sample does not call `WithEnvOverrides`, so environment overrides such as `MODEL_ID` do not apply to it (your host can add the call — see [QUICK-START.md](QUICK-START.md#minimal-example)).

**Model client.** The core SDK has no model client. The sample registers both provider packages and lets `ChatClientFactory` create the client for `models[0]`:

```csharp
IChatClient client;
if (demo)
{
    client = new DemoChatClient();
}
else
{
    LlamaSharpChatClientProvider.Register();
    RemoteChatClientProvider.Register();
    client = await ChatClientFactory.CreateAsync(config.PrimaryModel);
}
```

**Approval handler.** `Approve` is a `Func<SafetyOperation, Task<bool>>`. It prints the operation kind and its target (`Command` for `exec`, `Path` for `write`), prints `Detail` (for `execute_code` that is the full script), then reads `y` from the console — or returns `true` right away with `--yes`:

```csharp
Task<bool> Approve(SafetyOperation operation)
{
    Console.WriteLine($"approve {operation.Kind}: {operation.Command ?? operation.Path}?{(autoApprove ? " (auto)" : " [y/N]")}");
    if (!string.IsNullOrEmpty(operation.Detail))
    {
        Console.WriteLine(operation.Detail);
    }

    return Task.FromResult(autoApprove || string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase));
}
```

The handler is only called for operations the safety gate marks as "ask"; reads and in-workspace writes are decided by the gate without asking. See [SAFETY.md](SAFETY.md).

### `MinimalHostApp.cs` — build a session, run a turn

```csharp
await using var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(chatClient)
    .UseResultsDirectory(resultsRoot)
    .UseApprovalHandler(approve)
    .BuildAsync(cancellationToken);

output.WriteLine($"session {session.SessionId} — workdir {session.Layout.WorkDir}");
prepareWorkDirectory?.Invoke(session.Layout.WorkDir);
```

- `UseConfig` and `UseChatClient` are the only required calls.
- `UseResultsDirectory(resultsRoot)` places the session at `<resultsRoot>/<new session id>/`; the agent works in its `work/` subdirectory (`session.Layout.WorkDir`). In demo mode, `prepareWorkDirectory` writes `one.txt` (3 lines) and `two.txt` (2 lines) there.
- `await using` disposes the session at the end (it stops any MCP server processes). The chat client is owned by the caller and is not disposed by the session.

The turn is consumed as a typed event stream. The sample prints tool calls, compaction and guard notices, and the final answer:

```csharp
await foreach (var evt in session.RunTurnEventsAsync(instruction, cancellationToken))
{
    switch (evt)
    {
        case AgentToolCallStarted call: output.WriteLine($"[tool] {call.Name}"); break;
        case AgentToolCallCompleted: output.WriteLine("[tool] done"); break;
        case AgentCompacted: output.WriteLine("[context compacted]"); break;
        case AgentGuardTriggered guard: output.WriteLine($"[guard] {guard.Guard.Kind}"); break;
        case AgentTurnCompleted completed: answer = completed.FinalText; output.WriteLine(answer); break;
        case AgentTurnFailed failed: output.WriteLine($"[error] {failed.Message}"); break;
        case AgentTurnCancelled: output.WriteLine("[cancelled]"); break;
    }
}
```

The last event of a turn is always one of `AgentTurnCompleted`, `AgentTurnFailed` or `AgentTurnCancelled`.

### `DemoChatClient.cs` — a scripted model

`DemoChatClient` implements `Microsoft.Extensions.AI.IChatClient` and decides its reply from the conversation it receives:

1. **First call** (no tool result yet) — returns a `FunctionCallContent` for `execute_code` with `language = "python"` and a script that prints `name<TAB>line count` for every file in the current directory.
2. **Second call** (a `FunctionResultContent` is present) — extracts the `"output"` string from the tool result JSON (`{"ok":…,"exitCode":…,"output":"…"}`) and turns each line into a Markdown table row.

`GetStreamingResponseAsync` replays the same response as streaming updates (`ToChatResponseUpdates()`), which is what `RunTurnEventsAsync` consumes. The same technique — a scripted `IChatClient` — is a convenient way to unit-test your own host and tools without a model.

---

## The Multi-Agent Sample (`samples/MultiAgentHost`)

[`samples/MultiAgentHost`](../samples/MultiAgentHost/) shows both multi-agent patterns of [MULTI-AGENT.md](MULTI-AGENT.md) with the same two seeded files (`alpha.txt`, `beta.txt`). It uses only the public API and references the four SDK projects plus `LLamaSharp.Backend.Cuda13`.

| File | Role |
|---|---|
| [`Program.cs`](../samples/MultiAgentHost/Program.cs) | Command line, config, model client, and a console approval handler that prompts one request at a time (sub-agents and participants may ask at the same time) and shows `AgentPath` |
| [`MultiAgentHostApp.cs`](../samples/MultiAgentHost/MultiAgentHostApp.cs) | `RunSubAgentAsync` (a session with the sub-agent `scripter`) and `RunFanOutAsync` (three participants — `lines`, `words`, `chars` — joined into one table by `JoinTable`) |
| [`DemoChatClient.cs`](../samples/MultiAgentHost/DemoChatClient.cs) | A scripted model for both flows: a session with `agent_scripter` delegates; any other session runs a Python script for its metric |

```bash
# Scripted model (needs python3 on PATH)
dotnet run --project samples/MultiAgentHost -- subagent --demo --yes
dotnet run --project samples/MultiAgentHost -- fanout --demo --yes

# A real model: models[0] of the config
dotnet run --project samples/MultiAgentHost -- fanout --config json/kudos-agent.json --yes
```

Both flows need a License Key (`--license <file>` or `Host.Cli license set`; exit code `3` without one). `fanout` opens several sessions at the same time, so a key with `maxConcurrent` 1 (development / Evaluation) is not enough for it ([LICENSING.md](LICENSING.md#concurrent-sessions)).

Output of `subagent --demo --yes`:

```
session 20261124-070804-fa75 — workdir /…/results/20261124-070804-fa75/work
approve exec (agent scripter): execute_code(python)? (auto)
[sub-agent] scripter started (session 20261124-070805-09de)
[sub-agent] scripter ok
| file | lines |
|---|---|
| alpha.txt | 3 |
| beta.txt | 2 |
```

The approval names the sub-agent that asks (`agent scripter`). The `[sub-agent]` events are handed out between model updates, so they can appear after the child's approval prompt.

Output of `fanout --demo --yes`:

```
coordinator 20261124-070806-8d38
approve exec: execute_code(python)? (auto)
approve exec: execute_code(python)? (auto)
approve exec: execute_code(python)? (auto)
[lines] ok in 263 ms
[words] ok in 268 ms
[chars] ok in 261 ms
| file | lines | words | chars |
|---|---|---|---|
| alpha.txt | 3 | 6 | 28 |
| beta.txt | 2 | 3 | 17 |
```

Each participant is its own session with its own work directory (the sample seeds every one), its role comes from `WorkflowParticipant.Instructions`, and the coordinator session holds the `workflow-start` / `workflow-step` / `workflow-end` records. `subagent` and `fanout` exit with `0` on success, `1` when there is no answer (or the workflow failed), and `2` on a usage error.

---

## Custom Tools

A tool is an `ITool`: a unique `Name` and an `AIFunction` (name, description and parameter schema shown to the model). `ToolFunction.Create` builds the `AIFunction` from a delegate. Return results with `ToolResult.Ok` / `ToolResult.Fail` — errors are returned to the model as results, not thrown, so it can correct itself.

A tool that changes something should go through the session's safety gate and approval handler. This one follows the same pattern as the built-in `execute_code`: consult the gate, never auto-run, ask the user:

```csharp
using Kudos.Agent.Safety;
using Kudos.Agent.Tools;
using Microsoft.Extensions.AI;

public sealed class SetParameterTool : ITool
{
    private readonly ToolContext _context;

    public SetParameterTool(ToolContext context)
    {
        _context = context;
        Function = ToolFunction.Create(
            ExecuteAsync,
            name: "set_parameter",
            description: "Sets a named parameter of the open document to a new value. Asks the user first.");
    }

    public string Name => "set_parameter";

    public AIFunction Function { get; }

    private async Task<string> ExecuteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        var operation = new SafetyOperation("exec", Command: "set_parameter", Detail: $"{name} = {value}");
        var decision = await _context.SafetyGate.CheckAsync(operation, cancellationToken);
        if (decision.Effect == SafetyEffect.Auto)
        {
            decision = new SafetyDecision(SafetyEffect.Ask, "set_parameter always asks");
        }

        // Records the decision in the rollout and calls the approval handler for "ask"
        var denied = await SafetyCheck.CheckAsync(_context, operation, Task.FromResult(decision), cancellationToken);
        if (denied is not null)
        {
            return ToolResult.Fail($"set_parameter {denied}");
        }

        // ... apply the change in your application ...
        return ToolResult.Ok(output: $"{name} set to {value}");
    }
}

// A read-only tool needs no context
public sealed class CountRowsTool : ITool
{
    public string Name => "count_rows";

    public AIFunction Function { get; } = ToolFunction.Create(
        (string table) => ToolResult.Ok(output: $"{table}: 42 rows", count: 42),
        "count_rows",
        "Returns the number of rows in a table of the open document.");
}
```

Register them on the builder. `AddTool` takes a factory that receives the session's `ToolContext`; `AddTools` takes ready-made instances. To **replace** a built-in tool, remove it first — adding a tool whose name is still taken makes `BuildAsync` throw `InvalidOperationException`:

```csharp
await using var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(client)
    .RemoveTool("bash_command")                    // leave a built-in tool out
    .AddTool(ctx => new SetParameterTool(ctx))     // built from the session's ToolContext
    .AddTools([new CountRowsTool()])               // ready-made instances
    .ConfigureToolContext(ctx => ctx.DefaultTimeoutMs = 30_000)
    .BuildAsync();
```

See [TOOLS.md](TOOLS.md) for the built-in tools and their limits.

---

## Approval Handler for a UI

The approval handler is awaited inside the tool call, so the turn simply waits while your dialog is open. Route the decision to your own UI by operation kind:

```csharp
using Kudos.Agent.Safety;

public interface IConfirmDialog
{
    Task<bool> ConfirmAsync(string title, string? detail);
}

static Func<SafetyOperation, Task<bool>> CreateApprovalHandler(IConfirmDialog dialog) => op => op.Kind switch
{
    "exec" => dialog.ConfirmAsync($"Run {op.Command}?", op.Detail),   // Detail = the full script for execute_code
    "write" => dialog.ConfirmAsync($"Write {op.Path}?", null),        // e.g. a write outside the writable roots
    _ => Task.FromResult(false),
};

// ...
builder.UseApprovalHandler(CreateApprovalHandler(myDialog));
```

`Detail` is for display only and is never evaluated by the policy. A denied operation comes back to the model as a failed tool result (`rejected by user (approval denied): …`); without a handler the result is `approval required but no approval handler available (fail-closed): …`.

---

## Event Stream with Cancellation

`RunTurnEventsAsync` takes a `CancellationToken`. A cancelled token stops the turn at the next update and ends the stream with `AgentTurnCancelled` (the session records the interruption and its status becomes `interrupted`). It does not throw.

```csharp
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await foreach (var evt in session.RunTurnEventsAsync("Summarize the CSV files in the work directory.", cts.Token))
{
    switch (evt)
    {
        case AgentTextDelta t: Console.Write(t.Text); break;
        case AgentReasoningDelta r: Console.Write(r.Text); break;          // models that expose reasoning
        case AgentToolCallStarted s:
            Console.WriteLine($"\n> {s.Name}({string.Join(", ", s.Arguments?.Keys ?? [])})");
            break;
        case AgentToolCallCompleted c: Console.WriteLine($"< {c.CallId}"); break;
        case AgentCompacted k:
            Console.WriteLine($"[compacted: {k.Compaction.TokensBefore} -> {k.Compaction.TokensAfter} tokens]");
            break;
        case AgentGuardTriggered g: Console.WriteLine($"[guard {g.Guard.Kind}] {g.Guard.Detail}"); break;
        case AgentTurnCompleted done: Console.WriteLine($"\n--- done ({done.FinalText?.Length ?? 0} chars)"); break;
        case AgentTurnFailed f: Console.Error.WriteLine($"\n--- failed: {f.Message}"); break;
        case AgentTurnCancelled: Console.WriteLine("\n--- cancelled"); break;
    }
}
```

Other ways to run a turn:

```csharp
// Final response only (Microsoft.Agents.AI AgentResponse). A cancelled token throws OperationCanceledException.
var response = await session.RunTurnAsync("Create data.csv with 10 random rows.", cts.Token);
Console.WriteLine(response.Text);

// The session keeps the conversation — the next turn continues it.
var next = await session.RunTurnAsync("Now add a total row.");

// While a turn runs (e.g. from a UI thread): queue extra user input for the next model call.
session.Steering.Enqueue("Use semicolons as separators.");
```

To continue an earlier session in a new process, build with the same results directory and session id and resume from its directory (or its `rollout.jsonl`):

```csharp
await using var resumed = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(client)
    .UseResultsDirectory("results", sessionId)
    .ResumeFrom(Path.Combine("results", sessionId))
    .BuildAsync();
```

See [SESSIONS.md](SESSIONS.md) for the session files and resume semantics.

---

## See Also

- [QUICK-START.md](QUICK-START.md) — installation and a minimal host
- [API.md](API.md) — the types used in these snippets
- [TOOLS.md](TOOLS.md) — adding, replacing and removing tools
- [SAFETY.md](SAFETY.md) — the approval handler contract
- [MULTI-AGENT.md](MULTI-AGENT.md) — sub-agents and workflows
- [SESSIONS.md](SESSIONS.md) — session files, resume, cancellation
