namespace ProjectBeacon.Domain.Enums;

/// <summary>Lifecycle of a review check run. <see cref="Failed"/> cannot prove <c>finish_work</c> done.</summary>
    public enum ReviewRunStatus
{
    Started,
    Completed,
    Failed
}
