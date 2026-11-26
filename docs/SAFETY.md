# Safety and Approvals

This guide explains how the SDK decides whether a tool call runs automatically, needs the
user's approval, or is refused. It also covers what an embedding host must implement and which
settings to use.

## Contents

1. [Model in One Page](#model-in-one-page)
2. [Configuration Keys](#configuration-keys)
3. [Operations](#operations)
4. [Approval Policies and Sandbox Modes](#approval-policies-and-sandbox-modes)
5. [Exec Rules](#exec-rules)
6. [Argument-Scope Checks](#argument-scope-checks)
7. [Workspace Boundaries](#workspace-boundaries)
8. [The Approval Handler Contract](#the-approval-handler-contract)
9. [Destructive Operations](#destructive-operations)
10. [How the Safety Settings Reach the Prompt](#how-the-safety-settings-reach-the-prompt)
11. [Custom Safety Gates](#custom-safety-gates)
12. [Recommended Settings for Embedding Hosts](#recommended-settings-for-embedding-hosts)
13. [Limitations](#limitations)

---

## Model in One Page

Each gated tool builds a `SafetyOperation` and asks the session's `ISafetyGate` for a
`SafetyDecision` with one of three effects:

| `SafetyEffect` | Meaning |
|----------------|---------|
| `Auto` | Run without asking |
| `Ask` | Run only if the host's approval handler returns `true` |
| `Reject` | Do not run |

The default gate is `MatrixSafetyGate`
(`SafetyGate.cs`). It combines three inputs:

- **approval policy**: whether the user can be asked (`on-request`) or not (`never`)
- **sandbox mode**: what may be written without asking (`read-only` / `workspace-write` /
  `danger-full-access`)
- **exec rules**: command-prefix rules (`allow` / `ask` / `deny`) and an optional `execDefault`

On top of the matrix, some checks always apply and cannot be overridden by rules:

- shell-substitution metacharacters in commands are rejected
- argument-scope checks run on every command that would be auto-approved
- file tools cannot leave the session work directory

`SafetyCheck.CheckAsync` (`ToolContext.cs`)
resolves the decision. `Ask` calls the approval handler. **With no handler, `Ask` becomes a
refusal (fail-closed).** Every decision is written to the session rollout as a `safety-decision`
event, and every approval prompt as a `human-intervention` event (approved, latency).

---

## Configuration Keys

The `safety` section of `kudos-agent.json` maps to `SafetyConfig`. The full schema is in
[CONFIGURATION.md](CONFIGURATION.md).

```json
{
  "safety": {
    "approvalPolicy": "on-request",
    "sandboxMode": "workspace-write",
    "execDefault": null,
    "execRules": [
      { "prefix": ["git", "status"], "effect": "allow" },
      { "prefix": ["git", "push"],   "effect": "ask" },
      { "prefix": ["rm"],            "effect": "deny" }
    ],
    "writableRoots": [],
    "execArgScope": { "enabled": true }
  }
}
```

| Key | Type | Default | Values |
|-----|------|---------|--------|
| `safety.approvalPolicy` | enum | `on-request` | `on-request`, `never` |
| `safety.sandboxMode` | enum | `workspace-write` | `read-only`, `workspace-write`, `danger-full-access` |
| `safety.execDefault` | enum or null | `null` (use the sandbox matrix) | `allow`, `ask`, `deny` |
| `safety.execRules[]` | array | `[]` | `{ "prefix": [tokens...], "effect": "allow" \| "ask" \| "deny" }` |
| `safety.writableRoots[]` | string[] | `[]` (the session work directory) | Absolute paths, or paths relative to the work directory |
| `safety.execArgScope.enabled` | bool | `true` | `false` disables argument-scope checks |
| `safety.execArgScope.sensitiveReadRoots` | string[] | built-in list | Paths, or `$HOME/...` templates. **Replaces** the built-in list |

Enum values are case-insensitive and accept kebab-case (`on-request`), camelCase (`onRequest`), or
the C# name (`OnRequest`). The environment variables `SAFETY_APPROVAL_POLICY` and
`SAFETY_SANDBOX_MODE` override the file when the host calls `KudosAgentConfig.WithEnvOverrides`.

---

## Operations

`SafetyOperation(Kind, Path, Command, Detail)` is what the gate and the approval handler see.

| `Kind` | Raised by | Fields set | Gate decision |
|--------|-----------|------------|---------------|
| `read` | `read_file`, `view_image` | `Path` | Always `Auto` |
| `write` | `write_file`, `edit_file`, `apply_patch` (each path, including deletes and both paths of a move) | `Path` | Sandbox matrix + writable roots |
| `exec` | `bash_command` | `Command` (the command line) | Exec rules, then `execDefault`, then sandbox matrix |
| `exec` | `execute_code` | `Command = "execute_code(<language>)"`, `Detail` = full script | Same as above, but `Auto` is raised to `Ask` |
| `mcp` | `mcp_<server>_<tool>` | `Command` = namespaced tool name | Not sent to the gate: `Ask` under `on-request`, `Auto` under `never` |
| `a2a` | `a2a_<name>` ([A2A.md](A2A.md)) | `Command` = `<name> (<url>)`, `Detail` = the full request text that would be sent | Not sent to the gate: `Ask` under `on-request`, `Auto` under `never` |

`glob_files`, `grep_search`, the TODO tools, `load_skill`, and `compress_context` do not go
through the gate. The search tools are still confined to the work directory.

`Detail` is display-only. Policy never evaluates it, so a multi-line script can be shown to the
approver without tripping the newline guard on `Command`.

---

## Approval Policies and Sandbox Modes

The matrix below is what `MatrixSafetyGate.Decide` returns. It is pinned by
`MatrixGateTests.cs`. "in roots"
means inside `writableRoots`.

| Operation | `read-only` | `workspace-write` | `danger-full-access` |
|-----------|-------------|-------------------|----------------------|
| read | Auto | Auto | Auto |
| write, in roots | Reject | Auto | Auto |
| write, outside roots | Reject | `on-request`: Ask / `never`: Reject | Auto |
| exec, no rule matched, no `execDefault` | `on-request`: Ask / `never`: Reject | `on-request`: Ask / `never`: **Auto** | Auto |
| exec, `deny` rule | Reject | Reject | Reject |
| exec, `ask` rule | `on-request`: Ask / `never`: Reject | same | same (never auto-approved) |
| exec, `allow` rule | Auto | Auto | Auto |

`execDefault` (when set) replaces the "no rule matched" row in every mode:

| `execDefault` | `on-request` | `never` |
|---------------|--------------|---------|
| `allow` | Auto | Auto |
| `ask` | Ask | Reject |
| `deny` | Reject | Reject |

Notes:

- Every `Auto` exec decision still goes through the [argument-scope checks](#argument-scope-checks).
  A violation turns it into `Reject`.
- Under `workspace-write` + `never`, **any command not covered by a rule runs automatically.**
  Combine `never` with `execDefault: "deny"` or `"ask"` unless that is what you want.
- `execute_code` raises `Auto` to `Ask` in every mode, so under `never` it still needs an
  approval handler that says yes. Without one it is always refused. A `deny` rule or
  `execDefault: "deny"` refuses it outright.

---

## Exec Rules

Matching is implemented in `ExecPolicy.cs`:

1. **Shell-substitution guard first.** A command containing `$(`, a backtick, `<(`, `>(`, a
   background `&`, or a newline is rejected before any rule is consulted (`&&` and `2>&1` are
   fine). No rule can override this.
2. **Split into segments** on `&&`, `||`, `|`, and `;`, respecting single and double quotes.
3. **Tokenize each segment** on spaces and tabs, removing quotes (`git commit -m "a b"` becomes
   `git`, `commit`, `-m`, `a b`).
4. **Match each segment** against every rule. A rule matches when its `prefix` tokens equal the
   segment's first tokens **exactly**: case-sensitive, no globbing, no regex. Within a segment,
   `deny` beats `ask` and `ask` beats `allow`.
5. **Combine segments**:

| Segment results | Command result |
|-----------------|----------------|
| Any segment `deny` | `deny` (Reject) |
| Otherwise any segment `ask` | `ask` |
| Every segment `allow` | `allow` (Auto, subject to argument scope) |
| Otherwise | no match (falls through to `execDefault`, then the sandbox matrix) |

Examples, with `[git status]=allow`, `[git push]=ask`, `[rm]=deny`:

| Command | Result | Why |
|---------|--------|-----|
| `git status` | allow | Prefix match |
| `git status && git push origin main` | ask | One segment is `ask` |
| `git status \| rm -rf x` | deny | One segment is `deny` |
| `git status; ls` | no match | `ls` matches no rule |
| `git  status --short` | allow | Extra whitespace is ignored and extra trailing tokens are fine |
| `/bin/rm -rf x` | no match | `/bin/rm` is a different token from `rm` |
| `sudo rm -rf x` | no match | The prefix is anchored at the first token |

Because prefixes match literal tokens, a **deny list is easy to get around** (`/bin/rm`,
`env rm`, `FOO=1 rm`). For a strong guarantee, use an allow list: `execDefault: "deny"` (or
`"ask"`) plus `allow` rules for the commands you trust.

A host tool can raise `SafetyOperation("exec", Command: "...")` to take part in the same rules
(see [TOOLS.md](TOOLS.md#writing-a-tool)).

---

## Argument-Scope Checks

Prefix rules only look at the command name. Before any exec is auto-approved,
`ArgScope.cs` also checks the **arguments**. A
violation is always a `Reject`, whatever the mode or rules. Commands on the `Ask` path are not
argument-checked: the approver sees the full command instead.

| Check | Commands | Rule |
|-------|----------|------|
| Write paths | `rm`, `mv`, `cp`, `mkdir`, `ln`, `chmod` (paths after the mode), `touch`, `tee`, `install`, `sed -i` | Every non-flag argument must resolve inside `writableRoots` |
| Read paths | `cat`, `head`, `tail`, `wc`, `less`, `grep` / `rg` / `awk` (after the pattern), `sed` (files), `find` (start path) | No argument may resolve inside a sensitive root |
| `find` flags | `find` | `-exec`, `-execdir`, `-ok`, `-okdir`, `-delete` are rejected |
| Redirections | `> file`, `>> file` anywhere in the command | The target must resolve inside `writableRoots`. A quoted target is rejected as unclassifiable |

Paths are resolved against the work directory, with `~` expanded and symlinks resolved, so
`/var/...` versus `/private/var/...` on macOS is handled. The built-in sensitive roots
(`ArgScope.DefaultSensitiveReadRoots`) include `/etc/shadow`, `/etc/gshadow`, `/etc/sudoers`,
`/etc/sudoers.d`, `/root`, `$HOME/.ssh`, `$HOME/.aws`, `$HOME/.kube`, `$HOME/.docker`, and one more
tool-configuration directory under `$HOME`. Setting `execArgScope.sensitiveReadRoots` **replaces**
the whole list, so copy the defaults in if you want to extend them.

---

## Workspace Boundaries

There are two separate boundaries:

| Boundary | Enforced by | Applies to | Configurable |
|----------|-------------|------------|--------------|
| **Work directory** (`<resultsRoot>/<sessionId>/work`) | `Kudos.Agent.Tools.PathGuard` | `read_file`, `view_image`, `write_file`, `edit_file`, `apply_patch`, `glob_files`, `grep_search` | No. Applies in every sandbox mode, including `danger-full-access` |
| **Writable roots** (`safety.writableRoots`, default = work directory) | `MatrixSafetyGate` + argument scope | `write` decisions; exec write paths and redirections | Yes |

What this means in practice:

- File tools **never** read or write outside the work directory. If a write outside it is
  approved (`Ask`), the tool still fails with `Access denied: ... resolves outside workdir`.
  Extra `writableRoots` outside the work directory therefore only matter for shell commands that
  are argument-checked.
- To give the agent files, copy them into the work directory (the session exposes
  `session.Layout.WorkDir`), or let it read them with shell commands where policy allows.
- Shell commands and scripts (`bash_command`, `execute_code`) run with the work directory as
  their current directory, but they are **ordinary child processes with the host's privileges**.
  See [Limitations](#limitations).

---

## The Approval Handler Contract

The host provides the handler with `AgentSessionBuilder.UseApprovalHandler`, which sets
`ToolContext.ApprovalHandler`:

```csharp
Func<SafetyOperation, Task<bool>>
```

| Aspect | Contract |
|--------|----------|
| When it is called | Only when the decision is `Ask`: an `ask` exec rule, an uncovered command under `on-request`, a write outside roots under `on-request`, every `execute_code` that is not rejected, every MCP call under `on-request` |
| Input | The `SafetyOperation`: `Kind`, `Path` (writes), `Command` (exec / MCP), `Detail` (the full script for `execute_code`). The gate's reason is not passed to the handler, but it is recorded in the rollout |
| Return `true` | The operation runs |
| Return `false` | The tool returns `{"ok":false,"error":"<tool> rejected by user (approval denied): <reason>"}` to the model. The turn continues, and the model is told not to retry |
| No handler (`null`) | Every `Ask` becomes `approval required but no approval handler available (fail-closed): <reason>` |
| Sync / async | Asynchronous. Return `Task.FromResult(...)` for a synchronous policy, or await UI input |
| Concurrency | The SDK's tool loop runs tool calls one at a time, so approvals of one session arrive one at a time. With sub-agents running in parallel (`SubAgentPolicy.MaxConcurrent` > 1), approvals from different sub-agents can arrive at the same time — make the handler safe for concurrent calls (for example queue the dialogs) |
| Thread | Called on the tool-loop thread, usually not the UI thread. Marshal to your dispatcher yourself |
| Timeout | **None.** The tool call, and therefore the turn, waits until the task completes. Add your own timeout |
| Cancellation | The handler receives no `CancellationToken`. Watch your own turn token if you need to give up |
| Exceptions | Not caught by `SafetyCheck`. Catch inside the handler and return `false` |
| Sub-agents | Requests of sub-agents (`AgentSessionBuilder.AddSubAgent`) come to the same handler with `SafetyOperation.AgentPath` set (`reviewer`, `reviewer/checker`). A sub-agent cannot have its own handler, and its safety settings cannot be looser than the parent's (it may only narrow `sandboxMode`) |

A handler with a UI prompt, a timeout, and exception safety:

```csharp
Func<SafetyOperation, Task<bool>> approve = async operation =>
{
    try
    {
        var target = operation.Command ?? operation.Path ?? string.Empty;
        // Show operation.Detail (the full generated script for execute_code) in the dialog.
        var prompt = ShowApprovalDialogAsync(operation.Kind, target, operation.Detail); // your UI
        var winner = await Task.WhenAny(prompt, Task.Delay(TimeSpan.FromMinutes(2)));
        return winner == prompt && await prompt;   // timeout = deny
    }
    catch (Exception)
    {
        return false;                              // never let the handler throw
    }
};

var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(chatClient)
    .UseApprovalHandler(approve)
    .BuildAsync();
```

A headless policy handler for batch use, approving python scripts only. With `execDefault: "deny"`,
also add an `allow` rule for `["execute_code(python)"]`: the gate must not reject the operation
before it is raised to `Ask`.

```csharp
.UseApprovalHandler(op => Task.FromResult(
    op.Kind == "exec" && op.Command is "execute_code(python)"))
```

---

## Destructive Operations

The SDK has no separate "delete" or "destructive" operation kind. Destructive actions are gated
by how they happen:

| Action | Gated as | With default settings |
|--------|----------|-----------------------|
| Overwrite a file (`write_file`), edit, patch update | `write` | Auto inside the work directory |
| Delete a file via `apply_patch` (`*** Delete File:`) | `write` on that path | Auto inside the work directory |
| `rm`, `mv`, `chmod`, ... via `bash_command` | `exec` + argument scope | Ask (`on-request`). If auto-approved by a rule, targets must stay in writable roots |
| `find ... -delete` / `-exec` | Argument scope | Rejected on the auto path |
| Anything inside an `execute_code` script | `exec` (always ask) | The approver sees the full script |
| Writes outside the work directory by file tools | Work-directory boundary | Always refused |

Recommendations:

- Treat the work directory as scratch space the agent fully owns. Keep originals elsewhere and copy
  inputs in.
- Add `ask` or `deny` rules for commands with side effects outside the workspace (`["git","push"]`,
  `["git","reset"]`, `["rm"]`, package managers, deployment CLIs), and remember that literal-prefix
  matching can be bypassed. See [Exec rules](#exec-rules).
- Rely on the approval prompt for `execute_code`: it is the one place where arbitrary code is
  reviewed. Show `Detail` in full.

---

## How the Safety Settings Reach the Prompt

The model is told about the active policy, so it plans around it instead of discovering denials
by trial and error. `SystemPromptBuilder.Build` assembles the system prompt in this order, joined by
`---` separators:

1. `base-prompt.md`
2. `permissions/sandbox_mode/<sandboxMode>.md`
3. `permissions/approval_policy/<approvalPolicy>.md`
4. AGENTS.md files found from the work directory up to the filesystem root
5. the skills index ([SKILLS-GUIDE.md](SKILLS-GUIDE.md))
6. environment context (work directory, platform)

| Setting | Fragment | What it tells the model |
|---------|----------|-------------------------|
| `sandboxMode: read-only` | `read-only.md` | `write_file`, `edit_file`, `apply_patch` are rejected for every path |
| `sandboxMode: workspace-write` | `workspace-write.md` | Writes inside writable roots are fine, writes outside are escalated (`on-request`) or rejected (`never`) |
| `sandboxMode: danger-full-access` | `danger-full-access.md` | No filesystem sandboxing |
| `approvalPolicy: on-request` | `on-request.md` | Segment-wise rule evaluation. Uncovered commands are escalated, and in headless runs they are rejected and must not be retried |
| `approvalPolicy: never` | `never.md` | Uncovered segments run automatically under `workspace-write` and are rejected under `read-only`. Writes outside roots are rejected |

The base prompt (`base-prompt.md`) also changes behaviour by policy.
Under `never` the model is told to run tests and checks proactively. Under `on-request` it is told
to hold off until the user asks. Its *Tool Guidelines* describe exec rules, the approval of
`execute_code`, and MCP approval.

The prompts are embedded in the SDK. `AgentSessionBuilder.UsePromptsDirectory(dir)` loads them
from a directory instead (`base-prompt.md` plus `permissions/...`). A missing permission fragment is
silently left out, and a missing `base-prompt.md` throws `FileNotFoundException`. If you customise
the prompts, keep the fragments consistent with the configured policy. The prompt is fixed for the
session ([KV-CACHE.md](KV-CACHE.md)).

---

## Custom Safety Gates

`ToolContext.SafetyGate` is settable. Wrap the default gate to add product rules:

```csharp
using Kudos.Agent.Safety;

public sealed class ProductSafetyGate(ISafetyGate inner) : ISafetyGate
{
    public async Task<SafetyDecision> CheckAsync(SafetyOperation operation, CancellationToken cancellationToken = default)
    {
        // Example: never touch the product's own project files, whatever the config says.
        if (operation.Kind == "write" && operation.Path?.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new SafetyDecision(SafetyEffect.Reject, "project files are read-only");
        }

        return await inner.CheckAsync(operation, cancellationToken);
    }
}

var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(chatClient)
    .UseApprovalHandler(approve)
    .ConfigureToolContext(ctx =>
        ctx.SafetyGate = new ProductSafetyGate(new MatrixSafetyGate(ctx.Config.Safety, ctx.WorkDir)))
    .BuildAsync();
```

- `execute_code` still raises `Auto` to `Ask` with a custom gate.
- MCP tools never reach the gate (their decision comes from `approvalPolicy`).
- `MatrixSafetyGate.Decide(operation)` gives the pure policy decision, which is useful for tests.
- `AutoApproveSafetyGate` approves everything. It exists for tests and must not be used in
  production.

---

## Recommended Settings for Embedding Hosts

| Host scenario | `approvalPolicy` | `sandboxMode` | `execDefault` | Rules and handler |
|---------------|------------------|---------------|---------------|-------------------|
| Interactive desktop plugin (default) | `on-request` | `workspace-write` | `null` | `allow` for read-only inspection commands (`["ls"]`, `["git","status"]`, `["git","diff"]`), `ask`/`deny` for side effects. Handler = dialog showing `Detail` |
| Headless / batch service | `on-request` | `workspace-write` | `deny` | Explicit `allow` list. To permit scripts, add `{"prefix": ["execute_code(python)"], "effect": "allow"}`, since `execDefault: deny` would otherwise refuse `execute_code`. Handler = deterministic policy (e.g. approve `execute_code(python)` only), or no handler for no scripts |
| Read-only analysis | `on-request` | `read-only` | `ask` | Handler reviews every command and `execute_code`. Scripts can still write via the interpreter, so review them |
| Disposable VM / container only | `never` | `danger-full-access` | — | Only where the whole machine is expendable |

General advice:

- **Always set an approval handler** in interactive hosts. Without one, every `Ask` fails closed,
  which includes every `execute_code` call (the core use case).
- **Prefer `on-request`.** `never` + `workspace-write` auto-runs every uncovered command.
- **Keep `execArgScope` enabled**, and add product-specific secrets to `sensitiveReadRoots`
  together with the defaults.
- **Do not widen `writableRoots`** to user folders. Copy inputs into the work directory and
  results out of it.
- Load the config with `KudosAgentConfig.Load` and call `WithEnvOverrides` only if you want
  operators to switch policy through `SAFETY_*` environment variables.
- Use the rollout (`safety-decision` / `human-intervention` events) for auditing. See
  [SESSIONS.md](SESSIONS.md).

---

## Limitations

- **No OS sandbox.** `sandboxMode` is a policy applied to tool calls, not process isolation.
  Once a command or script is approved, it runs as a normal child process with the host's user
  rights and network access. A python script can write anywhere the user can.
- Exec rules match literal leading tokens, and argument-scope checks recognise a fixed list of
  command names by their first token. Path-qualified or wrapped invocations (`/bin/rm`,
  `env rm`, `xargs rm`) are not recognised. Use an allow list where it matters.
- Argument-scope checks only run on the auto-approve path. Approved commands are trusted as the
  user saw them.

---

## See Also

- [TOOLS.md](TOOLS.md) — what each tool asks the safety gate
- [CONFIGURATION.md](CONFIGURATION.md#1-safety) — the `safety` section
- [API.md](API.md#kudosagentsafety) — the `Kudos.Agent.Safety` types
- [ARCHITECTURE.md](ARCHITECTURE.md) — where the safety gate sits
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md#tools-mcp-and-approvals) — approval problems
