# R0 — Refactoring Inventory

## Goal

Produce a complete, evidence-based inventory of production files/classes that are expensive for future coding agents to load and modify.

Do not change code.

## Inspect

For each candidate record:
- path;
- LOC;
- main type(s);
- method count;
- responsibilities;
- major dependencies;
- relevant tests;
- next hardening phase affected;
- priority: A/B/C/D.

Signals:
- production file > ~400 LOC;
- class > ~300 LOC;
- method > ~80 LOC;
- many unrelated responsibilities;
- many unrelated classes in one file;
- future hardening phase touches only one part of a large file.

## Exclude

- generated EF migrations;
- EF model snapshot;
- other generated artifacts.

## Output

Create a concise table and identify:
1. must-refactor-before-H4;
2. should-refactor-before-H5-H8;
3. optional later cleanup.

Do not propose architecture redesign.
