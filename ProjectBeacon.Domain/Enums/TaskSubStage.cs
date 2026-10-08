namespace ProjectBeacon.Domain.Enums;

/// <summary>In-progress checklist shown on the board. <see cref="Complete"/> marks the task Done.</summary>
    public enum TaskSubStage
{
    RequirementGathering,
    Estimation,
    IntermediateResult,
    Review,
    Corrections,
    ReviewCorrections,
    Complete
}
