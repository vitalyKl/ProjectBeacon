namespace ProjectBeacon.Application.Authorization;

/// <summary>Resource an <see cref="AuthAction"/> is checked against.</summary>
public enum ResourceType
{
    Project,
    Org,
    ProjectMember,
    OrgMember,
    ApiToken,
    ProjectInvite,
    OrgInvite,
    Task,
    TaskStep,
    Pipeline,
    Context,
    Decision,
    Milestone,
    Label,
    Report,
    ChatSession,
    Device,
    WorkstationCommand,
    ProjectRuntime,
    ModelBackend,
    EvalRun,
    Work,
}
