namespace ProjectBeacon.API.Endpoints;

public static class BeaconApi
{
    public static IEndpointRouteBuilder MapBeaconApi(this IEndpointRouteBuilder app)
    {
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
        app.MapDecisionEndpoints();
        app.MapModelEndpoints();
        return app;
    }
}
