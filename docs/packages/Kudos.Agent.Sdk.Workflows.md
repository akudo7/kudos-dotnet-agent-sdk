# Kudos.Agent.Sdk.Workflows v0.6.0

**Multi-agent workflows for the Kudos Agent SDK — several coding-agent sessions as one run**

[Features](#features) · [Installation](#installation) · [Quick Start](#quick-start) · [Documentation](#documentation) · [License](#license)

---

## Overview

**Kudos.Agent.Sdk.Workflows** runs several [Kudos.Agent.Sdk](Kudos.Agent.Sdk.md) coding-agent sessions as one workflow on Microsoft Agent Framework Workflows (`Microsoft.Agents.AI.Workflows` 1.28.0).

Version 0.x: the API may still change between minor versions.

---

## Features

| Workflow | What it does |
|---|---|
| `AgentWorkflows.Concurrent` | Fan-out / fan-in: every participant gets the same input and runs at the same time; once all have reported, an aggregator combines the answers (default: one `## <name>` section per participant). |
| `AgentWorkflows.Sequential` | A pipeline: each participant gets the input plus the previous participant's answer; the last answer is the output. |

Notes:

- Each participant runs one turn of its own session, recorded there as usual. The run is recorded as `workflow-start` / `workflow-step` / `workflow-end` in `AgentWorkflowOptions.Recorder`.
- A participant that fails, times out (`ParticipantTimeout`) or answers nothing still reports a result, so the workflow never waits forever; the run is then `partial` (or `failed` when nobody answered).
- A concurrent workflow has at most `MaxFanOut` participants (default 4 — `subAgentPolicy.maxFanOut`).
- Participants on one in-process local model take turns on it (a warning is logged and `AgentWorkflow.Serialized` is true). For real parallelism use remote models or llama-server slots.
- A failed run is not resumed: run it again.
- `WorkflowParticipant.Instructions` gives each participant its role (prepended to the input as `## Your instructions` / `## Task`).

---

## Installation

```bash
dotnet add package Kudos.Agent.Sdk.Workflows --version 0.6.0
```

**Requirements:** .NET SDK 10 (target `net10.0`).

---

## Quick Start

```csharp
using Kudos.Agent.Workflows;

await using var security = await securityBuilder.BuildAsync();        // AgentSessionBuilder, as usual
await using var performance = await performanceBuilder.BuildAsync();

var review = AgentWorkflows.Concurrent(
    [
        new WorkflowParticipant("security", security) { Instructions = "Look only for security problems." },
        new WorkflowParticipant("performance", performance) { Instructions = "Look only for performance problems." },
    ],
    options: new AgentWorkflowOptions { Name = "review", Recorder = parent.Recorder });

var result = await review.RunAsync("Review the change in src/Orders.cs");
// result.Status: ok | partial | failed — result.Output: the aggregate — result.Results: one entry per participant
```

Let a session's model call the workflow as a sub-agent:

```csharp
var parentSession = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(chatClient)
    .AddSubAgent("review", "Runs the security and performance reviewers on a change.", review.AsAIAgent())
    .BuildAsync();
```

A runnable sample is in [`samples/MultiAgentHost`](../../samples/MultiAgentHost/).

---

## Documentation

- **[MULTI-AGENT.md](../MULTI-AGENT.md)** — sub-agents and workflows: approvals, depth, models and parallelism, records
- **[API.md](../API.md#kudosagentworkflows)** — the `Kudos.Agent.Workflows` types
- **[EXAMPLES.md](../EXAMPLES.md#the-multi-agent-sample-samplesmultiagenthost)** — the multi-agent sample walkthrough
- **[README.md](../README.md)** — every SDK document

---

## License

Kudos.Agent.Sdk.Workflows is distributed under the End User License Agreement (the LICENSE file included in this package, also at the repository root):

- **Evaluation** — free for up to 30 days; prototypes allowed, not in production, not distributed
- **Commercial license** — every other use (embedding the SDK in your own applications, production, commercial products, standalone CLI use) requires a commercial license and License Key from the Licensor

The sample code under `samples/` may be viewed, copied and modified for use in your applications. Prompts derived from OpenAI Codex are under the Apache License 2.0 — see the THIRD-PARTY-NOTICES.txt file included in this package.

---

## Author

**Hand-crafted by [Akira Kudo](https://www.linkedin.com/in/akira-kudo-4b04163/) in Tokyo, Japan**

Copyright © 2026 Akira Kudo
