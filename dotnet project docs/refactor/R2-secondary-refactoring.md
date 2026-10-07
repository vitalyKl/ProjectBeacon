# R2/R3 — Secondary Refactoring

Run only after R1 and before the related hardening phase when useful.

## TaskHandlers

Split by:
- lifecycle;
- comments;
- dependencies;
- queries.

## AuthHandlers

Split by:
- bootstrap;
- recovery;
- login/registration;
- password reset/change.

## Web components

Review:
- `TaskDetail.razor`
- `Agents.razor`
- `Workstations.razor`
- `ProjectManage.razor`

Extract only stable responsibilities. Avoid component explosion.

## Tests

Review large test files and split by behavior/subject:
- authorization;
- device;
- daemon;
- workstation actions;
- pipeline.

Do not refactor test architecture while production behavior is changing.

## Rule

Each task must be independently reviewable and behavior-preserving.
