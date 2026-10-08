namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;

/// <summary>
    /// Host registration for every <c>/v1</c> endpoint group.
    /// </summary>
    public static class BeaconApi
    {
        /// <summary>
        /// Maps every <c>/v1</c> group onto the host: version, auth, orgs, projects, tasks, milestones, work, pipeline, context, labels, reports, evals, decisions, models, devices, and chat.
        /// </summary>
        public static IEndpointRouteBuilder MapBeaconApi(this IEndpointRouteBuilder app)
    {
        app.MapVersionEndpoints();
        app.MapAuthEndpoints();
        app.MapOrgEndpoints();
        app.MapProjectEndpoints();
        app.MapTaskEndpoints();
        app.MapMilestoneEndpoints();
        app.MapWorkEndpoints();
        app.MapPipelineEndpoints();
        app.MapContextEndpoints();
        app.MapLabelEndpoints();
        app.MapReportEndpoints();
        app.MapEvalEndpoints();
        app.MapDecisionEndpoints();
        app.MapModelEndpoints();
        app.MapDeviceEndpoints();
        app.MapChatEndpoints();
        return app;
    }
}
