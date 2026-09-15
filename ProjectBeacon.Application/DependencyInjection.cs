namespace ProjectBeacon.Application;

using Application.Agents;
using Application.Auth;
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
        services.AddTransient<GenerateReportHandler>();
        services.AddTransient<ListReportsHandler>();
        services.AddTransient<GetReportHandler>();

        services.AddTransient<CreateTaskHandler>();
        services.AddTransient<UpdateTaskHandler>();
        services.AddTransient<DeleteTaskHandler>();
        services.AddTransient<ChangeSubStageHandler>();
        services.AddTransient<AddCommentHandler>();
        services.AddTransient<SetDependenciesHandler>();
        services.AddTransient<AddReviewNotesHandler>();
        services.AddTransient<GetTaskHandler>();
        services.AddTransient<ListProjectTasksHandler>();
        services.AddTransient<ListTasksByStatusHandler>();
        services.AddTransient<ClaimTaskHandler>();
        services.AddTransient<ChangeTaskStatusHandler>();

        services.AddTransient<FinishWorkHandler>();

        services.AddTransient<GetCurrentProjectHandler>();
        services.AddTransient<GetDashboardCountsHandler>();
        services.AddTransient<ListMyProjectsHandler>();
        services.AddTransient<GetProjectOverviewHandler>();
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

        services.AddTransient<CreateMilestoneHandler>();
        services.AddTransient<UpdateMilestoneHandler>();
        services.AddTransient<DeleteMilestoneHandler>();
        services.AddTransient<GetMilestoneHandler>();
        services.AddTransient<ListProjectMilestonesHandler>();
        services.AddTransient<CloseMilestoneHandler>();
        services.AddTransient<ReopenMilestoneHandler>();

        services.AddTransient<ILlamaSwapProxy, UnavailableLlamaSwapProxy>();
        services.AddTransient<UpsertLocalModelBackendHandler>();
        services.AddTransient<DeleteLocalModelBackendHandler>();
        services.AddTransient<SetRoleBindingHandler>();
        services.AddTransient<RemoveRoleBindingHandler>();
        services.AddTransient<GetModelRegistryHandler>();
        services.AddTransient<GetProxyStatusHandler>();
        services.AddTransient<ReloadProxyHandler>();
        services.AddTransient<UnloadProxyHandler>();

        return services;
    }
}
