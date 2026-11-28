# Kudos Agent for Autodesk Revit (Demo)

An application built on Kudos Agent SDK: an AI agent add-in that runs **inside Autodesk Revit 2027**. This page describes the prototype; the add-in itself is not distributed.

**Demo video:** https://www.youtube.com/watch?v=132ZBmiiJ1g

---

## What It Does

You ask in a chat pane docked in Revit. The agent reads the open model, writes Revit API C# code, runs it inside Revit after you approve it, and answers with a table.

In the demo the request is "How many structural columns are on each level of this model? Answer with a Markdown table.":

1. The agent reads the model (levels, grids, element counts per category)
2. It writes C#. The first try uses a property the current Revit API no longer has, fails to compile, and the agent fixes it on its own
3. Only code that compiles reaches the approval card; the user reads it and presses Approve
4. The code runs inside Revit and the agent answers with a per-level table

## Highlights

- **The LLM runs inside the Revit process** (local model — the model data is not sent anywhere)
- **Every script runs only after the user approves it**; code that changes the model runs in one transaction and is rolled back on error
- **Runaway scripts are stopped** (infinite loops, deep recursion) so Revit does not crash
- **Answers render as Markdown** in the pane — tables with copy as Markdown / Excel
- The agent can look at the active view as an image (for models that accept images)

## The Chat Pane

- **Send / Stop on one button**: Esc also stops the answer. Questions typed while the agent works wait in a queue and are sent in turn; Stop puts them back in the input
- **Past questions**: ↑ in an empty input lists them (kept across Revit sessions), Tab puts one back in the input
- **Images and PDF**: attach with the 📎 button or drop files on the pane; thumbnails show above the input (✕ removes one) and in the question. Images go to models that accept them; a PDF is given to the agent by its path
- **Slash commands**: `/new` (new conversation), `/compact [focus]` (summarize the conversation so far to free context), `/settings`, `/skills`, `/help`
- **Under the input**: the model in use and the tokens of the conversation
- **Approvals**: an approval card shows the full code before it runs. A bypass mode (shown in a warning colour) grants every approval without asking, for a trusted session

## Settings

- **Models**: local GGUF models in Revit's process or remote models (Anthropic, OpenAI, Azure OpenAI, Google Gemini, Amazon Bedrock, an OpenAI-compatible server such as llama.cpp). API keys are not entered in the add-in: they are read from the environment or the OS secret store
- **Instructions**: add to or replace the agent's instructions
- **Skills**: the bundled Revit C# skill plus your own skills, each on or off (editing a bundled skill saves your own copy)
- **MCP**: your MCP servers (add, edit, test, on / off)
- **Agent**: conversation compression (when and how much to keep), loop guards, attachment limits and the script time limit — defaults follow the selected model

## What the SDK Provides

The add-in passes Revit-specific tools (reading the model, running C#, placing a column, capturing the view) to the SDK in place of its general-purpose tools. The SDK handles the agent loop, approvals, the local LLM and the session records.

## Demo Setup

- Autodesk Revit 2027, sample model "Snowdon Towers Sample Structural" (not saved)
- Kudos Agent SDK 0.7.0
- Qwen3.8 27B (4-bit GGUF) through LLaMASharp / llama.cpp (CUDA) on 2 × NVIDIA GeForce RTX 3090
- .NET 10, C# scripting with Roslyn, Microsoft Agent Framework

---

Autodesk and Revit are registered trademarks or trademarks of Autodesk, Inc. in the USA and other countries. This prototype is not affiliated with or endorsed by Autodesk.
