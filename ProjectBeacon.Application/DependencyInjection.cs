namespace ProjectBeacon.Application;

using Application.Agents;
using Application.Auth;
using Application.Chat;
using Application.Devices;
using Application.Identity;
using Application.Projects;
using Application.Tasks;
using Application.Milestones;
using Application.Context;
using Application.Decisions;
using Application.Reports;
using Infrastructure.LlamaSwap;
using Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services)
    {
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

        if (services.All(d => d.ServiceType != typeof(LlamaSwapOptions)))
            services.AddSingleton(_ => LlamaSwapOptions.FromEnvironment());
        services.AddTransient<ISessionSpawner, ManualSessionSpawner>();
        services.AddTransient<StartPipelineHandler>();
        services.AddTransient<CreateSubtaskHandler>();
        services.AddTransient<StartActorSessionHandler>();
        services.AddTransient<LaunchSessionHandler>();
        services.AddTransient<ReportSubtaskResultHandler>();
        services.AddTransient<FailSubtaskHandler>();
        services.AddTransient<StartReviewHandler>();
        services.AddTransient<RecordReviewVerdictHandler>();
        services.AddTransient<ApprovePipelineHandler>();
        services.AddTransient<ForceClosePipelineHandler>();
        services.AddTransient<GetPipelineHandler>();

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
        services.AddTransient<ActivateConstraintHandler>();
        services.AddTransient<RejectConstraintHandler>();
        services.AddTransient<ListContextRevisionsHandler>();

        services.AddTransient<CreateDecisionHandler>();
        services.AddTransient<ListDecisionsHandler>();
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
        services.AddTransient<ClaimNextCommandHandler>();
        services.AddTransient<CompleteCommandHandler>();
        services.AddTransient<GetCommandHandler>();
        services.AddTransient<AttachRuntimeHandler>();
        services.AddTransient<ListRuntimesHandler>();
        services.AddTransient<DetachRuntimeHandler>();
        services.AddTransient<GetLlamaSwapConfigHandler>();
        services.AddTransient<ListHostSamplesHandler>();

        services.AddTransient<ILlamaSwapProxy, UnavailableLlamaSwapProxy>();
        services.AddTransient<UpsertLocalModelBackendHandler>();
        services.AddTransient<DeleteLocalModelBackendHandler>();
        services.AddTransient<SetRoleBindingHandler>();
        services.AddTransient<RemoveRoleBindingHandler>();
        services.AddTransient<GetModelRegistryHandler>();
        services.AddTransient<GetChatModelHandler>();
        services.AddTransient<SetChatModelHandler>();
        services.AddTransient<NudgeModelsHandler>();
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
        services.AddTransient<CreateChatSessionHandler>();
        services.AddTransient<ListChatSessionsHandler>();
        services.AddTransient<GetChatSessionHandler>();
        services.AddTransient<ListChatPartsHandler>();
        services.AddTransient<SendChatPromptHandler>();
        services.AddTransient<AbortChatHandler>();
        services.AddTransient<AppendChatPartHandler>();
        services.AddTransient<MarkChatIdleHandler>();

        return services;
    }
}
