namespace ProjectBeacon.Domain.Enums;

public enum ContextScopeType
{
    Project = 0,
    Repo = 1,
    Path = 2,
    Task = 3
}

public enum ContextReviewState
{
    NeedsReview = 0,
    Reviewed = 1
}

public enum ContextSource
{
    Native = 0,
    ImportedAgentsMd = 1,
    ImportedClaudeMd = 2,
    ImportedCursor = 3,
    ImportedGrok = 4,
    ImportedOwnershipMd = 5,
    ImportedConventionsMd = 6
}
