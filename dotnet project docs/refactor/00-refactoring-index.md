# ProjectBeacon — Structural Refactoring Index

## Purpose

Perform safe structural refactoring after H3.3 and before H4+.

Primary goals:
- reduce agent context size;
- isolate responsibilities;
- make H4-H8 changes local and predictable;
- reduce regression surface.

This is structural refactoring, not architecture redesign.

## Rules

Allowed:
- move existing code;
- extract classes/files/methods;
- move related DTOs/records;
- namespace/usings cleanup;
- small duplication removal with identical semantics.

Do not change:
- HTTP/MCP contracts;
- DB schema;
- authorization semantics;
- command semantics;
- runtime lifecycle;
- desired/applied semantics;
- public behavior.

Always run affected tests, full test suite, and formatting after each task.

## Execution

1. R0 Inventory
2. R1 Core extraction
   - R1.1 DeviceHandlers
   - R1.2 WorkstationActions
   - R1.3 McpApiTools
   - R1.4 McpStdioServer
   - R1.5 WorkstationDaemon
   - R1.6 CodeIndex
   - R1.7 BeaconDbContext
3. H4
4. R2 extraction ahead of H5-H8
5. H5-H8
6. R3 secondary cleanup

## Priority

P0:
- DeviceHandlers
- WorkstationActions
- McpApiTools
- McpStdioServer

P1:
- WorkstationDaemon
- CodeIndex
- BeaconDbContext

P2:
- TaskHandlers
- AuthHandlers
- large Web components
- large test files

Do not refactor generated EF migrations/snapshots.
