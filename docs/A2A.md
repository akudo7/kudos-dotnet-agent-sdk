# A2A — Agent2Agent

The agent can talk to other agents over the [A2A protocol](https://a2a-protocol.org) (v1.0), in both directions:

- **Client** — remote A2A agents become tools of a session: `a2a_<name>`.
- **Server** — a coding-agent session is exposed over A2A, so other agents can send it work.

Both live in the project `src/Kudos.Agent.Sdk.A2A` (namespace `Kudos.Agent.A2A`, target `net10.0`). It is **not published as a NuGet package yet**: its dependencies are preview releases (`Microsoft.Agents.AI.A2A` / `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` `1.28.0-preview.261106.1`, `A2A` `1.0.0-preview4`), and the server needs the ASP.NET Core shared framework (`Microsoft.AspNetCore.App`). The core `Kudos.Agent.Sdk` package has no A2A dependency — it only carries the config section, the record types and the MCP connection sharing that A2A uses.

Everything is off by default: nothing is sent or served unless the `a2a` config section says so ([CONFIGURATION.md](CONFIGURATION.md#10-a2a)).

## Contents

1. [Client: Remote Agents as Tools](#client-remote-agents-as-tools)
2. [Server: Serving the Agent](#server-serving-the-agent)
3. [Limits](#limits)

---

## Client: Remote Agents as Tools

```json
"a2a": {
  "agents": [
    { "name": "estimator", "url": "http://127.0.0.1:8770", "headers": { "Authorization": "Bearer ${ESTIMATOR_TOKEN}" } }
  ]
}
```

```csharp
using Kudos.Agent.A2A;

// once per process: reads every agent's card (an agent whose card cannot be read is skipped with a warning)
Console.WriteLine(A2AToolSource.Notice(config.A2A.Agents));      // where requests may go (null when none)
using var a2a = await A2AToolSource.ConnectAsync(config.A2A.Agents, logger);

// per session: one a2a_<name> tool per agent
var builder = new AgentSessionBuilder().UseConfig(config).UseChatClient(client).UseApprovalHandler(Approve);
await using var session = await a2a.AddTo(builder).BuildAsync();
```

What the `a2a_<name>` tool does:

| Topic | Behaviour |
|---|---|
| Arguments | One string, `query` — the request. The remote agent does not see the session's conversation. |
| Description | The card's name and description plus its skills, so the model knows when to use it. |
| Approval | Operation kind `a2a`, `Command` = `<name> (<url>)`, `Detail` = the full request text. With `approvalPolicy: on-request` every call asks (show `Detail` — it is exactly what leaves the machine); with `never` calls go out without asking. Denied → an error result, nothing is sent. |
| Conversation | Each session keeps one remote conversation (`contextId`) per agent, so follow-up requests see the earlier ones. |
| Remote question | When the remote task stops in `input-required` (for example it asks for an approval), the question comes back as the tool result, marked `[<name> needs input]`. The model's next call to the same tool answers it and continues the same remote task. |
| Answer | The text of the task's artifacts (else its status message), truncated to 20 000 characters. |
| Failures | Connection refused, timeout, HTTP 401/403/404, and tasks that end `failed` / `rejected` / `canceled` become error results that tell the model what to do next ("do not retry; report to the user …"). The session never stops because of a remote agent. |
| Timeout | `timeoutSeconds` per call; on timeout or when the caller's turn is cancelled, the remote task is cancelled (best effort). |
| Records | Every call is an `a2a-call` rollout event: agent, URL, `contextId`, `taskId`, final state, request (2 000 characters), answer size, duration, error ([SESSIONS.md](SESSIONS.md)). |

The tool list is fixed when the session starts — `A2AToolSource` reads the cards once, and no tool appears or disappears between turns ([KV-CACHE.md](KV-CACHE.md)). Order: the built-in tools, host tools (`agent_*`, `a2a_*` in the order they were added), then `mcp_*`.

A skill can tell the model to delegate: name the tool in the skill body (`Call a2a_estimator with …`) — nothing else is needed.

---

## Server: Serving the Agent

```json
"safety": { "approvalPolicy": "on-request" },
"a2a": {
  "server": {
    "enabled": true,
    "listen": "http://127.0.0.1:8765",
    "approval": "local",
    "token": "${A2A_SERVER_TOKEN}"
  }
}
```

With the bundled CLI:

```bash
dotnet src/Host.Cli/bin/Release/net10.0/Host.Cli.dll serve-a2a --config json/kudos-agent.json
# A2A server: http://127.0.0.1:8765/  (card: http://127.0.0.1:8765/.well-known/agent-card.json)
#   HTTP+JSON: http://127.0.0.1:8765/a2a/agent
#   JSONRPC:   http://127.0.0.1:8765/a2a/agent-rpc
```

In your own host:

```csharp
await using var mcp = await McpClientManager.ConnectSharedAsync(config.McpServers, workDir);   // once
var skillsIndex = AgentSessionBuilder.LoadSkillsIndex(config);                                 // once

await using var server = await A2AServerHost.StartAsync(new A2AServerHostOptions
{
    Config = config,
    ResultsDirectory = Path.Combine(workDir, "results"),
    LocalApproval = AskTheUserAsync,                    // approval = local
    Skills = SkillLoader.LoadIndex(Path.GetFullPath(config.Skills.SkillsPath)),  // listed on the card
    Logger = logger,
    // the same assembly as your interactive sessions — same prompt, same tools
    CreateSessionBuilder = request => new AgentSessionBuilder()
        .UseConfig(config)
        .UseChatClient(client)
        .UseSkillsIndex(skillsIndex)
        .UseMcpConnection(mcp),
});
```

`A2AServerHost.StartAsync` refuses to start (`InvalidOperationException`) unless `a2a.server.enabled` is `true` and `safety.approvalPolicy` is `on-request` — an outside agent must never run commands without approval. `A2AServerHost.StartupProblem(config)` returns the reason without starting.

### How a request is served

| Topic | Behaviour |
|---|---|
| Conversations | One session per A2A `contextId`, with its own `results/<session id>/` (rollout, status, work directory — conversations never see each other's files). The map `contextId → session id` is kept in `<results>/a2a-contexts.json`; after a restart a known conversation resumes from its rollout. |
| Tasks | One message = one turn = one task: `submitted → working → (input-required) → completed / failed / canceled`. The final answer is an artifact named `result` (text). |
| Streaming | `message:stream` sends a `working` update per tool call (`tool: <name>` — the arguments are not sent), then the artifact and the final state. |
| Tools | Exactly the tools of a batch / REPL session built the same way: the built-in tools (`execute_code`, `bash_command`, file tools, search, todos, `load_skill`, `compress_context`), `mcp_*` from the shared connection (one server process for all conversations; approvals and records go to each conversation's session), and `a2a_*` if you add them (see call depth). |
| Approvals | `approval` = `local`: `LocalApproval` (the CLI asks on its terminal; without a handler everything is denied). `requester`: the task stops in `input-required` with the question — for `execute_code` the full code — and the caller's next message on the same task answers it (`yes` / `y` / `approve` / `allow` / `ok` approve, anything else denies). `reject`: every operation that needs approval is denied (read-only and auto-allowed operations still run). An approval not answered within `approvalTimeoutMinutes` is denied; the model sees the denial and the turn goes on. |
| Cancel | `tasks/cancel` stops the turn (recorded as `turn-interrupted`) and the task ends `canceled`. A requester that disconnects while a turn is working cancels it too. |
| Concurrency | At most `maxConcurrentTasks` turns at a time (more wait as `submitted`), one turn per conversation. |
| Call depth | Requests carry the metadata key `kudos.a2a.depth` (hops so far; `a2a_*` tools send their session's depth + 1). A request deeper than `maxCallDepth` is rejected — this stops agents calling each other in a loop. Pass `request.Depth` to `A2AToolSource.AddTo(builder, request.Depth)` when served sessions get `a2a_*` tools. |
| Authentication | With `token`, every request needs `Authorization: Bearer <token>` (fixed-time comparison, `401` otherwise); the card at `/.well-known/agent-card.json` stays public and declares the bearer scheme. Without a token the server only listens on loopback (validation). Plain HTTP — keep it on loopback or a trusted network. |
| Card | Name and description from `a2a.server.card`, `text` in and out, streaming, the interfaces, and the skills index as `skills`. MCP tools are not listed. |
| Records | Each state change is an `a2a-task` event in the conversation's rollout: task, context, state, how the requester authenticated (`bearer` / `none`), its address, the hop count, and a detail (the approval question, the failure reason). Tool calls and approvals are recorded as in any session, so `Host.Cli verify` / `quality` work on served sessions. |

### Safety checklist for serving

- Keep `approval: local` unless the calling agent is trusted to approve command execution on your machine (`requester` logs a warning at startup).
- Leave `safety.writableRoots` at its default (the conversation's work directory); do not add repository or product paths.
- Use `reject` for unattended servers: only read-only work and auto-allowed operations run.
- Keep `listen` on `127.0.0.1`; on a LAN set a `token` from an environment variable.

---

## Limits

- Only text parts are read and returned (no files or data parts).
- A task waiting for an answer lives in memory: a restarted server cannot continue it (the conversation can continue with a new message).
- Push notifications, the extended agent card and gRPC are not offered.
- Running the server inside a product process (Revit / Inventor / 3ds Max) is not verified yet.

---

## See Also

- [CONFIGURATION.md](CONFIGURATION.md#10-a2a) — the `a2a` section
- [SAFETY.md](SAFETY.md) — approvals for remote requests
- [TOOLS.md](TOOLS.md#a2a-agents-as-tools) — `a2a_<name>` tools
- [MULTI-AGENT.md](MULTI-AGENT.md) — sub-agents and workflows in the same process
- [SESSIONS.md](SESSIONS.md) — session records
