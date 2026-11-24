namespace ProjectBeacon.Web.Tests;

using System;
using System.Reflection;
using System.Resources;

public sealed class LocalizationTests
{
    private readonly ResourceManager _rm;

    public LocalizationTests()
    {
        var assembly = typeof(ProjectBeacon.Web.Features.Dashboard.Dashboard).Assembly;
        _rm = new ResourceManager("ProjectBeacon.Web.Resources.Web", assembly);
    }

    [Fact]
    public void ResourceManager_Dashboard_English()
    {
        var value = _rm.GetString("Dashboard", new System.Globalization.CultureInfo("en"));
        Assert.Equal("Dashboard", value);
    }

    [Fact]
    public void ResourceManager_Dashboard_Russian()
    {
        var value = _rm.GetString("Dashboard", new System.Globalization.CultureInfo("ru"));
        Assert.Equal("Панель управления", value);
    }

    [Fact]
    public void ResourceManager_Dashboard_Japanese()
    {
        var value = _rm.GetString("Dashboard", new System.Globalization.CultureInfo("ja"));
        Assert.Equal("ダッシュボード", value);
    }

    [Fact]
    public void ResourceManager_Dashboard_Chinese()
    {
        var value = _rm.GetString("Dashboard", new System.Globalization.CultureInfo("zh"));
        Assert.Equal("仪表板", value);
    }

    [Fact]
    public void ResourceManager_WelcomeBack_Russian()
    {
        var value = _rm.GetString("WelcomeBack", new System.Globalization.CultureInfo("ru"));
        Assert.Equal("Добро пожаловать.", value);
    }

    [Fact]
    public void ResourceManager_WelcomeBack_English()
    {
        var value = _rm.GetString("WelcomeBack", new System.Globalization.CultureInfo("en"));
        Assert.Equal("Welcome back.", value);
    }

    [Fact]
    public void ResourceManager_Settings_Russian()
    {
        var value = _rm.GetString("Settings", new System.Globalization.CultureInfo("ru"));
        Assert.Equal("Настройки", value);
    }

    [Fact]
    public void ResourceManager_Projects_Russian()
    {
        var value = _rm.GetString("Projects", new System.Globalization.CultureInfo("ru"));
        Assert.Equal("Проекты", value);
    }

    [Fact]
    public void ResourceManager_TasksAndGoToBoard_Russian()
    {
        Assert.Equal("Задачи", _rm.GetString("Tasks", new System.Globalization.CultureInfo("ru")));
        Assert.Equal("К доске", _rm.GetString("GoToBoard", new System.Globalization.CultureInfo("ru")));
    }

    [Fact]
    public void ResourceManager_Dashboard_German()
    {
        var value = _rm.GetString("Dashboard", new System.Globalization.CultureInfo("de"));
        Assert.Equal("Dashboard", value);
    }

    [Fact]
    public void ResourceManager_PageNotFound_Russian()
    {
        var value = _rm.GetString("PageNotFound", new System.Globalization.CultureInfo("ru"));
        Assert.Equal("Страница не найдена", value);
    }

    [Fact]
    public void ResourceManager_BootstrapAlreadyCompleted_Russian()
    {
        var value = _rm.GetString("BootstrapAlreadyCompleted", new System.Globalization.CultureInfo("ru"));
        Assert.Equal("Инициализация уже выполнена.", value);
    }

    [Fact]
    public void ResourceManager_SectionGoals_English()
    {
        Assert.Equal("Goals", _rm.GetString("SectionGoals", new System.Globalization.CultureInfo("en")));
    }

    [Fact]
    public void ResourceManager_Proposed_English()
    {
        Assert.Equal("Proposed", _rm.GetString("Proposed", new System.Globalization.CultureInfo("en")));
    }
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("de")]
    [InlineData("ja")]
    [InlineData("zh")]
    public void UiNotificationResources_AreAvailableInEverySupportedCulture(string cultureName)
    {
        var culture = new System.Globalization.CultureInfo(cultureName);
        foreach (var key in new[]
        {
            "UiEmptyNotification", "UiGenericFailure", "UiGenericOperation", "UiOperationFailed",
            "UiOperationFailedWithDetail", "UiOperationAuthenticationRequired", "UiOperationUnavailable", "UiOperationNotAllowed", "UiOperationTargetNotFound",
            "UiOperationConflict", "UiOperationUnexpectedFailed", "AuthUnexpectedError",
            "AuthRequestFailed", "InviteEmailMismatch", "InviteInvalidOrExpired", "InviteTokenMissing", "ResetTokenMissing", "AuthBootstrapOperation", "AuthForgotOperation",
            "AuthInviteOperation", "AuthRecoverOperation", "AuthRegisterOperation", "AuthResetOperation",
            "ChatAnswerUnexpectedFailed", "LandingReasonWorkspaceUnavailable", "LandingReasonTaskWorktreeChanged", "LandingReasonReviewEvidenceChanged", "LandingReasonTargetBranchChanged", "LandingReasonGitConfigurationInvalid", "LandingReasonCandidateBuildFailed", "LandingReasonProtectedChangesDetected", "LandingReasonLandingRecoveryFailed", "LandingReasonRevertFailed", "LandingReasonCommandQueueFailed", "LandingReasonManualActionRequired", "LandingReasonTaskApprovalRequired", "LandingReasonPostMergeVerificationFailed", "LandingReasonUnknown", "GitSyncTechnicalDetails", "GitSyncReasonUnknown", "PipelinePreflightReasonUnknown", "WorkstationUpdatePending", "WorkstationUpdateProgress", "ChatPollingUnavailable", "TaskPipelineRefreshUnavailable", "GitSyncCommandRestoreFailed", "ProxyStatusUnavailable", "ChatTaskContextUnavailable", "TaskLoadUnavailable", "WorkstationCommandsLoadFailed", "WorkstationSettingsInvalid", "WorkstationProbeInvalid", "WorkstationUpdateReportedVersion", "WorkstationCommandFailed", "WorkstationCommandCancelled", "PipelinePatchConflict", "PipelineActivitySnapshotInvalid", "GitSyncStatusRefreshFailed", "GitSyncCancelPendingConfirm", "GitSyncCancelledBeforeStart", "GitSyncPendingExpired", "GitSyncCancelFailed", "UiGitSyncAlreadyClaimed", "FolderBrowseFailed", "ProjectSwitcherLoadFailed", "SubtaskFailedOpenActivity", "SynchronizeRepository", "FolderListingInvalid", "TimeZoneSaveFailed", "Load", "Reset", "Refresh", "Reload", "Unload", "Check", "Open", "Run", "Rename", "Upload", "Download", "Disconnect", "Move", "Initialize", "Enable", "Verify", "Disable", "Change", "Request", "Fail", "Close", "Reopen", "Activate", "Add", "ChangeStatus", "Preview", "Revert", "Export", "Accept", "Deprecate", "Replace", "Read", "Install", "Browse", "Execute", "Save", "Delete", "Create", "Update", "Restore", "Start", "Restart", "Send", "Cancel", "Attach", "Compile"
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(_rm.GetString(key, culture)), $"{key} is missing for {cultureName}.");
        }
    }
}
