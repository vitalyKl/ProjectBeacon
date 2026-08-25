---
managed-by: projectbeacon
---

# Beacon's Differentiation

Teams adopt **Linear + MCP** and assume agent context is solved by `.cursor/rules` or `AGENTS.md`. That assumption fails because both approaches treat context as **static text** — either in a ticketing system where agents are an afterthought, or in markdown files that drift from the board, are never scoped to the current task, and cannot be versioned or audited. Beacon's claim is narrow: **a compiled, budgeted session brief plus a shared control plane for work is measurably more effective than raw repo access, and the difference can be measured before it can be debated.**

The compiled brief is the only surface that combines four signals in one request: the living project brief (goals, non-goals, conventions, pitfalls), the current task (acceptance, linked paths, dependencies), active constraints (must/must_not rules that override any markdown file), and the last handoff (who changed what, what to do next). Linear+MCP can provide the first signal (task metadata) and AGENTS.md can provide the second (static rules), but neither compiles them together against the current task context, nor can they detect when the brief has drifted from the board.

The falsifier is **A1: the context-effectiveness eval harness**. If a fixture-driven comparison of "with brief" vs "raw repo access" shows no statistically significant reduction in tokens consumed before the first correct edit, fewer agent turns to completion, or a higher pass rate, the thesis collapses and Beacon must pivot its product focus. If the harness shows saved tokens and saved turns consistently across fixtures, the claim holds and the product can invest in the control plane (roadmap, task coordination, session memory) knowing the brief compiler is the differentiator, not the board itself.

Two concrete consequences follow:

1. **Beacon is not a ticketing tool with MCP added.** Linear and Jira solve human workflow. Beacon solves agent workflow *around* that human workflow: it compiles the brief that agents actually consume, tracks whose session is doing what so agents don't collide, and preserves handoff so the next agent doesn't start from zero. The board is the UI for the control plane, not the product.

2. **AGENTS.md is a compatibility surface, not a product.** Beacon imports and exports AGENTS.md because agent hosts require it. But the living source of truth is the Context editor and the compile endpoint — not a markdown file on disk. Agents that read AGENTS.md directly are on the v2 path (bidirectional sync). v1 agents get a compiled brief that is always consistent with the board.
