export class UniqueViolationError extends Error {
  override readonly name = "UniqueViolationError";

  constructor(constraint: string) {
    super(`unique constraint violated: ${constraint}`);
  }
}

export class ProjectNotFoundError extends Error {
  override readonly name = "ProjectNotFoundError";

  constructor() {
    super("project not found");
  }
}
