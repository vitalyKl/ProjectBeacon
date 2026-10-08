namespace ProjectBeacon.Domain.Enums;

/// <summary>Whose brief a context section belongs to. Compile merges by this scope.</summary>
    public enum ContextScopeType
{
    Project = 0,
    Repo = 1,
    Path = 2,
    Task = 3
}

/// <summary>Whether a human has accepted an imported or edited context section.</summary>
    public enum ContextReviewState
{
    NeedsReview = 0,
    Reviewed = 1
}

/// <summary>Where a context section was authored. <see cref="Native"/> is written in Beacon.</summary>
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
