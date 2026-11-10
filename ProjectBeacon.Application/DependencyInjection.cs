namespace ProjectBeacon.Application;

using Application.Agents;
using Application.Admin;
using Application.Common;
using Application.Auth;
using Application.Authorization;
using Application.Chat;
using Application.Devices;
using Application.Evals;
using Application.Identity;
using Application.Projects;
using Application.Tasks;
using Application.Milestones;
using Application.Mail;
using Application.Context;
using Application.Decisions;
using Application.Reports;
using Application.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
/// <summary>
/// Registers Application handlers and the authorization service for the host.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IAuthorizationService, AuthorizationService>();
        services.AddScoped<IAdminGate, DatabaseAdminGate>();
        services.AddScoped<PlatformEmailAdminService>();

        services.AddTransient<BootstrapHandler>();
        services.AddTransient<RecoverAdminHandler>();
        services.AddTransient<LoginHandler>();
        services.AddTransient<RegisterHandler>();
        services.AddTransient<ForgotPasswordHandler>();
        services.AddTransient<ResetPasswordHandler>();
        services.AddTransient<ChangePasswordHandler>();
        services.AddTransient<CreateOrgInviteHandler>();
        services.AddTransient<CreateProjectInviteHandler>();
        services.AddTransient<GetInviteHandler>();
        services.AddTransient<AcceptInviteHandler>();
        services.AddTransient<ListOrgInvitesHandler>();
        services.AddTransient<ListProjectInvitesHandler>();
        services.AddTransient<RevokeOrgInviteHandler>();
        services.AddTransient<RevokeProjectInviteHandler>();

        services.AddTransient<CreateOrgHandler>();
        services.AddTransient<UpdateOrgHandler>();
        services.AddTransient<GetOrgHandler>();
        services.AddTransient<ListOrgsHandler>();

        services.AddTransient<CreateProjectHandler>();
        services.AddTransient<UpdateProjectHandler>();
        services.AddTransient<DeleteProjectHandler>();
        services.AddTransient<GetProjectHandler>();
        services.AddTransient<ListProjectsHandler>();

        services.AddTransient<AddProjectMemberHandler>();
        services.AddTransient<RemoveProjectMemberHandler>();
        services.AddTransient<GetProjectMembersHandler>();

        services.AddTransient<CreateApiTokenHandler>();
        services.AddTransient<RevokeApiTokenHandler>();
        services.AddTransient<GetApiTokenHandler>();
        services.AddTransient<ListApiTokensHandler>();
        services.AddTransient<MatchLabelHandler>();
        services.AddTransient<AddLabelPathHandler>();
        services.AddTransient<CreateLabelHandler>();
        services.AddTransient<UpdateLabelHandler>();
        services.AddTransient<DeleteLabelHandler>();
        services.AddTransient<GenerateReportHandler>();
        services.AddTransient<ListReportsHandler>();
        services.AddTransient<GetReportHandler>();
        services.AddTransient<ContextCostReportHandler>();

        services.AddTransient<CreateTaskHandler>();
        services.AddTransient<UpdateTaskHandler>();
        services.AddTransient<DeleteTaskHandler>();
        services.AddTransient<ChangeSubStageHandler>();
        services.AddTransient<AddCommentHandler>();
        services.AddTransient<ListTaskStepsHandler>();
        services.AddTransient<AddTaskStepHandler>();
        services.AddTransient<ToggleTaskStepHandler>();
        services.AddTransient<DeleteTaskStepHandler>();
        services.AddTransient<SetDependenciesHandler>();
        services.AddTransient<AddReviewNotesHandler>();
        services.AddTransient<GetTaskHandler>();
        services.AddTransient<ListProjectTasksHandler>();
        services.AddTransient<ListTasksByStatusHandler>();
        services.AddTransient<ClaimTaskHandler>();
        services.AddTransient<ChangeTaskStatusHandler>();

        services.AddTransient<FinishWorkHandler>();
        services.AddTransient<WorkspaceIndexHandler>();
        services.AddTransient<WorkspaceReadHandler>();
        services.AddTransient<WorkspaceWriteHandler>();

        services.AddTransient<ISessionSpawner, ManualSessionSpawner>();
        services.AddTransient<PipelineActorQueueDispatcher>();
        services.AddTransient<StartPipelineHandler>();
        services.AddTransient<CancelPipelineSessionHandler>();
        services.AddTransient<CancelSubtaskHandler>();
        services.AddTransient<CancelPipelineHandler>();
        services.AddTransient<RestartPipelineHandler>();
        services.AddTransient<CreateSubtaskHandler>();
        services.AddTransient<StartActorSessionHandler>();
        services.AddTransient<LaunchSessionHandler>();
        services.AddTransient<RetryPipelineSessionHandler>();
        services.AddTransient<ReportSubtaskResultHandler>();
        services.AddTransient<FailSubtaskHandler>();
        services.AddTransient<StartReviewHandler>();
        services.AddTransient<RecordReviewVerdictHandler>();
        services.AddTransient<RecordReviewCheckHandler>();
        services.AddTransient<ApprovePipelineHandler>();
        services.AddTransient<ForceClosePipelineHandler>();
        services.AddTransient<GetLivePipelineTaskHandler>();
        services.AddTransient<GetPipelineHandler>();
        // Minimal API and Blazor inject these handlers by concrete type.
        services.AddTransient<ReplyPipelineFormHandler>();
        services.AddTransient<ListWaitingPipelineTasksHandler>();

        services.AddTransient<GetCurrentProjectHandler>();
        services.AddTransient<GetDashboardCountsHandler>();
        services.AddTransient<ListMyProjectsHandler>();
        services.AddTransient<GetProjectOverviewHandler>();
        services.AddTransient<GetProjectPulseHandler>();
        services.AddTransient<ListLabelsHandler>();
        services.AddScoped<TenantContextBinder>();

        services.AddTransient<UpsertContextNodeHandler>();
        services.AddTransient<ListContextNodesHandler>();
        services.AddTransient<GetContextNodeHandler>();
        services.AddTransient<DeleteContextNodeHandler>();
        services.AddTransient<CompileBriefHandler>();
        services.AddTransient<ImportFilesHandler>();
        services.AddTransient<ExportAgentsMdHandler>();
        services.AddTransient<CreateConstraintHandler>();
        services.AddTransient<ListConstraintsHandler>();
        services.AddTransient<GetConstraintHandler>();
        services.AddTransient<ActivateConstraintHandler>();
        services.AddTransient<RejectConstraintHandler>();
        services.AddTransient<ListContextRevisionsHandler>();

        services.AddTransient<CreateDecisionHandler>();
        services.AddTransient<ListDecisionsHandler>();
        services.AddTransient<GetDecisionHandler>();
        services.AddTransient<AcceptDecisionHandler>();
        services.AddTransient<DeprecateDecisionHandler>();
        services.AddTransient<SupersedeDecisionHandler>();

        services.AddTransient<CreateMilestoneHandler>();
        services.AddTransient<UpdateMilestoneHandler>();
        services.AddTransient<DeleteMilestoneHandler>();
        services.AddTransient<GetMilestoneHandler>();
        services.AddTransient<ListProjectMilestonesHandler>();
        services.AddTransient<CloseMilestoneHandler>();
        services.AddTransient<ReopenMilestoneHandler>();

        services.AddTransient<CreateDeviceHandler>();
        services.AddTransient<ListDevicesHandler>();
        services.AddTransient<RevokeDeviceHandler>();
        services.AddTransient<HeartbeatDeviceHandler>();
        services.AddTransient<EnqueueCommandHandler>();
        services.AddTransient<EnqueueProjectGitSyncHandler>();
        services.AddTransient<GetProjectGitSyncCommandHandler>();
        services.AddTransient<ClaimNextCommandHandler>();
        services.AddTransient<CompleteCommandHandler>();
        // Device progress endpoint injects this concrete handler directly.
        services.AddTransient<ReportCommandProgressHandler>();
        // Minimal API endpoints inject this concrete handler directly, so register the concrete type for endpoint metadata inference.
        services.AddTransient<IssueHarnessTokenHandler>();
        services.AddTransient<GetCommandHandler>();
        services.AddTransient<ListCommandsHandler>();
        services.AddTransient<AttachRuntimeHandler>();
        services.AddTransient<ListRuntimesHandler>();
        services.AddTransient<DetachRuntimeHandler>();
        services.AddTransient<GetRuntimeDiagnosticsHandler>();
        services.AddTransient<GetLlamaSwapConfigHandler>();
        services.AddTransient<ListHostSamplesHandler>();

        services.AddTransient<ILlamaSwapProxy, UnavailableLlamaSwapProxy>();
        services.AddTransient<UpsertLocalModelBackendHandler>();
        services.AddTransient<DeleteLocalModelBackendHandler>();
        services.AddTransient<SetRoleBindingHandler>();
        services.AddTransient<RemoveRoleBindingHandler>();
        services.AddTransient<GetModelRegistryHandler>();
        services.AddTransient<GetAgentProfileOverridesHandler>();
        services.AddTransient<SaveAgentProfileOverrideHandler>();
        services.AddTransient<ResetAgentProfileOverrideHandler>();
        // The Blazor MCP settings component injects these handlers by concrete type.
        services.AddTransient<ListProjectMcpServersHandler>();
        services.AddTransient<SaveProjectMcpServersHandler>();
        services.AddTransient<GetChatModelHandler>();
        services.AddTransient<SetChatModelHandler>();
        services.AddTransient<NudgeModelsHandler>();
        services.AddTransient<ListAgentProviderConnectionsHandler>();
        services.AddTransient<SaveAgentProviderConnectionHandler>();
        services.AddTransient<DeleteAgentProviderConnectionHandler>();
        services.AddTransient<DeviceProviderConnectionsHandler>();
        services.AddTransient<GetTotpStatusHandler>();
        services.AddTransient<BeginTotpHandler>();
        services.AddTransient<ConfirmTotpHandler>();
        services.AddTransient<DisableTotpHandler>();
        services.AddTransient<ListTaskKindsHandler>();
        services.AddTransient<SaveTaskKindHandler>();
        services.AddTransient<DeleteTaskKindHandler>();
        services.AddTransient<ResetBuiltInTaskKindHandler>();
        services.AddTransient<ListTaskPhasesHandler>();
        services.AddTransient<SetTaskPhaseModelHandler>();
        services.AddTransient<SaveAgentTemplateHandler>();
        services.AddTransient<DeleteAgentTemplateHandler>();
        services.AddTransient<GetProxyStatusHandler>();
        services.AddTransient<ReloadProxyHandler>();
        services.AddTransient<UnloadProxyHandler>();
        services.AddTransient<ApplyAgentConfigHandler>();
        services.AddSingleton<ChatEventSignal>();
        services.AddTransient<CreateChatSessionHandler>();
        services.AddTransient<ListChatSessionsHandler>();
        services.AddTransient<GetChatSessionHandler>();
        services.AddTransient<ListChatPartsHandler>();
        services.AddTransient<SendChatPromptHandler>();
        services.AddTransient<AbortChatHandler>();
        // Chat API endpoints inject these concrete handlers directly; interface scanning alone is not enough for Minimal API service inference.
        services.AddTransient<SwitchChatAgentHandler>();
        services.AddTransient<ReplyChatFormHandler>();
        services.AddTransient<RenameChatSessionHandler>();
        services.AddTransient<ArchiveChatSessionHandler>();
        services.AddTransient<AppendChatPartHandler>();
        services.AddTransient<MarkChatIdleHandler>();
        services.AddTransient<ReportChatUsageHandler>();

        services.AddTransient<RecordEvalRunHandler>();
        services.AddTransient<CompleteEvalRunHandler>();
        services.AddTransient<ListEvalRunsHandler>();
        services.AddTransient<EvalPairHandler>();

        RegisterDispatchableHandlers(services);
        services.AddTransient<ICommandDispatcher, ApplicationDispatcher>();
        services.AddTransient<IQueryDispatcher, ApplicationDispatcher>();

        return services;
    }

    private static void RegisterDispatchableHandlers(IServiceCollection services)
    {
        var handlerDefinitions = new HashSet<Type>
        {
            typeof(ICommandHandler<,>),
            typeof(IQueryHandler<,>)
        };

        var candidates = typeof(ServiceCollectionExtensions).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters)
            .SelectMany(implementationType => implementationType.GetInterfaces()
                .Where(contract => contract.IsGenericType
                    && handlerDefinitions.Contains(contract.GetGenericTypeDefinition()))
                .Select(contract => (Contract: contract, Implementation: implementationType)))
            .ToArray();

        var duplicate = candidates
            .GroupBy(candidate => candidate.Contract)
            .FirstOrDefault(group => group.Select(candidate => candidate.Implementation).Distinct().Count() > 1);
        if (duplicate is not null)
        {
            var implementations = string.Join(", ", duplicate.Select(candidate => candidate.Implementation.FullName).Distinct());
            throw new InvalidOperationException(
                $"Multiple Application handlers implement '{duplicate.Key.FullName}': {implementations}.");
        }

        foreach (var candidate in candidates)
            services.AddTransient(candidate.Contract, candidate.Implementation);
    }
}
