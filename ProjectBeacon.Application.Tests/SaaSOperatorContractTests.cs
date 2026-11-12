namespace ProjectBeacon.Application.Tests;

using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Entitlements;
using Microsoft.Extensions.Configuration;

public sealed class SaaSOperatorContractTests
{
    [Fact]
    public void CurrentRegistrationPolicyIsInviteOnly()
    {
        var policy = RegistrationPolicy.SaaSAlpha;

        policy.Validate();

        Assert.Equal(RegistrationMode.InviteOnly, policy.Mode);
        Assert.False(policy.RequiresEmailVerification);
        Assert.False(policy.RequiresOperatorApproval);
        Assert.False(policy.AllowsSelfServiceOrganizationCreation);
        Assert.True(LocalAuthOptions.InviteOnly(null));

        var legacyOpenConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AUTH_LOCAL_INVITE_ONLY"] = "false"
            })
            .Build();
        Assert.True(LocalAuthOptions.InviteOnly(legacyOpenConfiguration));

        var testingOpenConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Testing",
                ["AUTH_LOCAL_INVITE_ONLY"] = "false"
            })
            .Build();
        Assert.False(LocalAuthOptions.InviteOnly(testingOpenConfiguration));
    }

    [Fact]
    public void OpenRegistrationCannotBeConfiguredWithoutVerifiedEmail()
    {
        var policy = new RegistrationPolicy(
            RegistrationMode.OpenWithVerifiedEmail,
            RequiresEmailVerification: false,
            RequiresOperatorApproval: false,
            AllowsSelfServiceOrganizationCreation: true);

        Assert.Throws<InvalidOperationException>(policy.Validate);
    }

    [Fact]
    public void ApprovalRequiredRegistrationMustRequireOperatorApproval()
    {
        var policy = new RegistrationPolicy(
            RegistrationMode.ApprovalRequired,
            RequiresEmailVerification: false,
            RequiresOperatorApproval: false,
            AllowsSelfServiceOrganizationCreation: false);

        Assert.Throws<InvalidOperationException>(policy.Validate);
    }

    [Fact]
    public void CurrentSystemAdministratorContainsEveryDefinedOperatorPermission()
    {
        var all = OperatorPermission.OrganizationsRead
            | OperatorPermission.OrganizationsManage
            | OperatorPermission.UsersRead
            | OperatorPermission.UsersManage
            | OperatorPermission.EntitlementsRead
            | OperatorPermission.EntitlementsManage
            | OperatorPermission.BillingRead
            | OperatorPermission.BillingManage
            | OperatorPermission.OperationsRead
            | OperatorPermission.ContainerLogsRead
            | OperatorPermission.AuditRead
            | OperatorPermission.AuditExport
            | OperatorPermission.SystemMaintenance
            | OperatorPermission.EmailSettingsRead
            | OperatorPermission.EmailSettingsManage
            | OperatorPermission.OperatorRolesManage;

        Assert.True(OperatorPermissionSets.Includes(
            OperatorPermissionSets.CurrentSystemAdministrator, all));
        Assert.False(OperatorPermissionSets.Includes(
            OperatorPermissionSets.CurrentSystemAdministrator, OperatorPermission.None));
    }

    [Fact]
    public void ReservedOperatorRolesHaveSeparatedPermissionSets()
    {
        var support = OperatorRolePermissionSets.For(OperatorRole.Support);
        var billing = OperatorRolePermissionSets.For(OperatorRole.Billing);
        var auditor = OperatorRolePermissionSets.For(OperatorRole.Auditor);

        Assert.True(OperatorPermissionSets.Includes(support, OperatorPermission.OrganizationsManage));
        Assert.True(OperatorPermissionSets.Includes(support, OperatorPermission.UsersManage));
        Assert.True(OperatorPermissionSets.Includes(support, OperatorPermission.EntitlementsRead));
        Assert.True(OperatorPermissionSets.Includes(support, OperatorPermission.OperationsRead));
        Assert.False(OperatorPermissionSets.Includes(support, OperatorPermission.EntitlementsManage));
        Assert.False(OperatorPermissionSets.Includes(support, OperatorPermission.BillingManage));
        Assert.True(OperatorPermissionSets.Includes(billing, OperatorPermission.EntitlementsManage));
        Assert.True(OperatorPermissionSets.Includes(billing, OperatorPermission.BillingManage));
        Assert.False(OperatorPermissionSets.Includes(billing, OperatorPermission.OperationsRead));
        Assert.False(OperatorPermissionSets.Includes(billing, OperatorPermission.OrganizationsManage));
        Assert.False(OperatorPermissionSets.Includes(billing, OperatorPermission.UsersManage));
        Assert.True(OperatorPermissionSets.Includes(auditor, OperatorPermission.AuditExport));
        Assert.True(OperatorPermissionSets.Includes(auditor, OperatorPermission.OperationsRead));
        Assert.False(OperatorPermissionSets.Includes(auditor, OperatorPermission.AuditRead | OperatorPermission.UsersManage));
        Assert.Equal(OperatorPermission.None, OperatorRolePermissionSets.For((OperatorRole)999));
    }

    [Fact]
    public void OperatorCannotGrantSubscriptionEntitlementDirectly()
    {
        Assert.DoesNotContain(
            nameof(EntitlementSource.Subscription),
            Enum.GetNames<OperatorEntitlementSource>());
    }

    [Fact]
    public void EntitlementIsEffectiveOnlyInsideItsActiveTimeWindow()
    {
        var start = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var end = DateTimeOffset.Parse("2026-12-01T00:00:00Z");
        var entitlement = new OrganizationEntitlement(
            Guid.NewGuid(),
            Guid.NewGuid(),
            EntitlementSource.AlphaProgram,
            EntitlementStatus.Active,
            start,
            end,
            new Dictionary<string, long>(),
            new HashSet<string>());

        Assert.False(entitlement.IsEffectiveAt(start.AddTicks(-1)));
        Assert.True(entitlement.IsEffectiveAt(start));
        Assert.True(entitlement.IsEffectiveAt(end.AddTicks(-1)));
        Assert.False(entitlement.IsEffectiveAt(end));
    }

    [Fact]
    public void SuspendedEntitlementIsNeverEffective()
    {
        var entitlement = new OrganizationEntitlement(
            Guid.NewGuid(),
            Guid.NewGuid(),
            EntitlementSource.Subscription,
            EntitlementStatus.Suspended,
            DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
            null,
            new Dictionary<string, long>(),
            new HashSet<string>());

        Assert.False(entitlement.IsEffectiveAt(DateTimeOffset.Parse("2026-11-08T00:00:00Z")));
    }
}