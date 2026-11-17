namespace ProjectBeacon.Domain.Enums;

/// <summary>How much of the task landing decision may be automated by a project policy.</summary>
public enum GitLandingAutonomyLevel
{
    /// <summary>Never execute a landing automatically. A human must explicitly execute a preview.</summary>
    Manual = 0,
    /// <summary>Land through the project integration branch. This requires the Phase 6 integration workflow.</summary>
    IntegrationBranch = 1,
    /// <summary>Execute a verified preview directly against the reviewed target branch.</summary>
    ReviewedTarget = 2
}
