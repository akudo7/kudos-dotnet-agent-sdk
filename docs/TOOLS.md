# Tools

This guide covers the tools the agent can call: the built-in set, how a host application adds,
replaces, or removes tools, how MCP servers plug in, and how tool results and errors get back to
the model.

## Contents

1. [Overview](#overview)
2. [Result Envelope](#result-envelope)
3. [Built-In Tools Reference](#built-in-tools-reference)
4. [Limits and Tool-Context Settings](#limits-and-tool-context-settings)
5. [Adding, Replacing, and Removing Tools](#adding-replacing-and-removing-tools)
6. [MCP Integration](#mcp-integration)
7. [A2A Agents as Tools](#a2a-agents-as-tools)
8. [How Tool Results and Errors Reach the Model](#how-tool-results-and-errors-reach-the-model)

---

## Overview

When a session is created, `AgentSessionBuilder.BuildAsync` builds the tool list once, and it stays
fixed for the whole session:

1. **Default tools**: `ToolRegistry.CreateDefault(ToolContext)` registers the built-in tools in a
   fixed order. Tools named in `ToolContext.RemovedTools` are skipped.
2. **Host tools**: each factory in `ToolContext.AdditionalTools` (filled by
   `AgentSessionBuilder.AddTool` / `AddTools`) is called with the session's `ToolContext` and
   registered after the defaults.
   Sub-agent tools (`agent_<name>` from `AddSubAgent` / `subAgents[]`, then `spawn_agent` when
   `subAgentPolicy.allowSpawn` is on) are registered here too.
3. **MCP tools**: tools from the configured MCP servers (`mcp_<server>_<tool>`) are added last.

Every tool runs inside the session work directory (`<resultsRoot>/<sessionId>/work`, see
[SESSIONS.md](SESSIONS.md)).

### Built-in tools at a glance

Approval column: what happens with the default safety settings (`approvalPolicy: on-request`,
`sandboxMode: workspace-write`). See [SAFETY.md](SAFETY.md) for the full matrix.

| # | Tool | Purpose | Safety check | Default approval |
|---|------|---------|--------------|------------------|
| 1 | `execute_code` | Write a bash / python script to `.exec/` and run it (the primary tool) | `exec`, **always escalated to ask** | Always asks |
| 2 | `bash_command` | Run one shell command | `exec` (exec rules + sandbox matrix) | Asks unless an exec rule allows it |
| 3 | `read_file` | Read a text file with line numbers, paged | `read` | Never asks |
| 4 | `write_file` | Create or overwrite a file | `write` | Auto inside writable roots |
| 5 | `edit_file` | Replace one exact string, or all occurrences | `write` | Auto inside writable roots |
| 6 | `apply_patch` | Atomic multi-file patch (add / update / delete / move) | `write` for every path touched | Auto inside writable roots |
| 7 | `glob_files` | Find files by glob pattern (uses `rg`) | none (work-directory boundary only) | Never asks |
| 8 | `grep_search` | Regex search of file contents (uses `rg`) | none (work-directory boundary only) | Never asks |
| 9 | `write_todos` | Replace the session TODO list | none | Never asks |
| 10 | `read_todos` | Read the TODO list, filtered if needed | none | Never asks |
| 11 | `load_skill` | Load a skill body or reference file | none | Never asks |
| 12 | `compress_context` | Ask for context compaction before the next model call | none | Never asks |
| 13 | `view_image` | Show an image file of the work directory to the model (PNG / JPEG / WebP / GIF) | `read` | Never asks |
| — | `mcp_<server>_<tool>` | Tools from configured MCP servers | approval policy only | Always asks under `on-request` |
| — | `agent_<name>` | Delegate a `query` to a registered sub-agent ([API.md](API.md#sub-agents)) | none itself — the sub-agent's tools are checked, and their approvals reach your handler with `AgentPath` | Never asks itself |
| — | `spawn_agent` | Start a one-shot sub-agent from model-written `instructions` + `query` (`subAgentPolicy.allowSpawn`, default off) | same as `agent_<name>` | Never asks itself |

`compress_context` is registered only when `contextCompression.mode` is not `backstop`.
`view_image` is registered only when the primary model takes images (`models[].config.inputModalities`, or `config.mmproj`
for the in-process model — see [ATTACHMENTS.md](ATTACHMENTS.md)), so a text-only model keeps the 12-tool list.
The source is in `../src/Kudos.Agent.Sdk/Tools/`.

### External requirements

| Tool | Needs |
|------|-------|
| `glob_files`, `grep_search` | `rg` (ripgrep) on `PATH` |
| `execute_code` (python) | `python3` on `PATH` |
| `execute_code` (bash), `bash_command` | The shell in `ToolContext.Shell` (default `ShellSpec.Bash` = `/bin/bash -c <command>`) |

---

## Result Envelope

Every built-in tool returns a JSON string built by `ToolResult.Ok` / `ToolResult.Fail`
(`ToolResult.cs`):

```json
{"ok": true,  "exitCode": 0, "output": "...", "truncated": true, "count": 12}
{"ok": false, "error": "command exited with code 2", "exitCode": 2, "output": "..."}
```

| Field | Present when |
|-------|--------------|
| `ok` | Always |
| `output` | The tool has text output |
| `exitCode` | Process tools (`bash_command`, `execute_code`) |
| `truncated` | Only when `true` (output was capped or paged) |
| `count` | Search and list tools (`glob_files`, `grep_search`, `read_todos`) |
| `error` | Only on `ok: false` |

The tools report failures as `ok: false` results rather than throwing exceptions, so the model can
read the error and try again (see
[How tool results and errors reach the model](#how-tool-results-and-errors-reach-the-model)).

---

## Built-In Tools Reference

Paths are always relative to the session work directory. A path that resolves outside it is
refused with `Access denied: "<path>" resolves outside workdir "<workdir>"`
(`Tools/PathGuard.cs`). This applies to every
file and search tool, whatever the sandbox mode.

### `execute_code`

The primary tool for computation and file processing. It writes the script to
`.exec/NNNN.sh` or `.exec/NNNN.py` in the work directory, so the model can read it again later,
then runs it with the work directory as the current directory.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `language` | string | yes | `bash` / `sh` / `shell` or `python` / `python3` / `py` (case-insensitive). Anything else fails |
| `code` | string | yes | Script body |
| `timeout_ms` | int | no | Default `ToolContext.DefaultTimeoutMs` (120000), clamped to `1..MaxTimeoutMs` (600000) |

- Interpreter: `python3 <script>` for python; `ToolContext.Shell.FileName <script>` for bash.
- Safety: the gate sees `SafetyOperation("exec", Command: "execute_code(<language>)", Detail: <code>)`.
  If the gate says **auto**, the decision is raised to **ask**, so generated code never runs
  without approval. A **reject** still rejects. The full code is in `Detail` for the approval
  UI.
- Result: stdout, then stderr, each capped at `ExecOutputBytes` (100 KB) with a
  `[truncated: output capped at N bytes]` marker. Exit code 0 gives `ok: true`. Any other exit
  code gives `ok: false` with `exitCode` and `output`.
- Timeout: the whole process tree is killed and the tool returns `script timed out after Nms (killed)`.

### `bash_command`

Runs one command through `ToolContext.Shell` (default `/bin/bash -c <command>`), with the work
directory as the current directory.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `command` | string | yes | Command line |
| `description` | string | no | Free text for the model's own use (not used by the tool) |
| `timeout_ms` | int | no | Same default and clamp as `execute_code` |

- Commands containing shell-substitution metacharacters are rejected before the safety gate runs:
  `$(`, a backtick, `<(`, `>(`, a background `&`, or a newline (`&&` and `>&` are allowed).
- Safety: `SafetyOperation("exec", Command: command)`, decided by exec rules, `execDefault`, and
  the sandbox matrix ([SAFETY.md](SAFETY.md#exec-rules)).
- Output and timeout handling work as for `execute_code`. Empty output is reported as `(no output)`.

### `read_file`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `file_path` | string | yes | File to read |
| `offset` | int | no | 0-based first line (default 0) |
| `limit` | int | no | Number of lines (default `ReadFileTool.ReadMaxLines` = 2000) |

Output lines look like `12→code`. When lines remain after the window, `truncated: true` is set
and the output ends with `[N more lines - use offset/limit to page]`. There is no byte cap, so a
file with very long lines is returned in full.

### `write_file`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `file_path` | string | yes | Target file. Parent directories are created |
| `content` | string | yes | Full file content. An existing file is overwritten |

Returns `Wrote file: <path> (<bytes> bytes)`.

### `edit_file`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `file_path` | string | yes | Target file (must exist) |
| `old_string` | string | yes | Exact text to replace (without `read_file` line-number prefixes) |
| `new_string` | string | yes | Replacement, which must differ from `old_string` |
| `replace_all` | bool | no | Replace every occurrence (default `false`) |

Without `replace_all`, `old_string` must occur exactly once. Otherwise the call fails with
`old_string was found in N locations...` or `old_string not found`.

### `apply_patch`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `patch` | string | yes | Patch text in the format below |

```text
*** Begin Patch
*** Update File: src/app.ts
@@ optional context label
 context line (starts with one space)
-removed line
+added line
*** Add File: notes.txt
+every content line starts with +
*** Delete File: old.txt
*** Move File: old/path => new/path
*** End Patch
```

- **Atomic**: every change is validated and computed first, then committed. If the commit fails,
  the changes are rolled back (`Patch commit failed, rolled back: ...`).
- Update hunks are located by their context lines, not by line numbers.
- `Add File` fails if the file exists, `Delete File` and `Update File` fail if it does not, and
  `Move File` fails if the destination exists.
- A file may appear in only one section, and nothing may follow `*** End Patch`.
- Safety: every path gets its own `write` check (both paths of a move). **Deletions are gated as
  writes**: there is no separate delete operation.

### `glob_files`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pattern` | string | yes | Glob, e.g. `**/*.cs` |
| `search_path` | string | no | Directory to search (default: work directory) |

Runs `rg --files -g <pattern>`. Results are sorted by modification time (newest first) and capped
at `GlobFilesTool.MaxResults` = 200. `count` is the total number of matches. When the cap is hit,
`truncated: true` is set and a `[... N results omitted ...]` line is added. Directories in
`ToolContext.ExcludeDirs` (default `node_modules`, `.git`, `dist`) are skipped. The search times out
after `DefaultTimeoutMs`.

### `grep_search`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pattern` | string | yes | Rust regex (ripgrep syntax) |
| `search_path` | string | no | File or directory (default: work directory) |
| `glob_pattern` | string | no | File filter, e.g. `*.cs` |
| `case_insensitive` | bool | no | Adds `-i` |

Output lines are `path:line:content`, and no matches gives `No matches` with `count: 0`. There are
two limits:

| Limit | Default | When exceeded |
|-------|---------|---------------|
| `ToolContext.GrepCaptureBytes` | 16 MB | The search stops and fails: `grep output exceeded N bytes — narrow the pattern or search_path` |
| `ToolContext.GrepReturnBytes` | 100 KB | Output is truncated, with `truncated: true` and a marker |

### `write_todos` / `read_todos`

| Tool | Parameter | Type | Description |
|------|-----------|------|-------------|
| `write_todos` | `todos` | array of `{content, status?, priority?}` | Replaces the whole list. `status`: `pending` (default) / `in_progress` / `completed`. `priority`: `high` / `medium` (default) / `low` |
| `read_todos` | `status` | enum, optional | Filter |
| `read_todos` | `priority` | enum, optional | Filter |

The list lives in `ToolContext.TodoStore` (default `InMemoryTodoStore`, scoped to the session). A host
can supply its own `ITodoStore` through `ConfigureToolContext`.

### `load_skill`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | yes | Skill name, `[A-Za-z0-9._-]+` |
| `path` | string | no | Reference file inside the skill directory |

Returns the SKILL.md body without its frontmatter, or the reference file. See
[SKILLS-GUIDE.md](SKILLS-GUIDE.md#the-load_skill-tool).

### `view_image`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `path` | string | yes | Image file, relative to the work directory |

Checks the path like `read_file` (safety operation `read`, work-directory boundary), then the content:
at most `attachments.maxFileMb`, and an image by its leading bytes (PNG, JPEG, WebP or GIF — a PDF or a
text file is refused with `not an image`). The image is copied to `attachments/` (the session record refers
to the copy) and returned to the model with a text line:
`{"ok":true,"output":"out/chart.png (image/png, 640x480, 12,345 bytes) — saved as attachments/chart.png"}`.
Registered only for a model that takes images. See [ATTACHMENTS.md](ATTACHMENTS.md).

### `compress_context`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `reason` | string | no | Echoed in the result |

Sets a pending flag. The actual compaction is applied before the next model call, and the turn
continues. If the context was compacted just before this call, it returns a "no further compaction
needed" message. When `contextCompression.enabled` is `false`, the tool is still registered (unless
the mode is `backstop`) but returns `ok: false`. See [CONFIGURATION.md](CONFIGURATION.md) and
[SESSIONS.md](SESSIONS.md).

---

## Limits and Tool-Context Settings

These are properties of `ToolContext`. Change them with `AgentSessionBuilder.ConfigureToolContext`.

| Property | Default | Used by |
|----------|---------|---------|
| `ExecOutputBytes` | `102400` (100 KB, applied separately to stdout and stderr) | `bash_command`, `execute_code` |
| `DefaultTimeoutMs` | `120000` | `bash_command`, `execute_code` (default timeout); `glob_files`, `grep_search` (fixed timeout) |
| `MaxTimeoutMs` | `600000` | Upper clamp for `timeout_ms` |
| `GrepCaptureBytes` | `16777216` (16 MB) | `grep_search` |
| `GrepReturnBytes` | `102400` (100 KB) | `grep_search` |
| `ExcludeDirs` | `node_modules`, `.git`, `dist` | `glob_files`, `grep_search` |
| `Shell` | `ShellSpec.Bash` | `bash_command`, `execute_code` (bash) |
| `TodoStore` | `InMemoryTodoStore` | `write_todos`, `read_todos` |
| `SkillsPath` | empty, meaning `config.skills.skillsPath` resolved against the process current directory | `load_skill` |
| `SafetyGate` | `MatrixSafetyGate(Config.Safety, WorkDir)`, created lazily | All gated tools ([SAFETY.md](SAFETY.md)) |
| `ApprovalHandler` | Set by `UseApprovalHandler` | All gated tools |

```csharp
var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(chatClient)
    .ConfigureToolContext(ctx =>
    {
        ctx.ExecOutputBytes = 32 * 1024;
        ctx.DefaultTimeoutMs = 60_000;
        ctx.ExcludeDirs = ["node_modules", ".git", "dist", "bin", "obj"];
    })
    .BuildAsync();
```

---

## Adding, Replacing, and Removing Tools

### Builder API

| `AgentSessionBuilder` method | Effect |
|------------------------------|--------|
| `AddTool(Func<ToolContext, ITool> factory)` | Registers a host tool built from the session's `ToolContext`. Use this form when the tool needs the work directory, the safety gate, or the approval handler |
| `AddTools(IEnumerable<ITool> tools)` | Registers ready-made tool instances |
| `RemoveTool(string name)` | Leaves a default tool out, by name |
| `ConfigureToolContext(Action<ToolContext>)` | Final adjustments to the context before the session is created |

Rules (`ToolRegistry.cs`):

- Host tools are registered **after** the defaults, in the order they were added.
- Names must be unique. A duplicate throws `InvalidOperationException("Duplicate tool name: ...")`
  while `BuildAsync` runs. To **replace** a built-in tool, call `RemoveTool(name)` first, then
  add yours under the same name.
- `RemoveTool` only affects the default tools (including `compress_context`).
- The tool list is part of the prompt and is fixed for the session. Changing it affects new
  sessions only (see [KV-CACHE.md](KV-CACHE.md)).

### Writing a tool

A tool implements `ITool`: a unique `Name` and an `AIFunction` that holds the description, the JSON
schema, and the delegate. Building blocks from the public API:

| Type | Use |
|------|-----|
| `ToolFunction.Create(Delegate, name, description)` | Wraps `AIFunctionFactory.Create`. It also turns non-string JSON arguments into strings for `string` parameters, so a model that sends `{"content": 10}` does not fail |
| `ToolFunction.CreateWithContent(Delegate, name, description)` | Same, for a tool that returns images: an `AIContent` / `IEnumerable<AIContent>` result is passed on as is (`AIFunctionFactory` would serialize it to JSON). See [Returning images](#returning-images) |
| `SafetyCheck.CheckAsync(ToolContext, SafetyOperation, CancellationToken)` | Runs the gate, then the approval handler if needed, and records the decision. Returns `null` when allowed, or the denial text |
| `SafetyCheck.CheckAsync(ToolContext, SafetyOperation, Task<SafetyDecision>, CancellationToken)` | Same, for a decision you computed yourself (e.g. "always ask") |
| `ToolResult.Ok(...)` / `ToolResult.Fail(...)` | The standard result envelope |
| `PathGuard.Resolve(ToolContext, path)` | Resolves a path against the work directory and throws `InvalidOperationException` if it leaves it |

With the default `MatrixSafetyGate`, `SafetyOperation.Kind` must be `"read"`, `"write"`, or `"exec"`.
Any other kind makes the gate throw `ArgumentException`. For an `"exec"` operation, `Command` is
matched against `safety.execRules`, so a host tool can be allow-listed or denied in config like
any shell command ([SAFETY.md](SAFETY.md#exec-rules)).

```csharp
using Kudos.Agent.Safety;
using Kudos.Agent.Tools;
using Microsoft.Extensions.AI;

/// Example host tool: runs a product command with approval.
public sealed class RunMacroTool : ITool
{
    private readonly ToolContext _context;

    public RunMacroTool(ToolContext context)
    {
        _context = context;
        Function = ToolFunction.Create(
            ExecuteAsync,
            name: "run_macro",
            description: "Run a named macro in the host application and return its output. " +
                         "Use it only for macros listed by list_macros.");
    }

    public string Name => "run_macro";

    public AIFunction Function { get; }

    public async Task<string> ExecuteAsync(string macro, CancellationToken cancellationToken = default)
    {
        // Goes through exec rules / execDefault / the sandbox matrix, then the approval handler.
        var denied = await SafetyCheck.CheckAsync(
            _context,
            new SafetyOperation("exec", Command: $"run_macro {macro}"),
            cancellationToken);
        if (denied is not null)
        {
            return ToolResult.Fail($"run_macro {denied}");
        }

        try
        {
            var output = await MacroHost.RunAsync(macro, cancellationToken); // your product API
            return ToolResult.Ok(output: output);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ex.Message); // let the model self-correct
        }
    }
}
```

Registering it, replacing `execute_code`, and dropping the TODO tools:

```csharp
var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(chatClient)
    .UseApprovalHandler(approve)
    .RemoveTool(ExecuteCodeTool.FunctionName)   // "execute_code"
    .RemoveTool(WriteTodosTool.FunctionName)
    .RemoveTool(ReadTodosTool.FunctionName)
    .AddTool(ctx => new RunMacroTool(ctx))
    .AddTool(ctx => new MyExecuteCodeTool(ctx)) // same name as the removed default
    .BuildAsync();
```

To make a tool **always** ask, as `execute_code` does, compute the decision yourself:

```csharp
var operation = new SafetyOperation("exec", Command: "publish_model", Detail: summary);
var decision = await _context.SafetyGate.CheckAsync(operation, cancellationToken);
if (decision.Effect == SafetyEffect.Auto)
{
    decision = new SafetyDecision(SafetyEffect.Ask, "publish_model always asks");
}

var denied = await SafetyCheck.CheckAsync(_context, operation, Task.FromResult(decision), cancellationToken);
```

Tips:

- **The description is the prompt.** Say what the tool does, when to use it instead of a
  neighbouring tool, and what limits apply. The built-in descriptions are good models.
- Return `ToolResult.Fail` for expected failures instead of throwing.
- Cap large outputs (`ProcessRunner.Truncate(text, maxBytes)` adds the standard marker).
- Put long content in `SafetyOperation.Detail` (shown to the approver, never evaluated by policy).
  Keep `Command` to a single line: newlines are rejected by the shell-substitution guard.

---

## MCP Integration

Stdio MCP servers are configured in the `mcpServers` section of `kudos-agent.json` and connected
when the session is created (`McpClientManager.cs`,
official `ModelContextProtocol` C# SDK).

```json
{
  "mcpServers": [
    {
      "name": "example",
      "command": "npx",
      "args": ["-y", "@example/mcp-server"],
      "env": { "TOKEN": "${MY_TOKEN}" },
      "enabled": true
    }
  ]
}
```

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `name` | string | required | Must match `[A-Za-z0-9._-]+` and be unique (case-insensitive). Used in tool names |
| `command` | string | required | Executable to launch (stdio transport) |
| `args` | string[] | `[]` | Arguments |
| `env` | object | `{}` | Extra environment variables. The server also inherits the host process environment. `${VAR}` references are expanded by `KudosAgentConfig.WithEnvOverrides`, and a missing variable expands to an empty string |
| `enabled` | bool | `true` | `false` keeps the entry but does not start the server |

Behaviour:

| Aspect | Behaviour |
|--------|-----------|
| Launch | Each enabled server is started as a child process with the session work directory as its working directory. Its stderr lines are sent to the session logger (`UseLogger`) as warnings, prefixed `[mcp:<name>] stderr:` |
| Naming | Each tool the server lists is exposed as `mcp_<server>_<tool>`, with the server's JSON schema unchanged |
| Global switch | `MCP_ENABLED=0` / `false` / `off` / `no` in the environment passed to `WithEnvOverrides` starts no server at all. The entries stay in the file |
| Startup failure | A server that fails to start (I/O error, cancellation, or `InvalidOperationException`, e.g. the command does not exist) is skipped with the warning `[mcp:<name>] startup failed (...) — server skipped`. The session runs without its tools |
| Approval | MCP tools bypass the exec matrix. `approvalPolicy: on-request` always asks the approval handler (`SafetyOperation.Kind = "mcp"`, `Command = "mcp_<server>_<tool>"`), and with no handler the call is refused. `never` runs them automatically |
| Errors | A result with `isError` becomes `ToolResult.Fail(<text>)`. An exception during the call becomes `ok: false` with `mcp tool call failed: <message>`. The session is never dropped |
| Result text | Text content blocks are joined with newlines. Other block types are converted to strings |
| Shutdown | Disposing the session disconnects every server |
| Shared connection | A host that creates many sessions can start the servers once: `McpClientManager.ConnectSharedAsync(servers, workingDirectory, onWarning)` and `AgentSessionBuilder.UseMcpConnection(connection)` per session. Each session gets its own `mcp_*` tools (`connection.ForContext(toolContext)`), so approvals and records stay per session; disposing a session leaves the servers running |

`MCP_ENABLED` and `${VAR}` expansion only take effect when the host calls
`config.WithEnvOverrides(env)`. `enabled: false` always applies. MCP results do not get the
context-usage suffix described below. The full `mcpServers` schema is in
[CONFIGURATION.md](CONFIGURATION.md).

---

## A2A Agents as Tools

Remote A2A agents configured in `a2a.agents` become `a2a_<name>` tools (argument `query`) when the host adds them with
`A2AToolSource.AddTo(builder)` from the `Kudos.Agent.Sdk.A2A` project. Approval uses `SafetyOperation.Kind = "a2a"` with the
full request in `Detail`; failures come back as `ok: false` results. See [A2A.md](A2A.md).

### Returning images

A tool can return images or PDFs as `DataContent` (for example a product tool that captures a view).
Return them from a `ToolFunction.CreateWithContent` delegate, alone or after a `TextContent`:

```csharp
Function = ToolFunction.CreateWithContent(
    async (CancellationToken ct) =>
    {
        byte[] png = await CaptureViewAsync(ct);
        return (object)new AIContent[] { new TextContent(ToolResult.Ok(output: "view captured")), new DataContent(png, "image/png") { Name = "view.png" } };
    },
    name: "capture_view",
    description: "Capture the current view as an image.");
```

The result of every built-in and host tool (MCP tools excepted) passes through the same step before it
reaches the model: each medium is
checked (size, type by content) and saved under `attachments/`, the result becomes
`[TextContent, DataContent…]`, and a medium the model cannot take is replaced by a sentence pointing at the
saved file (`[image: view.png 800x600 — attachments/view.png] (not shown — the model does not take this type)`).
A result without media is not changed. See [ATTACHMENTS.md](ATTACHMENTS.md).

---

## How Tool Results and Errors Reach the Model

1. **The tool returns a string** (the JSON envelope). Microsoft Agent Framework's tool loop adds it to
   the history as the tool result for that call.
2. **Context-usage suffix**: when context compression is enabled, every registered (non-MCP) tool
   is wrapped in `ContextUsageFunction`, which appends
   `[context ≈ N / M tokens (P%)]`, followed by a directive (`consider calling compress_context`,
   `call compress_context now`, or `automatic compaction will run before your next step`) once the
   context grows. With `contextCompression.mode = backstop`, the size is shown without directives.
3. **Denials are ordinary results.** A rejected or unapproved operation returns `ok: false` with
   the reason, for example `{"ok":false,"error":"command rejected by user (approval denied): ..."}`
   or `... approval required but no approval handler available (fail-closed): ...`. The turn
   continues, and the base prompt tells the model not to retry denied commands.
4. **Exceptions.** The built-in tools catch their expected failures. An exception that escapes a
   tool, for example a missing required argument when the delegate is bound, is handled by the
   `Microsoft.Extensions.AI` function-invocation loop, which reports an error result to the model.
   Repeated consecutive errors end the turn (`AgentTurnFailed`).
5. **Guards** run at the model-call boundary before tools execute
   ([CONFIGURATION.md](CONFIGURATION.md), `guard` section):
   - A tool call whose serialized arguments exceed `guard.maxToolArgsKb` (default 64 KB) never
     reaches the tool. The model gets one nudge to split the work, and a second oversized call ends the turn.
   - Repeat breakers detect identical repeated calls and results. They nudge the model or force a
     final answer.
   - `guard.maxToolCalls` (0 = off) caps the total number of tool calls per session.
6. **Tool-loop cap**: at most `CodingAgentFactory.MaxToolLoopIterations` (500) model calls per turn.
7. **Images in tool results** (`view_image`, host tools): Anthropic, Bedrock and Gemini receive them inside the
   tool result. The OpenAI Chat Completions format (OpenAI, Azure OpenAI, OpenAI-compatible servers) has no
   image in a tool message, so the tool result carries the text plus `… (shown in the next message)` and a user
   message right after the tool messages carries the images ([ATTACHMENTS.md](ATTACHMENTS.md#openai-format-tool-results)).
   The in-process model with its projector (`config.mmproj`) sees them inside the tool response, after each image's
   reference sentence ([MODELS.md](MODELS.md#images-and-pdfs)).

Hosts can watch tool activity through `AgentToolCallStarted` / `AgentToolCallCompleted` events from
`CodingAgentSession.RunTurnEventsAsync` (see [API.md](API.md)).

---

## See Also

- [SAFETY.md](SAFETY.md) — approval policies, sandbox modes, exec rules, approval handler
- [SKILLS-GUIDE.md](SKILLS-GUIDE.md) — skills and `load_skill`
- [CONFIGURATION.md](CONFIGURATION.md) — full `kudos-agent.json` schema
- [API.md](API.md) — `AgentSessionBuilder`, `ToolContext`, events
- [KV-CACHE.md](KV-CACHE.md) — why tool definitions are fixed per session
- [ATTACHMENTS.md](ATTACHMENTS.md) — images and PDFs, `view_image`
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md#tools-mcp-and-approvals) — tool, MCP and approval problems
