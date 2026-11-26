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

## What the SDK Provides

The add-in passes Revit-specific tools (reading the model, running C#, placing a column, capturing the view) to the SDK in place of its general-purpose tools. The SDK handles the agent loop, approvals, the local LLM and the session records.

## Demo Setup

- Autodesk Revit 2027, sample model "Snowdon Towers Sample Structural" (not saved)
- Kudos Agent SDK 0.6.0
- Qwen3.8 27B (4-bit GGUF) through LLaMASharp / llama.cpp (CUDA) on 2 × NVIDIA GeForce RTX 3090
- .NET 10, C# scripting with Roslyn, Microsoft Agent Framework

---

Autodesk and Revit are registered trademarks or trademarks of Autodesk, Inc. in the USA and other countries. This prototype is not affiliated with or endorsed by Autodesk.
