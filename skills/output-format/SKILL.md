---
name: output-format
description: Output rules for aggregation and change tasks — aggregation results as a Markdown table, change operations as a before/after comparison table, large results as a count + representative sample + a way to get the rest
---
# Output Format

Output rules for aggregation and change tasks (core use case U5). Read this skill before running an aggregation, change, or analysis task.

## Aggregation results

- Always report aggregation results (counts, totals, per-item lists) as a **Markdown table**.
- Keep column headers short and self-explanatory, and align numeric columns.
- Follow the table with a 1–3 sentence summary (totals, notable items, caveats).

## Change operations

- Always report change operations (adding, modifying, or deleting files) as a **before/after comparison table** (target file / before / after).
- State the number of files changed and how the change was verified (re-read, diff, test run, etc.).

## No fabrication

- Base every fact in the answer (file names, directories, counts, code contents, error messages) **only on results returned by tools / MCP / skills in this session**. Never fill gaps from memory, guesses, or imagination.
- If a tool call was denied, failed, or returned nothing, say so honestly. Never substitute an imagined result.
- If you are forced to give a final answer before the task is complete, clearly separate verified results from unverified or not-retrieved parts (e.g. mark them "not retrieved"). Never present a guess as a result.

## Large results

- When there are more than 20 result rows, do not list them all:
  1. Have the aggregation script itself write the full results to a Markdown table file (a path relative to the session working directory, e.g. `results.md` — never write into the directory or repository being analyzed).
  2. Answer with Markdown tables of at most 20 rows **each** (the most relevant rows, e.g. the largest values). The limit applies to every table in the answer, including extra or derived tables (a subset of the items that match a condition, a top-N list, a breakdown): show at most 20 rows of each and state its total row count — the full data is in the saved file.
  3. State the totals (row count, sums of numeric columns).
  4. Give the saved file's path as the way to get the rest.
