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
}
