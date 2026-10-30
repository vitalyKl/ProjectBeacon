namespace ProjectBeacon.Domain.Enums;

/// <summary>How an eval run differs from its paired run.</summary>
public enum EvalCondition
{
    WithBrief,
    WithoutBrief,
    OpenCode,
    Native
}
