# Skills Guide

Skills are Markdown files that teach the agent procedures, conventions, and checklists for a kind
of task, without hard-coding them into the system prompt. This guide covers the file format,
discovery, how skills reach the model, and how to write good ones.

## Contents

1. [Introduction](#introduction)
2. [Quick Start](#quick-start)
3. [The SKILL.md Format](#the-skillmd-format)
4. [Discovery and Configuration](#discovery-and-configuration)
5. [How Skills Reach the Model](#how-skills-reach-the-model)
6. [The `load_skill` Tool](#the-load_skill-tool)
7. [Enabling and Disabling Skills](#enabling-and-disabling-skills)
8. [Writing a Good Skill](#writing-a-good-skill)
9. [Examples from This Repository](#examples-from-this-repository)
10. [FAQ](#faq)

---

## Introduction

### What is a skill?

A skill is a `SKILL.md` file with a short YAML frontmatter (`name`, `description`) and a Markdown
body. Skills use **progressive disclosure**:

| Stage | What the model sees | Cost |
|-------|---------------------|------|
| Session start | One index line per skill: name + description | One short line per skill |
| On demand | The full body, after the model calls `load_skill(name)` | Only when the task needs it |
| Deeper | Reference files inside the skill directory, via `load_skill(name, path)` | Only when needed |

### Why use skills?

- **Domain knowledge without prompt bloat**: dozens of skills cost only their index lines until used.
- **Editable by non-developers**: plain Markdown, with no rebuild needed for new sessions.
- **Product specific**: a product plugin can ship its own skills directory (for example, how to
  report results, or which product API to call for a task).

---

## Quick Start

1. Create `skills/report-style/SKILL.md` next to the host's working directory:

   ```markdown
   ---
   name: report-style
   description: How to format analysis reports — read before writing any report or summary table
   ---
   # Report Style

   ## Steps

   1. Put the answer first, in one sentence.
   2. Show numbers in a Markdown table, numeric columns aligned.
   3. End with caveats (data not retrieved, assumptions).
   ```

2. Make sure skills are enabled (the default) in `kudos-agent.json`:

   ```json
   { "skills": { "enabled": true, "skillsPath": "skills" } }
   ```

3. Build a session as usual. The skill appears in the system prompt index, and the model calls
   `load_skill("report-style")` before writing a report.

---

## The SKILL.md Format

### Layout

Two forms are discovered in the skills root (`SkillLoader.cs`):

| Form | Path | Reference files |
|------|------|-----------------|
| Directory (recommended) | `<skillsRoot>/<name>/SKILL.md` | Yes: any file under `<skillsRoot>/<name>/` |
| Flat | `<skillsRoot>/<name>.md` | Not intended (the `path` argument resolves against the skills root) |

Only the top level of the skills root is scanned. Nested folders are not searched for further
skills.

```text
skills/
├── output-format/
│   └── SKILL.md
├── e2e-task-rules/
│   └── SKILL.md
└── model-export/
    ├── SKILL.md
    └── reference/
        └── checklist.md      ← load_skill("model-export", "reference/checklist.md")
```

### Frontmatter: what is actually parsed

The loader parses a small YAML subset, not full YAML:

| Rule | Detail |
|------|--------|
| Delimiters | The file must start with a `---` line and the frontmatter must be closed by another `---` line |
| Keys read | **Only `name` and `description`.** Any other key (`version`, `tags`, `allowed-tools`, ...) is ignored |
| Line format | `key: value` on one line. The value is everything after the first `:`, trimmed. One pair of surrounding `"` or `'` quotes is removed |
| Not supported | Multi-line values (`>`, `|`, indented continuation), lists, nested maps |
| Comments | Blank lines and lines starting with `#` are skipped |
| Required | Both `name` and `description` must be non-empty |

A skill that fails these rules is **skipped with a warning**, which goes to the logger passed to
`UseLogger` or `LoadSkillsIndex` and is prefixed `[skills]`. It never breaks the session:

| Problem | Warning text |
|---------|--------------|
| No leading `---` | `missing YAML frontmatter (file must start with '---')` |
| No closing `---` | `unclosed YAML frontmatter (no closing '---')` |
| No `name` | `frontmatter has no 'name'` |
| No `description` | `frontmatter has no 'description'` |
| Read error | `skill '<path>' unreadable: ...` |

> **Keep `name` equal to the directory (or file) name.** The index shows the frontmatter `name`,
> but `load_skill` finds files by **directory or file name**. If they differ, the model gets
> `skill '<name>' not found` when it follows the index.

### Body

Everything after the closing `---` is the body. `load_skill` returns it with the frontmatter
stripped. Write it in plain Markdown: headings, numbered steps, and tables all work well.

---

## Discovery and Configuration

### Config keys

| Key | Type | Default | Meaning |
|-----|------|---------|---------|
| `skills.enabled` | bool | `true` | Build the skills index for the system prompt |
| `skills.skillsPath` | string | `"skills"` | Skills root. A relative path is resolved against the **process current directory** |

See [CONFIGURATION.md](CONFIGURATION.md) for the full schema.

### Where the index and `load_skill` look

The index and `load_skill` resolve the skills root separately:

| Consumer | Root, in priority order |
|----------|-------------------------|
| Skills index (system prompt) | 1. `UseSkillsIndex(text)` (pre-rendered) → 2. `UseSkillsDirectory(dir)` → 3. `config.skills.skillsPath` resolved against the current directory |
| `load_skill` tool | 1. `ToolContext.SkillsPath` → 2. `config.skills.skillsPath` resolved against the current directory |

`UseSkillsDirectory` does **not** change where `load_skill` reads from. When a host keeps its skills
in a custom place (for example, next to a plugin DLL), set both:

```csharp
var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");

var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(chatClient)
    .UseApprovalHandler(approve)
    .UseSkillsDirectory(skillsRoot)                        // index in the system prompt
    .ConfigureToolContext(ctx => ctx.SkillsPath = skillsRoot) // load_skill
    .BuildAsync();
```

### Long-lived hosts

A host that creates many sessions can scan once and reuse the rendered index:

```csharp
// once, at startup
string skillsIndex = AgentSessionBuilder.LoadSkillsIndex(config, skillsRoot, logger);

// per session
var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(chatClient)
    .UseSkillsIndex(skillsIndex)
    .ConfigureToolContext(ctx => ctx.SkillsPath = skillsRoot)
    .BuildAsync();
```

`LoadSkillsIndex` returns an empty string when `skills.enabled` is `false`. The lower-level API is
also public: `SkillLoader.LoadIndex(root, onWarning)` returns `List<SkillInfo>` (name,
description, path, directory), `SkillLoader.BuildIndexPrompt(skills)` renders it, and
`SkillLoader.ParseSkillFile(content)` validates a single file. These are useful for a skills
management UI or a CI check.

---

## How Skills Reach the Model

At session creation the index is rendered and placed in the system prompt, after the AGENTS.md
fragment and before the environment context (see [SAFETY.md](SAFETY.md#how-the-safety-settings-reach-the-prompt)
for the full order). Skills are sorted by name (case-insensitive). With the two skills in this
repository, the section reads:

```markdown
# Skills

Skills are domain knowledge packs (procedures, conventions, checklists). The list below shows name + description only. Before doing work a skill covers, call `load_skill` with the skill name to read its full instructions first — do not guess a skill's contents.

- `e2e-task-rules` — Procedure for aggregation, analysis, and file-change tasks (per-file line / TODO counts, log analysis, batch changes across several files such as config files) — read this skill before starting such a task
- `output-format` — Output rules for aggregation and change tasks — aggregation results as a Markdown table, change operations as a before/after comparison table, large results as a count + representative sample + a way to get the rest
```

With no valid skills, the whole section is left out. The base prompt's *Tool Guidelines* also have a
`load_skill` section telling the model to load a skill before doing work it covers.

The index is fixed for the session: adding or editing skills affects **new** sessions only. Bodies
are read from disk each time `load_skill` runs. Keeping the system prompt constant matters for
the in-process model's cache ([KV-CACHE.md](KV-CACHE.md)).

---

## The `load_skill` Tool

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | yes | Skill name, `[A-Za-z0-9._-]+` |
| `path` | string | no | Reference file relative to the skill directory |

Lookup order for `name`: `<root>/<name>/SKILL.md`, then `<root>/<name>.md`.

| Call | Result |
|------|--------|
| `load_skill("output-format")` | `ok: true`, with the body without frontmatter |
| `load_skill("model-export", "reference/checklist.md")` | `ok: true`, with the file content unchanged |
| Unknown name | `ok: false`: `skill 'x' not found. Available skills: a, b, ...` (lists directory skills and flat `.md` files) |
| Invalid name (e.g. `../x`) | `ok: false`: `invalid skill name ...` |
| `path` escaping the skill directory | `ok: false`: `path '...' escapes the skill directory` |
| Missing reference file | `ok: false`: `reference file '...' not found in skill directory` |

`load_skill` needs no approval and is not limited to the session work directory. It reads from
the skills root, not the work directory. See [TOOLS.md](TOOLS.md) for the other tools.

---

## Enabling and Disabling Skills

| Goal | How |
|------|-----|
| Disable globally | `"skills": { "enabled": false }`. No index is put in the prompt |
| Also remove the tool | `.RemoveTool(LoadSkillTool.FunctionName)`. With `enabled: false` alone, `load_skill` is **still registered** and can load skills by name if the root exists |
| Disable one skill | Move it out of the skills root, or break its frontmatter on purpose (it is then skipped with a warning). There is no per-skill `enabled` key |
| Different skill sets per session | Point `UseSkillsDirectory` + `ToolContext.SkillsPath` at different roots, or pass a filtered `UseSkillsIndex` |

---

## Writing a Good Skill

### The description decides whether the skill is used

The model sees only `name` and `description` until it calls `load_skill`. The description must
say **what** the skill covers and **when** to load it:

| Weak | Strong |
|------|--------|
| `Output rules` | `Output rules for aggregation and change tasks — aggregation results as a Markdown table, change operations as a before/after comparison table, ...` |
| `Task procedure` | `Procedure for aggregation, analysis, and file-change tasks (per-file line / TODO counts, log analysis, ...) — read this skill before starting such a task` |

### Do

- Start the body with one sentence restating the scope.
- Use numbered **steps** that name the actual tools (`write_todos`, `execute_code`, `bash_command`).
- State **constraints** separately (what never to do, where intermediate files go).
- Keep the body focused. Move long checklists and examples into reference files and mention them
  by path in the body.
- Refer to other skills by name when they work together (as `e2e-task-rules` does with
  `output-format`).
- Keep each frontmatter value on a single line.

### Don't

- Don't use multi-line YAML or depend on extra frontmatter keys: they are ignored.
- Don't name the frontmatter differently from the directory.
- Don't put secrets in skills. Their content goes into the model context.
- Don't tell the model to bypass safety (for example, "write directly to the user's folder"). The
  safety gate still applies ([SAFETY.md](SAFETY.md)), so the instruction only causes failed calls.

### Recommended structure

```markdown
---
name: <same-as-directory>
description: <what it covers> — <when to load it>
---
# <Title>

<One sentence: scope and when it applies.>

## Steps

1. ...
2. ...

## Constraints

- ...

## Reference

- `reference/<file>.md` — <what is in it> (load with load_skill("<name>", "reference/<file>.md"))
```

---

## Examples from This Repository

The repository ships two runtime skills in [`../skills/`](../skills/).

### `output-format`

[`../skills/output-format/SKILL.md`](../skills/output-format/SKILL.md) holds **output rules**:
aggregation results as Markdown tables, changes as before/after tables, a no-fabrication rule, and
a large-results rule (at most 20 rows per table in the answer, with the full table saved to a file in the
work directory). It is a good template for a "how to present results" skill:

```markdown
---
name: output-format
description: Output rules for aggregation and change tasks — aggregation results as a Markdown table, change operations as a before/after comparison table, large results as a count + representative sample + a way to get the rest
---
# Output Format

Output rules for aggregation and change tasks (core use case U5). Read this skill before running an aggregation, change, or analysis task.

## Aggregation results

- Always report aggregation results (counts, totals, per-item lists) as a **Markdown table**.
...
```

### `e2e-task-rules`

`../skills/e2e-task-rules/SKILL.md` is a **procedure**
skill. It plans with `write_todos`, does aggregation with an `execute_code` Python script, and
makes file changes through scripts as well, so the approval prompt shows the user the whole
change. It then reports with the `output-format` rules. It shows three useful patterns: steps
that name the tools, a dedicated *Constraints* section, and delegating presentation to another
skill.

---

## FAQ

**The skill is not in the prompt.** Check the `[skills]` warnings in your logger, confirm
`skills.enabled` is `true`, and confirm the root. A relative `skillsPath` resolves against the
process current directory, which for a plugin is often the product's install folder. Use an
absolute path or `UseSkillsDirectory`.

**The model says `skill 'x' not found`.** The frontmatter `name` differs from the directory name,
or `ToolContext.SkillsPath` was not set to the same root as `UseSkillsDirectory`.

**I edited a skill but the model still follows the old text.** The index is fixed per session.
Bodies are re-read on every `load_skill` call, but the model may have loaded the body earlier in the
conversation. Start a new session.

**Can a skill run code?** Not by itself. A skill is instructions. The model carries them out with
the normal tools, under the normal safety rules.

---

## See Also

- [TOOLS.md](TOOLS.md) — `load_skill` and the other built-in tools
- [SAFETY.md](SAFETY.md) — skills run under the normal safety rules
- [CONFIGURATION.md](CONFIGURATION.md#4-skills) — the `skills` section
- [API.md](API.md) — skills settings on `AgentSessionBuilder`
- [QUICK-START.md](QUICK-START.md) — a minimal host
