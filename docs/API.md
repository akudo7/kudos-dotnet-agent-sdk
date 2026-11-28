# API Reference

This page covers the consumer path: build a session, run turns, handle events and approvals, create model clients, and plug in tools. Every type listed here is public in `Kudos.Agent.Sdk` 0.7.0 unless another package is named. The packages expose more public types (prompt rendering, rollout replay, guards, compaction internals, individual built-in tools); they are lower-level building blocks and are not needed to embed the agent.

| Namespace | Package | Main types |
|---|---|---|
| [`Kudos.Agent.Agents`](#kudosagentagents) | `Kudos.Agent.Sdk` | `AgentSessionBuilder`, `CodingAgentSession`, `AgentEvent` and its subtypes |
| [`Kudos.Agent.Config`](#kudosagentconfig) | `Kudos.Agent.Sdk` | `KudosAgentConfig`, `ModelConfig`, `ModelSettings`, `ModelType` |
| [`Kudos.Agent.Llm`](#kudosagentllm) | all three | `ChatClientFactory`, `IChatClientProvider`, `LlamaSharpChatClientProvider`, `RemoteChatClientProvider` |
| [`Kudos.Agent.Safety`](#kudosagentsafety) | `Kudos.Agent.Sdk` | `SafetyOperation`, `SafetyDecision`, `SafetyEffect`, `ISafetyGate` |
| [`Kudos.Agent.Tools`](#kudosagenttools) | `Kudos.Agent.Sdk` | `ITool`, `ToolFunction`, `ToolResult`, `ToolContext`, `SafetyCheck` |
| [`Kudos.Agent.Sessions`](#kudosagentsessions) | `Kudos.Agent.Sdk` | `SessionLayout` |
| [`Kudos.Agent.Attachments`](#kudosagentattachments) | `Kudos.Agent.Sdk` | `AttachmentStore`, `Attachment`, `AttachmentRef`, `AttachmentException`, `InputModalities`, `MediaSniffer`, `AttachmentMessages` |
| [`Kudos.Agent.Rollout`](#kudosagentrollout) | `Kudos.Agent.Sdk` | `CompactionEvent`, `GuardEvent` (event payloads) |
| [`Kudos.Agent.Secrets`](#kudosagentsecrets) | `Kudos.Agent.Sdk` | `SecretsLoader`, `SecretStores`, `ISecretStore`, `SecretStoreException`, `Redactor` |
| [`Kudos.Agent.Licensing`](#kudosagentlicensing) | `Kudos.Agent.Sdk` | `KudosLicensing`, `KudosLicense`, `LicenseException`, `LicenseFailure`, `LicenseLevel`, `LicenseFeature` |
| [`Kudos.Agent.Workflows`](#kudosagentworkflows) | `Kudos.Agent.Sdk.Workflows` | `AgentWorkflows`, `AgentWorkflow`, `WorkflowParticipant`, `AgentWorkflowResult` |

## Contents

1. [Kudos.Agent.Agents](#kudosagentagents)
2. [Kudos.Agent.Config](#kudosagentconfig)
3. [Kudos.Agent.Llm](#kudosagentllm)
4. [Kudos.Agent.Safety](#kudosagentsafety)
5. [Kudos.Agent.Tools](#kudosagenttools)
6. [Kudos.Agent.Sessions](#kudosagentsessions)
7. [Kudos.Agent.Attachments](#kudosagentattachments)
8. [Kudos.Agent.Rollout](#kudosagentrollout)
9. [Kudos.Agent.Secrets](#kudosagentsecrets)
10. [Kudos.Agent.Licensing](#kudosagentlicensing)
11. [Kudos.Agent.Workflows](#kudosagentworkflows)

---

## Kudos.Agent.Agents

### AgentSessionBuilder

```csharp
public sealed class AgentSessionBuilder
```

Assembles a `CodingAgentSession`: config → session layout → skills index → system prompt → `ToolContext` (approval handler, tools) → session. Every host uses the same steps, so the prompt and tool list are the same for every host. All `Use*` / `Add*` methods return the builder for chaining.

```csharp
await using var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(client)
    .UseResultsDirectory("results")
    .UseApprovalHandler(approve)
    .BuildAsync();
```

#### Required

| Method | Description |
|---|---|
| `UseConfig(KudosAgentConfig config)` | The agent config (models, safety, compression, guards, skills, MCP). `BuildAsync` throws `InvalidOperationException` without it. |
| `UseChatClient(IChatClient chatClient)` | The model client — from `ChatClientFactory.CreateAsync` or any `Microsoft.Extensions.AI.IChatClient`. The session does **not** dispose it. `BuildAsync` throws `InvalidOperationException` without it. |

#### Session location and records

| Method | Description |
|---|---|
| `UseResultsDirectory(string resultsRoot, string? sessionId = null)` | The session lives in `<resultsRoot>/<sessionId>/` (`work/`, `rollout.jsonl`, `status.json`). Default: `./results` under the current directory and a new session id (`SessionLayout.NewSessionId()`). |
| `UseSessionLayout(SessionLayout layout)` | An explicit layout; overrides `UseResultsDirectory`. |
| `ResumeFrom(string? rolloutPathOrSessionDirectory)` | Seeds the conversation from an earlier session's `rollout.jsonl` (or its session directory) before the first turn. Pair it with the same session id to keep appending to that session. See [SESSIONS.md](SESSIONS.md). |
| `UseTask(string task)` | Task text recorded on `session-start` (batch hosts). Default: empty (interactive hosts). |
| `UseConfigPath(string? configPath)` | Config file path recorded on `session-start` (informational only). |

#### Prompt, skills and verification

| Method | Description |
|---|---|
| `UsePromptsDirectory(string? promptsDirectory)` | A directory with your own `base-prompt.md` and `permissions/` fragments. Default (`null`): the prompts embedded in the SDK. |
| `UseSkillsDirectory(string skillsRoot)` | Skills root for the skills index. Default: `config.Skills.SkillsPath` resolved against the current directory. It does not change where the `load_skill` tool reads skill bodies (`ToolContext.SkillsPath`); when the root differs from the config default, set that too: `.ConfigureToolContext(ctx => ctx.SkillsPath = skillsRoot)`. |
| `UseSkillsIndex(string skillsIndex)` | A pre-rendered skills index (see `LoadSkillsIndex`); overrides `UseSkillsDirectory`. |
| `UseVerifyDirectory(string? directory)` | Directory the verify gate detects and runs its command in. Default: the session work directory. |
| `string BuildInstructions(string workDir)` | Returns the system prompt a session with this configuration would use for `workDir`, without creating a session. Requires `UseConfig`. |
| `static string LoadSkillsIndex(KudosAgentConfig config, string? skillsRoot = null, ILogger? logger = null)` | Renders the skills index (frontmatter only — bodies load on demand through the `load_skill` tool). Empty when `config.Skills.Enabled` is `false`. Warnings (`[skills] …`) go to `logger`. A long-lived host renders it once and passes it to `UseSkillsIndex` for every session. See [SKILLS-GUIDE.md](SKILLS-GUIDE.md). |

#### Approvals, logging, tools

| Method | Description |
|---|---|
| `UseApprovalHandler(Func<SafetyOperation, Task<bool>> approvalHandler)` | Called for every operation the safety gate marks as "ask". Return `true` to allow. **Without a handler such operations are denied (fail-closed).** See [SAFETY.md](SAFETY.md). |
| `UseLogger(ILogger logger)` | Receives the session's warnings (MCP, skills, compaction, single-writer). Default: none (`NullLogger`). |
| `AddTool(Func<ToolContext, ITool> factory)` | Adds a host tool built from the session's `ToolContext` (safety gate, rollout, work directory). Added after the built-in tools. |
| `AddTools(IEnumerable<ITool> tools)` | Adds ready-made host tools. |
| `RemoveTool(string name)` | Leaves a built-in tool out by name. To replace a built-in tool, remove it and add yours under the same name; a name that is still taken makes `BuildAsync` throw `InvalidOperationException`. |
| `ConfigureToolContext(Action<ToolContext> configure)` | Further adjustments to the `ToolContext` (limits, shell, todo store, …) just before the session is created. Can be called more than once; actions run in order. |
| `AddSubAgent(string name, string description, Func<AgentSessionBuilder, AgentSessionBuilder>? configure = null, SubAgentOptions? options = null)` | Adds a sub-agent the model calls through the tool `agent_<name>` (argument `query`). See [Sub-agents](#sub-agents). `name`: lower-case letters, digits, `_`, `-` (max 40, first character a letter or digit); an invalid name throws `ArgumentException`, a duplicate `InvalidOperationException`. |
| `UseSubAgentPolicy(SubAgentPolicy policy)` | Nesting depth and result-size limits for this session's sub-agents; inherited by its children. |
| `UseLicense(string licenseText)` | The License Key for this builder's sessions — tried after the OS secret store and before `KudosLicensing.UseLicense`; the first key that verifies is used. See [LICENSING.md](LICENSING.md). |
| `UseMcpConnection(McpClientManager connection)` | Uses an MCP connection the host made once with `McpClientManager.ConnectSharedAsync(servers, workingDirectory, onWarning)` instead of starting the configured servers for this session. The session gets the same `mcp_*` tools (built with `connection.ForContext(toolContext)` — approvals and records go to this session); disposing the session leaves the servers running, the host disposes the connection. For long-lived hosts that create many sessions, such as the A2A server ([A2A.md](A2A.md)). |

#### Build

```csharp
public Task<CodingAgentSession> BuildAsync(CancellationToken cancellationToken = default)
```

Checks the License Key first — a valid key whose level includes the session's model and MCP servers, and a free seat under its `maxConcurrent` — and throws `LicenseException` before anything is created when it fails ([LICENSING.md](LICENSING.md)). Then creates the session directory, connects the configured MCP servers (a failing server is skipped with a warning), builds the tool list, and records `session-start`.

### CodingAgentSession

```csharp
public sealed class CodingAgentSession : IAsyncDisposable
```

One agent conversation. Each turn is recorded to the session's `rollout.jsonl` (plus an audit chain), and `status.json` moves through `running` → `complete` / `error` / `interrupted`. Dispose it with `await using` — that stops MCP server processes. The chat client stays owned by the caller.

#### Running turns

All three methods run one turn (user input → agent loop with tool calls → final answer) on the same conversation; call them again for the next turn.

| Method | Returns | Notes |
|---|---|---|
| `RunTurnEventsAsync(string userInput, CancellationToken cancellationToken = default)` | `IAsyncEnumerable<AgentEvent>` | Typed events for a UI ([below](#agentevent)). The consumer drives the turn: the next step (for example a tool call's approval) starts only after the previous event was taken, so a UI can show a tool call before its approval dialog. A cancelled token stops the turn at the next update and ends the stream with `AgentTurnCancelled` (recorded as `turn-interrupted`, status `interrupted`). A failed turn ends with `AgentTurnFailed`; neither throws. |
| `RunTurnAsync(string userInput, CancellationToken cancellationToken = default)` | `Task<AgentResponse>` | The final `Microsoft.Agents.AI.AgentResponse` (use `.Text` for the answer). Failures and cancellation are recorded and rethrown (`OperationCanceledException` for a cancelled token). |
| `RunTurnStreamingAsync(string userInput, CancellationToken cancellationToken = default)` | `IAsyncEnumerable<AgentResponseUpdate>` | The raw Microsoft Agent Framework streaming updates. Enumerate to the end for the turn to be recorded as complete. A cancelled token ends the stream and is recorded as `turn-interrupted` (status `interrupted`), not as an error. |

Each method has an overload with attachments as the second parameter — `RunTurnAsync(string userInput, IReadOnlyList<Attachment> attachments, CancellationToken = default)`, `RunTurnStreamingAsync(string, IReadOnlyList<Attachment>?, CancellationToken = default)`, `RunTurnEventsAsync(string, IReadOnlyList<Attachment>?, CancellationToken = default)`. Import the files with `session.Attachments` first. The attachments are checked before anything is recorded or sent: more than `attachments.maxPerMessage`, or an image for a model without image input, throws `AttachmentException` (from `RunTurnAsync`, or on the first enumeration step of the streaming methods). A PDF the model cannot read is not sent; the message gets a sentence with its path instead. See [ATTACHMENTS.md](ATTACHMENTS.md).

```csharp
var image = session.Attachments.ImportFile("/data/drawing.png");   // size + type checks, saved under work/attachments/
var response = await session.RunTurnAsync("Which parts are labelled in this drawing?", [image]);
```

#### Other members

| Member | Description |
|---|---|
| `string SessionId { get; }` | The session id (`Layout.SessionId`). |
| `SessionLayout Layout { get; }` | Session paths: `WorkDir`, `SessionDir`, `RolloutPath`, `StatusPath`. |
| `string Task { get; }` | The task text recorded on `session-start`. |
| `AttachmentStore Attachments { get; }` | The session's attachment store (`work/attachments/`, limits from `attachments`). Import files here for the attachment overloads. |
| `IReadOnlySet<string> InputModalities { get; }` | What the session's model takes: `text` plus `image` / `pdf` (`models[].config.inputModalities` or the provider default). |
| `Task<CompactionEvent?> CompactAsync(string? instructions = null, CancellationToken cancellationToken = default)` | Compacts the conversation now, between turns (a `/compact` command): everything before the latest turn becomes a summary, recorded as a `compaction` event with `trigger: "manual"` and returned (`TokensBefore` / `TokensAfter`); null when there is nothing to compact (one turn only, or the summary would not be smaller). `instructions`: what the summary should keep. Runs even when `contextCompression.enabled` is false. Never resets the conversation: `InvalidOperationException` during a turn or when the summary cannot be written; cancelling leaves the history unchanged. See [SESSIONS.md](SESSIONS.md#manual-compaction). |
| `SteeringChatClient Steering { get; }` | Mid-turn input: `Steering.Enqueue(string text)` queues a user message that is added to the next model call (one-shot). `Steering.PendingCount` is the number not yet sent. |
| `void RecordInterrupted(string reason)` | Records a turn interruption (for example on process signal) and sets the status to `interrupted`. |
| `object? ExclusiveModel { get; }` | What this session shares with the sessions it must take turns with on an in-process `LlamaCpp` model; `null` otherwise. With `seqMax` 1 it is the model client; with `seqMax` > 1 it is the model and the session's slot. Sessions whose values are equal (`Equals`) take turns — see **Model sharing** under [Sub-agents](#sub-agents). |
| `RolloutRecorder Recorder { get; }` | The session's rollout recorder. Its `Recorded` event (`Action<RolloutEvent>`) fires for every recorded event. See [SESSIONS.md](SESSIONS.md). |
| `ChatClientAgent Agent`, `AgentSession Session`, `InMemoryChatHistoryProvider History` | The underlying Microsoft Agent Framework objects (for example `History.GetMessages(Session)` for the conversation so far). |
| `ValueTask DisposeAsync()` | Releases MCP server processes and the sessions of its sub-agents. |
| `static Task<CodingAgentSession> CreateAsync(IChatClient chatClient, string instructions, ToolContext toolContext, SessionLayout layout, string task, string? seedFrom = null, string? configPath = null, CancellationToken cancellationToken = default, ILogger? logger = null)` | Low-level factory used by `AgentSessionBuilder` — you supply the system prompt and tool context yourself. Prefer the builder. |

### AgentEvent

```csharp
public abstract record AgentEvent;
```

Produced by `CodingAgentSession.RunTurnEventsAsync`. All subtypes are sealed records in `Kudos.Agent.Agents`. The **last event of a turn is always** `AgentTurnCompleted`, `AgentTurnFailed` or `AgentTurnCancelled`.

| Event | Properties | Meaning |
|---|---|---|
| `AgentTextDelta` | `string Text` | A piece of the assistant's visible answer. |
| `AgentReasoningDelta` | `string Text` | A piece of the model's reasoning (models that expose it). |
| `AgentToolCallStarted` | `string CallId`, `string Name`, `IDictionary<string, object?>? Arguments` | The model requested a tool call — before approval and execution. |
| `AgentToolCallCompleted` | `string CallId`, `object? Result` | The tool call finished; `Result` is what the model sees (usually the `ToolResult` JSON, possibly followed by a context-usage note; for a result with images, an `IList<AIContent>` of a `TextContent` followed by `DataContent` items). |
| `AgentCompacted` | `CompactionEvent Compaction` | The context was compacted (automatic backstop or the model's `compress_context`). |
| `AgentGuardTriggered` | `GuardEvent Guard` | A turn-level guard fired (repeat breaker, argument size, single-writer, …). |
| `AgentSubAgentStarted` | `string Agent`, `string ChildSessionId` | A sub-agent call started. `Agent` is the sub-agent path (`reviewer`, nested `reviewer/checker`). The child's own text is not part of this stream. |
| `AgentSubAgentCompleted` | `string Agent`, `string ChildSessionId`, `string Status` | A sub-agent call ended; `Status` is a `SubAgentStatus` value. |
| `AgentTurnCompleted` | `string? FinalText` | The turn finished; `FinalText` is the last assistant message. |
| `AgentTurnFailed` | `string Message` | The turn failed (recorded as an error, status `error`). |
| `AgentTurnCancelled` | — | The host cancelled the turn (recorded as `turn-interrupted`, status `interrupted`). |

### Sub-agents

A sub-agent is a child `CodingAgentSession` that the parent's model calls as a tool (guide: [MULTI-AGENT.md](MULTI-AGENT.md)):

```csharp
await using var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(chatClient)
    .UseApprovalHandler(approve)
    .AddSubAgent(
        "reviewer",
        "Reviews a change and lists concrete problems. Give it the file paths and what to check.",
        child => child.UsePromptsDirectory("prompts/reviewer").RemoveTool("execute_code"),
        new SubAgentOptions { Timeout = TimeSpan.FromMinutes(5) })
    .BuildAsync();
```

- **Tool**: `agent_<name>`, description = `description`, one argument `query` (string). The tool list is fixed when the session is built.
- **Child setup**: the child is created on the first call. It starts from the parent builder's config, chat client, prompts directory, skills and logger; `configure` can replace any of them (for example `UseChatClient(smallModel)`) and add or remove tools. The parent's own host tools (`AddTool`) are not inherited.
- **Always enforced**: the child works in the parent's work directory; its approval requests go to the parent's approval handler with `SafetyOperation.AgentPath` set (a handler set inside `configure` is ignored; no parent handler = fail-closed); its safety settings cannot be looser than the parent's — it may only narrow `sandboxMode`, other differences are ignored with a warning.
- **Result**: the child's final answer, truncated to `SubAgentPolicy.MaxResultChars` with a `[truncated: N of M characters]` note. A failure, a timeout or an empty answer comes back as a tool result `{"ok":false,"agent":"…","error":"failed|timeout|empty","message":"…","hint":"…"}`, and the parent's turn continues. Cancelling the parent's turn cancels the child (the child records `turn-interrupted`) and the parent's run throws as usual.
- **Records**: the parent's rollout gets `subagent-call` / `subagent-result` ([SESSIONS.md](SESSIONS.md#event-types)); the child has its own rollout, status and audit chain under `<session dir>/agents/<name>/<child session id>/`.
- **Nesting**: a child may add its own sub-agents. A session at depth `SubAgentPolicy.MaxDepth` (default 2) gets no `agent_*` tools.
- **An existing agent**: `AddSubAgent(string name, string description, Microsoft.Agents.AI.AIAgent agent, SubAgentOptions? options = null)` registers any MSAF `AIAgent` — for example a workflow from `Kudos.Agent.Sdk.Workflows` (`AgentWorkflow.AsAIAgent()`) — as `agent_<name>`. The call is recorded like a child call (`subagent-call` / `subagent-result`, with the agent's `Id` as `childSessionId` and no `childAuditHead`), counts against `MaxConcurrent`, and failures, timeouts and empty answers come back as the error result above. The agent is not a child session: it keeps its own model, tools, approval handling and records, and it stays owned by the caller. With `PersistConversation` (default) one `AgentSession` of the agent is kept for the parent's lifetime; otherwise each call runs without a session.
- **Config**: the same sub-agents can be defined in `kudos-agent.json` (`subAgents[]`, `subAgentPolicy` — [CONFIGURATION.md](CONFIGURATION.md#8-subagents)). Config entries apply to the top-level session only; an `AddSubAgent` call with the same name wins.
- **Dynamic sub-agents (`spawn_agent`)**: with `SubAgentPolicy.AllowSpawn` (config `subAgentPolicy.allowSpawn`, default off) the model also gets the tool `spawn_agent` with two arguments, `instructions` (the helper's role and rules) and `query` (the work). Each call starts a new one-shot sub-agent (path `spawn`, records under `agents/spawn/`) whose first message is the instructions followed by the work. It uses the session's own model, prompts, skills and safety, gets no registered sub-agents, and follows `MaxDepth` (it can spawn again while below it). Failures and approvals behave as for `agent_<name>`.
- **Parallel sub-agents**: when the model answers with several `agent_*` / `spawn_agent` calls, up to `SubAgentPolicy.MaxConcurrent` (default 3) run in parallel and the rest wait; every other tool of the session still runs one at a time. Calls to the same `agent_<name>` with a kept conversation run one after another. Your approval handler may therefore be called from several sub-agents at the same time.
- **Slots**: with an `OpenAICompatible` model (llama-server), or a `LlamaCpp` model with `seqMax` > 1 (in-process sequences — the slot is the sequence number; the top-level session is pinned even without sub-agents), the top-level session's requests carry `id_slot = ParentSlot` and each running sub-agent gets one of `EffectiveSubAgentSlots` (default parent 1, sub-agents 2, 3, 4), so each keeps its KV cache in its own server slot and they run in parallel. See [CONFIGURATION.md](CONFIGURATION.md#9-subagentpolicy) (server `--parallel`, `--kv-unified`). `LlamaServerSlot.OptionKey` (`"id_slot"`) is the `ChatOptions.AdditionalProperties` key a pinned request carries; the `Kudos.Agent.Sdk.Providers` OpenAICompatible client (`LlamaServerSlotChatClient`) writes it into the request body.
- **Model sharing**: the parent waits while the child runs, so parent and child can share one chat client. With `seqMax` > 1 each slot of an in-process `LlamaCpp` model is its own conversation: sessions on different slots run in parallel and keep their KV caches, sessions on the same slot take turns. With `seqMax` 1 (the default), every session on an in-process `LlamaCpp` model — parent, sub-agents, or separate top-level sessions such as workflow participants — waits for that model's gate, so it never serves two requests at once (`CodingAgentSession.ExclusiveModel` names the shared instance). With the in-process model the KV cache is rebuilt whenever the conversation switches between them: after every sub-agent call the parent's whole prompt is processed again (its cost grows with the parent's history — for example, 6,000 prompt tokens took about 40 s with a small model on a laptop CPU instead of about 0.5 s for an unchanged conversation), and parallel sub-agents sharing it run one at a time. A smaller or remote model for the child, llama-server slots, or `seqMax` > 1 avoid that.

| Type | Members |
|---|---|
| `SubAgentOptions` (record) | `bool PersistConversation` (default `true`: one child session for the parent's lifetime, so a second call continues the child's conversation; `false`: a new child per call), `TimeSpan? Timeout` (default none) |
| `SubAgentPolicy` (record) | `int MaxDepth` (default 2; 0 = no sub-agent tools at all), `int MaxResultChars` (default 20000), `bool AllowSpawn` (default false — offers `spawn_agent`, see below), `TimeSpan? SpawnTimeout` (default none), `int MaxConcurrent` (default 3 — sub-agents of the top-level session running at the same time), `int MaxFanOut` (default 4 — most participants of one concurrent workflow of `Kudos.Agent.Sdk.Workflows`), `bool PinSlots` (default true), `int? ParentSlot` (default 1), `IReadOnlyList<int>? SubAgentSlots` (default null = the `MaxConcurrent` slots after `ParentSlot`), `IReadOnlyList<int> EffectiveSubAgentSlots`, `static SubAgentPolicy FromConfig(SubAgentPolicyConfig)`. Without `UseSubAgentPolicy` the builder uses the config's `subAgentPolicy` section; an invalid policy makes `BuildAsync` throw `ArgumentException`. |
| `SubAgentStatus` (static) | `Ok = "ok"`, `Empty = "empty"`, `Failed = "failed"`, `Timeout = "timeout"`, `Cancelled = "cancelled"` |

#### CodingAgentSessionAgent

```csharp
public sealed class CodingAgentSessionAgent : Microsoft.Agents.AI.AIAgent
public CodingAgentSessionAgent(CodingAgentSession session, string? name = null, string? description = null)
```

Exposes a session as a Microsoft Agent Framework `AIAgent`, for APIs that expect one (`AsAIFunction()`, workflow participants). Each run is one turn of the wrapped session (`RunTurnAsync` / `RunTurnStreamingAsync`), so it is recorded like any other turn. The conversation lives in the wrapped session: every `AgentSession` of this agent continues the same conversation, and runs are serialized. The run's input is the text of its user messages (all message texts if there is none), joined by blank lines — `static string InputText(IEnumerable<ChatMessage>)`. `Name` defaults to the session id. The wrapped session stays owned by the caller.

### CodingAgentFactory

```csharp
public static class CodingAgentFactory
```

Lower-level: creates the Microsoft Agent Framework `ChatClientAgent` with the SDK's tool list (`Create(IChatClient, string instructions, ToolContext)`, `Create(IChatClient, string instructions, IEnumerable<AITool>? tools = null)`, `CreateWithHistory(...)`). Constants: `AgentName = "agent"`, `MaxToolLoopIterations = 500`. Not needed when you use `AgentSessionBuilder`.

---

## Kudos.Agent.Config

### KudosAgentConfig

```csharp
public sealed record KudosAgentConfig
```

The root of `kudos-agent.json`. Property names are matched case-insensitively; enum values accept kebab-case (`"on-request"`), camelCase and the C# names; comments and trailing commas are allowed. The schema of every section is documented in [CONFIGURATION.md](CONFIGURATION.md).

| Member | Description |
|---|---|
| `static KudosAgentConfig Load(string path)` | Reads the file and calls `FromJson`. |
| `static KudosAgentConfig FromJson(string json)` | Deserializes and validates. Throws `JsonException` for invalid or empty JSON and `InvalidOperationException` for invalid values (for example an empty `models` list, a `LlamaCpp` model without `model`, an `OpenAICompatible` model without `serverUrl`, an unsupported `effort`). |
| `KudosAgentConfig WithEnvOverrides(IReadOnlyDictionary<string, string?> env)` | Returns a copy with environment overrides applied, then validates it. Pass `SecretsLoader.ResolveProcessEnvironment(config)` (the process environment with the config's keys filled from the OS secret store) or `ChatClientFactory.ProcessEnvironment()` (the process environment only). `Load` / `FromJson` do **not** apply overrides on their own. |
| `void Validate()` | Re-runs validation (useful for a config built in code). |
| `ModelConfig PrimaryModel { get; }` | `Models[0]` — the model the host runs. |
| `int EffectiveContextWindow { get; }` | The primary model's `contextWindow`, else `contextCompression.modelContextWindow`. |
| `IReadOnlyList<string> MissingEnvRefs { get; }` | After `WithEnvOverrides`: the `${VAR}` names found in neither the given environment nor (when it was store-filled) the OS secret store, sorted. Each was removed from its value. Not serialized. |
| `static readonly IReadOnlyList<string> SupportedEfforts` | `low`, `medium`, `high`, `xhigh`. |
| `Models`, `Safety`, `ContextCompression`, `Skills`, `Verify`, `Guard`, `McpServers`, `SubAgents`, `SubAgentPolicy`, `A2A`, `Attachments` | The config sections (`List<ModelConfig>`, `SafetyConfig`, `ContextCompressionConfig`, `SkillsConfig`, `VerifyConfig`, `GuardConfig`, `List<McpServerConfig>`, `List<SubAgentConfig>`, `SubAgentPolicyConfig`, `A2AConfig`, `AttachmentsConfig`), all `init`-settable for configs built in code. |
| `KudosAgentConfig ForModel(string modelId)` | A copy for a session that runs `modelId`: that entry becomes `models[0]` and its `contextWindow` (if set) the compression window (the `MODEL_ID` semantics). Used for sub-agents with their own model. |

Environment variables read by `WithEnvOverrides`:

| Variable | Effect |
|---|---|
| `MODEL_ID` | Moves the `models[]` entry with this id to the front (it becomes `PrimaryModel`). Unknown ids throw. |
| `MODEL` / `SERVER_URL` | Override `model` / `serverUrl` of `models[0]` (applied after `MODEL_ID`). |
| `SAFETY_APPROVAL_POLICY` / `SAFETY_SANDBOX_MODE` | Override `safety.approvalPolicy` / `safety.sandboxMode`. |
| `CONTEXT_WINDOW` | Overrides the compression context window (otherwise the selected model's `contextWindow` applies). |
| `MCP_ENABLED=0` (`false` / `off` / `no`) | Starts no MCP server. |

`WithEnvOverrides` also drops `mcpServers` entries with `"enabled": false` and expands `${VAR}` references in `mcpServers[].env` from `env`.

A config can also be built in code:

```csharp
var config = new KudosAgentConfig
{
    Models = [new ModelConfig
    {
        Id = "claude",
        Type = ModelType.Anthropic,
        Config = new ModelSettings { Model = "claude-sonnet-5-5", MaxTokens = 16000 },
    }],
    Safety = new SafetyConfig { SandboxMode = SandboxMode.WorkspaceWrite, ApprovalPolicy = ApprovalPolicy.OnRequest },
    Skills = new SkillsConfig { Enabled = false },
};
```

### ModelConfig, ModelSettings, ModelType

| Type | Members |
|---|---|
| `sealed record ModelConfig` | `required string Id`, `ModelType Type` (default `LlamaCpp`), `ModelSettings Config` |
| `sealed record ModelSettings` | `Model`, `ServerUrl`, `Region`, `ContextSize`, `GpuLayers`, `MainGpu`, `Temperature`, `MaxTokens`, `Effort`, `ContextWindow`, `TimeoutSeconds`, `MaxRetries`, `InputModalities`; `const int DefaultTimeoutSeconds` |
| `enum ModelType` | `LlamaCpp`, `Anthropic`, `OpenAI`, `AzureOpenAI`, `OpenAICompatible`, `Bedrock`, `Gemini` |

What each setting means per model type: [MODELS.md](MODELS.md) and [CONFIGURATION.md](CONFIGURATION.md).

### ApprovalPolicy, SandboxMode

| Enum | Values (JSON) |
|---|---|
| `ApprovalPolicy` | `OnRequest` (`"on-request"`, default), `Never` (`"never"`) |
| `SandboxMode` | `ReadOnly` (`"read-only"`), `WorkspaceWrite` (`"workspace-write"`, default), `DangerFullAccess` (`"danger-full-access"`) |

Both live in `SafetyConfig` (`ApprovalPolicy`, `SandboxMode`, `ExecDefault`, `ExecRules`, `WritableRoots`, `ExecArgScope`). Their combined effect is described in [SAFETY.md](SAFETY.md).

---

## Kudos.Agent.Llm

### ChatClientFactory

```csharp
public static class ChatClientFactory
```

Creates the agent's `IChatClient` from a `models[]` entry through the provider registered for its `ModelType`. The core package registers nothing.

| Member | Description |
|---|---|
| `static Task<IChatClient> CreateAsync(ModelConfig model, HttpClient? httpClient = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string?>? environment = null)` | Creates the client. Throws `NotSupportedException` (message names the package and the `Register()` call) when no provider is registered for `model.Type`. `environment` defaults to `ProcessEnvironment()` with the model type's missing credential keys filled from `SecretStores.Default` ([SECRETS.md](SECRETS.md)); an explicit `environment` is used as given. A `LlamaCpp` model loads the GGUF file here (throws `FileNotFoundException` if missing). |
| `static void Register(IChatClientProvider provider)` | Registers (or replaces) the provider for each of its `ModelTypes`. Safe to call more than once. |
| `static bool IsRegistered(ModelType type)` | `true` when a provider is registered for the type. |
| `static string MissingProviderMessage(ModelType type)` | The message `CreateAsync` throws for an unregistered type. |
| `static bool IsRemote(ModelConfig model)` | `true` when prompts and tool results leave the machine: every type except `LlamaCpp`, and `OpenAICompatible` unless its `serverUrl` is loopback. |
| `static string RemoteNotice(ModelConfig model)` | A user-facing notice for a remote model (mentions when the server is on the local network). |
| `static string? CredentialWarning(ModelConfig model, IReadOnlyDictionary<string, string?> env)` | Warning text when the model's credential is not visible in `env`, else `null`. |
| `static bool IsPrivateNetworkHost(string host)` | RFC 1918 / link-local / IPv6 ULA addresses and `*.local` host names. |
| `static IReadOnlyDictionary<string, string?> ProcessEnvironment()` | The process environment as a case-insensitive dictionary (no secret-store lookup). |

### Provider registration

| Package | Call | Model types |
|---|---|---|
| `Kudos.Agent.Sdk.LlamaSharp` | `LlamaSharpChatClientProvider.Register()` | `LlamaCpp` |
| `Kudos.Agent.Sdk.Providers` | `RemoteChatClientProvider.Register()` | `Anthropic`, `OpenAI`, `AzureOpenAI`, `OpenAICompatible`, `Bedrock`, `Gemini` |

Call them once at startup, before `ChatClientFactory.CreateAsync`. Both are idempotent. Both classes are `sealed`, implement `IChatClientProvider`, and have a public parameterless constructor (`ChatClientFactory.Register(new RemoteChatClientProvider())` is equivalent).

Remote credentials come from the environment dictionary — the environment first, then the OS secret store (table in [QUICK-START.md](QUICK-START.md#credentials-for-remote-models), details in [SECRETS.md](SECRETS.md)). The `Anthropic` client takes `ANTHROPIC_API_KEY` / `ANTHROPIC_AUTH_TOKEN` from it and passes the key to the Anthropic SDK; with neither, the Anthropic SDK's own resolution (an `ant auth login` profile) applies. `AnthropicChatClient.Create(ModelSettings, HttpClient? = null, IReadOnlyDictionary<string, string?>? env = null)` takes the dictionary directly.

The LlamaSharp package also exposes `LlamaSharpChatClient` (an `IChatClient` with a static `CreateAsync(string modelPath, int gpuLayers, int mainGpu, int contextSize, LlamaSharpChatClientOptions? options = null, CancellationToken cancellationToken = default)`) for hosts that create the local client directly. `LlamaSharpChatClientOptions.SeqMax` / `KvUnified` load the context with several sequences (config `seqMax` / `kvUnified`); a request then runs on the sequence named by its slot (`LlamaServerSlot.OptionKey` in `ChatOptions.AdditionalProperties`; no slot = sequence 0; a slot ≥ `SeqMax` throws `ArgumentOutOfRangeException`), and `LlamaSharpChatClient.SequenceCount` reports the count. `StatefulLlamaExecutor` has per-sequence overloads (`InferAsync(prompt, sequence, …)`, `ContinueAsync(delta, sequence, …)`, `IsStateValid(sequence)`, `HasEndedCleanly(sequence)`, `GetLastGeneratedTokens(sequence)`, `GetFullRegenerationCount(sequence)`), `SequenceCount`, `SequenceCapacity` and `GetBatchedStepStats()`. Images (in-process projector): `LlamaSharpChatClientOptions.Mmproj` / `ImageMinTokens` / `ImageMaxTokens` load the model's projector (config `mmproj` / `imageMinTokens` / `imageMaxTokens`) and `LlamaSharpChatClient.SupportsImages` reports it; the executor takes a projector in the constructor `StatefulLlamaExecutor(LLamaWeights, IContextParams, ILogger?, MtmdWeights?)` (`SupportsImages`, `MediaMarker` = `<__media__>`), prompts with images as `IReadOnlyList<PromptSegment>` (`InferAsync(segments, sequence, …)`, `ContinueAsync(segments, sequence, …)` — the core package's `QwenPromptRenderer.RenderSegmentsWithGenerationPrompt` / `RenderDeltaSegments` render them; `PromptSegment.FromText` / `FromImage(PromptImage(data, mediaType, sha256))`), and reports `GetKvUsage(sequence)` (positions and KV cells — they differ after an image) and `GetLastPromptStats(sequence)` (cells reused and decoded by the last call). MTP speculative decoding: `LlamaSharpChatClientOptions.Mtp` (`bool?`, config `mtp` — null by default = on exactly when the loaded model has an MTP head; `true` requires one, `false` turns it off; works with any `SeqMax`) loads the model's MTP head and drafts one token per step; the executor takes it in the constructor `StatefulLlamaExecutor(LLamaWeights, IContextParams, ILogger?, MtmdWeights?, bool mtp)` (weights loaded with `ModelParams.LoadMTP`, parameters with `RecurrentRollbackSnapshots` ≥ 1) and reports `SpeculativeDecoding`, `GetSpeculativeStats()` (drafted / kept) and `GetLastGenerationStats(sequence)` (tokens and time of the last generation, without its prompt). `LlamaSharpChatClient.GetService` returns the client or its `StatefulLlamaExecutor`. See [MODELS.md](MODELS.md) and [KV-CACHE.md](KV-CACHE.md).

### IChatClientProvider

Implement it to add your own model backend (or to replace a built-in one — the last registration for a type wins):

```csharp
public interface IChatClientProvider
{
    IReadOnlyCollection<ModelType> ModelTypes { get; }
    Task<IChatClient> CreateAsync(ModelConfig model, ChatClientProviderContext context, CancellationToken cancellationToken);
    string? CredentialWarning(ModelConfig model, IReadOnlyDictionary<string, string?> environment);
}

public sealed record ChatClientProviderContext(HttpClient? HttpClient, IReadOnlyDictionary<string, string?> Environment);
```

---

## Kudos.Agent.Safety

### SafetyOperation

```csharp
public sealed record SafetyOperation(string Kind, string? Path = null, string? Command = null, string? Detail = null);
```

The operation passed to the safety gate and to your approval handler.

| Property | Description |
|---|---|
| `Kind` | `"read"`, `"write"` or `"exec"`. |
| `Path` | Target path for `read` / `write`. |
| `Command` | The command for `exec` (for `execute_code`: `execute_code(<language>)`). |
| `Detail` | Display-only text for the approval prompt — for `execute_code`, the full generated script. Never evaluated by the policy. |
| `AgentPath` (init) | The sub-agent that asks: `null` for the top-level session, `reviewer` for a sub-agent, `reviewer/checker` for a nested one. Display-only. |

### SafetyDecision, SafetyEffect

```csharp
public sealed record SafetyDecision(SafetyEffect Effect, string? Reason = null);

public enum SafetyEffect { Auto, Ask, Reject }
```

`Auto` runs without asking, `Ask` calls the approval handler (denied when there is none), `Reject` denies. A denial is returned to the model as a failed tool result, not as an exception.

### ISafetyGate

```csharp
public interface ISafetyGate
{
    Task<SafetyDecision> CheckAsync(SafetyOperation operation, CancellationToken cancellationToken = default);
}
```

| Implementation | Description |
|---|---|
| `MatrixSafetyGate(SafetyConfig config, string baseDir, string? homeDir = null)` | The default gate: approval policy × sandbox mode × exec rules, writable roots, argument scope. `Decide(SafetyOperation)` returns the pure policy decision. See [SAFETY.md](SAFETY.md). |
| `AutoApproveSafetyGate()` | Approves everything. Intended for tests that isolate tool behavior from safety. |

The session's gate is `ToolContext.SafetyGate` (built lazily from `config.Safety` and the work directory); a host can replace it through `ConfigureToolContext`. The built-in `execute_code` always asks, even when the gate would auto-approve.

---

## Kudos.Agent.Tools

### ITool

```csharp
public interface ITool
{
    string Name { get; }          // the name the model calls — unique per session
    AIFunction Function { get; }  // definition shown to the model (name, description, JSON schema) + implementation
}
```

### ToolFunction

| Member | Description |
|---|---|
| `static AIFunction Create(Delegate method, string name, string description)` | `AIFunctionFactory.Create` with lenient binding of `string` parameters (a number or boolean argument is converted to text instead of failing). A `CancellationToken` parameter receives the turn's token. |
| `static AIFunction CreateWithContent(Delegate method, string name, string description)` | `Create` for a tool that returns images: an `AIContent` / `IEnumerable<AIContent>` result is passed to the tool loop as is; any other result is serialized as by default. See [TOOLS.md](TOOLS.md#returning-images). |
| `static string? ToText(object? value)` | The conversion used for `string` parameters. |

### ToolResult

The JSON envelope tool results use: `{"ok":bool, "exitCode"?:int, "output"?:string, "truncated"?:bool, "count"?:int, "error"?:string}`.

| Member | Description |
|---|---|
| `static string Ok(string? output = null, int? exitCode = null, int? count = null, bool truncated = false)` | Success result. |
| `static string Fail(string error, string? output = null, int? exitCode = null)` | Failure result — the model reads the error and can correct itself. |

### SafetyCheck

| Member | Description |
|---|---|
| `static Task<string?> CheckAsync(ToolContext context, SafetyOperation operation, CancellationToken cancellationToken)` | Asks `context.SafetyGate`, records the decision in the rollout, calls the approval handler for `Ask`. Returns `null` when allowed, otherwise the denial text. |
| `static Task<string?> CheckAsync(ToolContext context, SafetyOperation operation, Task<SafetyDecision> decisionTask, CancellationToken cancellationToken)` | Same, for a decision the tool already computed (for example escalated from `Auto` to `Ask`). |

### ToolContext

```csharp
public sealed record ToolContext
```

The execution environment shared by a session's tools; `AddTool` factories receive it and `ConfigureToolContext` can adjust it. Members relevant to hosts:

| Member | Default | Description |
|---|---|---|
| `string WorkDir { get; init; }` | session `work/` | Root for file operations and execution. |
| `KudosAgentConfig Config { get; init; }` | — | The session config. |
| `ISafetyGate SafetyGate { get; set; }` | `MatrixSafetyGate` | The gate tools consult. |
| `Func<SafetyOperation, Task<bool>>? ApprovalHandler { get; set; }` | from `UseApprovalHandler` | `null` = fail-closed. |
| `ShellSpec Shell { get; set; }` | `ShellSpec.Bash` | Shell for `bash_command` / bash `execute_code`. `ShellSpec(string FileName, Func<string, IReadOnlyList<string>> BuildArgumentList)`; the default runs `/bin/bash -c <command>`. |
| `int DefaultTimeoutMs { get; set; }` | `120000` | Default timeout for `bash_command` / `execute_code`. |
| `int MaxTimeoutMs { get; set; }` | `600000` | Cap for a timeout the model requests. |
| `int ExecOutputBytes { get; set; }` | 100 KB | Output limit for `bash_command` / `execute_code`. |
| `int GrepCaptureBytes { get; set; }` / `int GrepReturnBytes { get; set; }` | 16 MB / 100 KB | `grep_search` capture and return limits. |
| `IReadOnlyList<string> ExcludeDirs { get; set; }` | `node_modules`, `.git`, `dist` | Directories the search tools skip. |
| `ITodoStore TodoStore { get; set; }` | `InMemoryTodoStore` | Storage for `write_todos` / `read_todos`. |
| `AttachmentStore Attachments { get; set; }` | `<WorkDir>/attachments/` with `config.Attachments` | Where attachments and tool-result images are saved. |
| `string SkillsPath { get; set; }` | empty (= `config.Skills.SkillsPath` against the current directory) | Skills root for `load_skill`. |
| `ISet<string> RemovedTools { get; }` | empty | Built-in tools to leave out (filled by `RemoveTool`). |
| `IList<Func<ToolContext, ITool>> AdditionalTools { get; }` | empty | Host tools (filled by `AddTool` / `AddTools`). |

`ToolRegistry.CreateDefault(ToolContext)` builds the session's tool list from a context: the built-in tools minus `RemovedTools`, then `AdditionalTools`; `Register(ITool)` throws `InvalidOperationException` for a duplicate name.

Built-in tool names: `execute_code`, `bash_command`, `read_file`, `write_file`, `edit_file`, `apply_patch`, `glob_files`, `grep_search`, `write_todos`, `read_todos`, `load_skill`, `compress_context` (not registered when `contextCompression.mode` is `backstop`), `view_image` (registered only when the primary model takes images), plus `mcp_<server>_<tool>` for each configured MCP server. Details: [TOOLS.md](TOOLS.md).

---

## Kudos.Agent.Sessions

### SessionLayout

```csharp
public sealed record SessionLayout
```

| Member | Description |
|---|---|
| `static SessionLayout Create(string resultsRoot, string sessionId)` | Validates the id (`[A-Za-z0-9._-]`, 1–64 characters; otherwise `ArgumentException`). |
| `static string NewSessionId(DateTime? now = null)` | `yyyyMMdd-HHmmss-xxxx` (UTC). |
| `static bool IsValidSessionId(string id)` | The id check used by `Create`. |
| `ResultsRoot`, `SessionId` | The inputs. |
| `SessionDir`, `WorkDir`, `RolloutPath`, `StatusPath` | `<root>/<id>/`, `…/work/`, `…/rollout.jsonl`, `…/status.json`. |
| `void Initialize()` | Creates the session and work directories (idempotent; the session does this on build). |

More on the session files: [SESSIONS.md](SESSIONS.md).

---

## Kudos.Agent.Attachments

Images and PDFs for a turn or from a tool. Guide: [ATTACHMENTS.md](ATTACHMENTS.md).

| Type | Members |
|---|---|
| `sealed class AttachmentStore` | `AttachmentStore(string workDir, AttachmentsConfig? limits = null)`; `Attachment ImportFile(string sourcePath)` / `Attachment ImportBytes(byte[] data, string? name)` — check the size (`attachments.maxFileMb`, not empty) and the type by content (PNG / JPEG / WebP / GIF / PDF), then save under `<workDir>/attachments/` (same content under the same name is reused; a name taken by other content gets the first 8 hex digits of the SHA-256 appended); throw `AttachmentException`. `static string? Check(string workDir, AttachmentRef reference, out byte[]? data)` / `static Attachment? Load(…, out string? problem)` — file present and matching its SHA-256. `WorkDir`, `Limits`, `const string DirectoryName = "attachments"`, `static string Sha256Hex(ReadOnlySpan<byte>)` |
| `sealed record Attachment(AttachmentRef Ref, byte[] Data)` | An imported attachment. |
| `sealed record AttachmentRef` | What the rollout records: `Path` (work-directory relative), `MediaType`, `Sha256`, `Bytes`, `Name`, `Width?` / `Height?`, `ReferenceOnly?`; `bool IsImage`; `string ReferenceText()` → `[image: plot.png 640x480 — attachments/plot.png]`. |
| `sealed class AttachmentException : Exception` | Unsupported type, too large, empty, missing file, too many attachments, image for a text-only model. |
| `static class InputModalities` | `IReadOnlySet<string> For(ModelConfig)` (configured list + `text`, or the provider default), `bool SupportsImages(ModelConfig)`, constants `Text` / `Image` / `Pdf`, `Known`. |
| `static class MediaSniffer` | `string? Detect(ReadOnlySpan<byte>)`, `(int Width, int Height)? TryGetImageSize(…)`, `int EstimatePdfPages(…)`, `IsImage` / `IsPdf`, the media-type constants. |
| `static class AttachmentMessages` | Message helpers: `ToDataContent(Attachment)`, `BuildUserInput(text, attachments, modalities, maxPerMessage, modelId?)` (what a turn sends and records), `Validate(attachments, modalities, maxPerMessage, modelId?)` (the same checks without building the message) — both also take the `ModelConfig` instead of the id, so the "cannot take images" error names the fix for the model type (`config.mmproj` for `LlamaCpp`, `--mmproj` + `inputModalities` for `OpenAICompatible`), `ToolResult(text, media)`, `ContentsOf` / `MediaOf` / `TextOf` (read a tool result value), `WithoutMedia(ChatMessage)` (media → reference sentences), `RelocateToolResultMedia(messages)` (the OpenAI-format rewrite — see [ATTACHMENTS.md](ATTACHMENTS.md#openai-format-tool-results)), `const string PathProperty`. |

---

## Kudos.Agent.Rollout

The payloads carried by `AgentCompacted` and `AgentGuardTriggered`:

| Type | Properties |
|---|---|
| `CompactionEvent` | `string Summary`, `string? Trigger` (`"backstop"` or `"tool"`), `string? Outcome` (`"summary"` or `"reset"`), `int? CompactedMessages`, `int? TokensBefore` / `int? TokensAfter` (approximate, chars / 4), `string? FailureReason` (set only when it fell back to a reset) |
| `GuardEvent` | `string Kind`, `string Detail` |

Both derive from `RolloutEvent`, the base of every line in `rollout.jsonl`. See [SESSIONS.md](SESSIONS.md).

---

## Kudos.Agent.Secrets

API keys and tokens: the environment first, then the OS secret store (macOS Keychain / Linux libsecret / Windows Credential Manager, service `kudos-dotnet-agent`, key = the variable name). Guide: [SECRETS.md](SECRETS.md).

| Member | Description |
|---|---|
| `static Dictionary<string, string?> SecretsLoader.Resolve(IReadOnlyDictionary<string, string?> environment, IEnumerable<string> keys, ISecretStore? store = null)` | A case-insensitive copy of `environment` with each of `keys` that is missing or empty filled from `store` (default `SecretStores.Default`). Never throws for a store failure. Registers the keys' values with `Redactor`. |
| `static Dictionary<string, string?> SecretsLoader.ResolveProcessEnvironment(KudosAgentConfig config, ISecretStore? store = null)` | `Resolve(ChatClientFactory.ProcessEnvironment(), KeysFor(config), store)` — what a host passes to `WithEnvOverrides` and `CreateAsync`. |
| `static IReadOnlyList<string> SecretsLoader.KeysFor(KudosAgentConfig config)` | The credential keys of every `models[]` entry, then `EnvRefs(config)`, without duplicates. |
| `static IReadOnlyList<string> SecretsLoader.EnvRefs(KudosAgentConfig config)` | The `${VAR}` names in `mcpServers[].env`, `a2a.agents[].headers` and `a2a.server.token`. |
| `static IReadOnlyList<string> SecretsLoader.CredentialKeys(ModelType type)` / `DefaultKeys` | The credential variables of a model type / of all types (secrets only — not endpoints or regions). |
| `static ISecretStore SecretStores.Default { get; set; }` | The store used when none is passed. Defaults to `ForCurrentOs()`, or `None` when `KUDOS_SECRET_STORE` is `off` / `0` / `false` / `no` / `none`. |
| `static ISecretStore SecretStores.ForCurrentOs()` / `None` | The native store of this OS / a store that reads nothing and refuses writes. |
| `const string SecretStores.ServiceName` / `DisableVariable` | `"kudos-dotnet-agent"` / `"KUDOS_SECRET_STORE"`. |
| `interface ISecretStore` | `string? Get(string key)` (null when missing or unreadable — never throws), `void Set(string key, string value)`, `bool Remove(string key)` (false when there was none); writes throw `SecretStoreException`. |
| `static string Redactor.Redact(string? text)` | Replaces known key formats and registered values with `Redactor.Mask` (`"[REDACTED]"`). |
| `static JsonElement Redactor.RedactJson(JsonElement element)` | Also masks properties whose name is secret-like (`IsSecretName`); returns the input unchanged when nothing is hidden. |
| `static void Redactor.RegisterSecret(string? value)` / `static bool Redactor.IsSecretName(string name)` | Adds a value to hide (8 characters or more) / tests a property name. |

---

## Kudos.Agent.Licensing

License Keys (guide: [LICENSING.md](LICENSING.md)).

| Member | Description |
|---|---|
| `static KudosLicense KudosLicensing.Resolve(string? builderLicense = null)` | The key sessions use now: the first that verifies of the OS secret store (`license`), `builderLicense`, the process-wide key, the entry assembly's embedded resource `kudos-agent.license`. Throws `LicenseException` (every source and its reason, or `Missing`). Runs the integrity check first. |
| `static KudosLicense KudosLicensing.Parse(string licenseText)` | Verifies a key (signature, fields, expiry) without using it. Throws `LicenseException`. |
| `static void KudosLicensing.UseLicense(string? licenseText)` / `UseLicense(Assembly assembly, string resourceName = "kudos-agent.license")` | Sets the process-wide key (verified now — an invalid key throws and keeps the previous one; `null` clears it) / reads it from an embedded resource of `assembly` (a plug-in's own assembly). |
| `static KudosLicense KudosLicensing.Require(LicenseFeature feature, string? builderLicense = null)` | `Resolve` + throws `LicenseException` (`FeatureNotLicensed`) when the level does not include `feature`. |
| `const string KudosLicensing.SecretStoreKey` / `EmbeddedResourceName` | `"license"` / `"kudos-agent.license"`. |
| `sealed class KudosLicense` | `Id`, `Subject`, `Issuer`, `IssuedAt`, `Expires` (`DateOnly`, last valid day, UTC), `Level`, `MaxConcurrent` (0 = no limit), `Product`, `Source`; `bool Allows(LicenseFeature)`; `static bool Allows(LicenseLevel, LicenseFeature)`; `static LicenseLevel RequiredLevel(LicenseFeature)`. |
| `sealed class LicenseException : Exception` | `LicenseFailure Failure` — `Missing`, `Malformed`, `InvalidSignature`, `Expired`, `FeatureNotLicensed`, `ConcurrencyLimit`, `IntegrityFailure`. |
| `enum LicenseLevel` / `enum LicenseFeature` | `Development`, `Professional`, `Enterprise`, `Unlimited` / `RemoteModels`, `Mcp` (professional), `A2A`, `Multimodal` (enterprise). |

---

## Kudos.Agent.Workflows

Package `Kudos.Agent.Sdk.Workflows` (adds `Microsoft.Agents.AI.Workflows` 1.28.0). Runs several `CodingAgentSession`s as one workflow on Microsoft Agent Framework Workflows. Guide: [MULTI-AGENT.md](MULTI-AGENT.md).

```csharp
await using var security = await securityBuilder.BuildAsync();
await using var performance = await performanceBuilder.BuildAsync();

var review = AgentWorkflows.Concurrent(
    [new WorkflowParticipant("security", security), new WorkflowParticipant("performance", performance)],
    options: new AgentWorkflowOptions { Name = "review", Recorder = parent.Recorder, ParticipantTimeout = TimeSpan.FromMinutes(10) });

AgentWorkflowResult result = await review.RunAsync("Review the change in src/Orders.cs");
Console.WriteLine(result.Status);   // ok | partial | failed
Console.WriteLine(result.Output);   // "## security\n\n…\n\n## performance\n\n…"
```

### AgentWorkflows

| Member | Description |
|---|---|
| `static AgentWorkflow Concurrent(IEnumerable<WorkflowParticipant> participants, WorkflowAggregator? aggregator = null, AgentWorkflowOptions? options = null)` | Fan-out / fan-in: every participant gets the same input and runs at the same time. Once all have reported — including the ones that failed, timed out or answered nothing — `aggregator` combines the results (default `DefaultAggregate`). Throws `ArgumentException` for no participants, more than `MaxFanOut`, an empty or duplicate name, or the same session twice. Participants that share an in-process model (`CodingAgentSession.ExclusiveModel`) take turns on it: a warning is logged and `AgentWorkflow.Serialized` is `true`. |
| `static AgentWorkflow Sequential(IEnumerable<WorkflowParticipant> participants, AgentWorkflowOptions? options = null)` | A pipeline: the first participant gets the input; each next one gets the input followed by `## Result of the previous step (<name>)` and that answer. The output is the last answer. A step that fails, times out or answers nothing stops the pipeline; the rest are listed in `Missing`. |
| `static string DefaultAggregate(IReadOnlyList<ParticipantResult> results)` | One section per participant in workflow order: `## <name>`, then its answer, or `[<status>: <error>]` when it has none. |

`delegate ValueTask<string> WorkflowAggregator(string input, IReadOnlyList<ParticipantResult> results, CancellationToken cancellationToken)` receives every participant's result in workflow order (failed ones included). If no participant answered, the run fails without calling it; if it throws, the run fails.

### AgentWorkflow

| Member | Description |
|---|---|
| `Task<AgentWorkflowResult> RunAsync(string input, CancellationToken cancellationToken = default)` | Runs the workflow. Each participant runs one turn of its own session (recorded in that session as usual). Runs of one workflow are serialized; each run builds a fresh MSAF workflow. Cancelling ends every participant's turn (recorded as `turn-interrupted`), records `workflow-end` with status `cancelled`, and throws `OperationCanceledException`. A failed run is not resumed — run it again. |
| `AIAgent AsAIAgent(string? description = null)` | The workflow as an MSAF `AIAgent`: each run is one workflow run on the text of the run's user messages and answers with `Output`; a run with status `failed` throws. Pass it to `AgentSessionBuilder.AddSubAgent(name, description, agent)` to let a session's model call the workflow. |
| `string Name`, `string Kind`, `IReadOnlyList<WorkflowParticipant> Participants`, `bool Serialized` | The workflow's name (`AgentWorkflowOptions.Name`), `"concurrent"` or `"sequential"`, the participants, and whether participants share an in-process model. |

### Workflow types

| Type | Members |
|---|---|
| `WorkflowParticipant` (record) | `string Name` (unique, non-empty), `CodingAgentSession Session` (in a workflow at most once), `string? Instructions { get; init; }` (the participant's role: when set, its input is `## Your instructions` + the instructions, then `## Task` + the workflow input). |
| `AgentWorkflowOptions` (record) | `string Name` (default `"workflow"`), `int MaxFanOut` (default 4 — pass `SubAgentPolicy.FromConfig(config.SubAgentPolicy).MaxFanOut` to follow `subAgentPolicy.maxFanOut`), `TimeSpan? ParticipantTimeout` (default none; exceeding it gives that participant the status `timeout`), `RolloutRecorder? Recorder` (where `workflow-start` / `workflow-step` / `workflow-end` go — for example a session's `Recorder`; default none), `ILogger? Logger` (default none). |
| `AgentWorkflowResult` (record) | `string RunId`, `string Status` (`WorkflowStatus`), `string Output`, `IReadOnlyList<ParticipantResult> Results` (participants that ran, in workflow order), `IReadOnlyList<string> Missing` (participants without a result), `string? Error`. |
| `ParticipantResult` (record) | `string Participant`, `string SessionId`, `string Status` (`SubAgentStatus`: `ok`, `empty`, `failed`, `timeout`, `cancelled`), `string Text` (the answer; empty unless `ok`), `long DurationMs`, `string? Error`. |
| `WorkflowStatus` (static) | `Ok = "ok"` (every participant answered), `Partial = "partial"` (output produced, some participants did not answer), `Failed = "failed"` (no usable output). |

For other graphs (switches, custom fan-out, nesting a workflow inside another), use MSAF's `WorkflowBuilder` directly with `CodingAgentSessionAgent` participants. MSAF's handoff orchestration is not supported: it injects handoff tools into each run, while a session's tool list is fixed when it is built.

---

## See Also

- [QUICK-START.md](QUICK-START.md) — installation and a minimal host
- [EXAMPLES.md](EXAMPLES.md) — the sample host and focused snippets
- [ARCHITECTURE.md](ARCHITECTURE.md) — how the session, tools, guards and model clients fit together
- Package READMEs — [Kudos.Agent.Sdk](packages/Kudos.Agent.Sdk.md) · [Kudos.Agent.Sdk.LlamaSharp](packages/Kudos.Agent.Sdk.LlamaSharp.md) · [Kudos.Agent.Sdk.Providers](packages/Kudos.Agent.Sdk.Providers.md) · [Kudos.Agent.Sdk.Workflows](packages/Kudos.Agent.Sdk.Workflows.md)
