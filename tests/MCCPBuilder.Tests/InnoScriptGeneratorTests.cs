using MCCPBuilder.Models;
using MCCPBuilder.Packaging;

namespace MCCPBuilder.Tests;

public sealed class InnoScriptGeneratorTests
{
    [Fact]
    public void Generate_EscapesQuotesAndSupportsChinesePaths()
    {
        var project = new ProjectConfig
        {
            Basic = new()
            {
                ClientName = "我的\"客户端",
                DisplayName = "中文客户端",
                ClientVersion = "1.0.0",
                OutputFileName = "安装程序"
            }
        };

        var script = new InnoScriptGenerator().Generate(project, @"D:\构建 文件\Payload", @"D:\构建 文件\输出");

        Assert.Contains("AppName=中文客户端", script);
        Assert.Contains("AppId={{", script);
        Assert.Contains(@"OutputDir=""D:\构建 文件\输出""", script);
        Assert.Contains(@"Source: ""D:\构建 文件\Payload\*""", script);
        Assert.Contains("我的\"\"客户端", script);
    }

    [Fact]
    public void Generate_AlwaysRequiresAdministratorPrivileges()
    {
        var project = new ProjectConfig { Basic = new() { ClientName = "Client", ClientVersion = "1.0.0" } };
        var script = new InnoScriptGenerator().Generate(project, @"C:\Payload", @"C:\Output");
        Assert.Contains("PrivilegesRequired=admin", script);
        Assert.DoesNotContain("PrivilegesRequired=lowest", script);
        Assert.Contains(@"{localappdata}\Programs\Client", script);
    }

    [Fact]
    public void Generate_RunsLauncherAsOriginalUserWhenAdminModeIsDisabled()
    {
        var project = new ProjectConfig
        {
            Basic = new() { ClientName = "Client", ClientVersion = "1.0.0" },
            Installation = new()
            {
                LaunchAfterInstall = true,
                RunLauncherAsAdministrator = false
            }
        };

        var script = new InnoScriptGenerator().Generate(
            project,
            @"C:\Payload",
            @"C:\Output");

        Assert.Contains(
            "Flags: nowait postinstall skipifsilent runasoriginaluser",
            script);
        Assert.Contains(
            "Flags: nowait skipifdoesntexist runasoriginaluser; Check: IsSelfUpdate",
            script);
    }

    [Fact]
    public void Generate_KeepsElevationForLauncherWhenAdminModeIsEnabled()
    {
        var project = new ProjectConfig
        {
            Basic = new() { ClientName = "Client", ClientVersion = "1.0.0" },
            Installation = new()
            {
                LaunchAfterInstall = true,
                RunLauncherAsAdministrator = true
            }
        };

        var script = new InnoScriptGenerator().Generate(
            project,
            @"C:\Payload",
            @"C:\Output");

        Assert.DoesNotContain("runasoriginaluser", script);
        Assert.Contains(
            "Flags: nowait postinstall skipifsilent",
            script);
    }

    [Theory]
    [InlineData("ESD", "OutputBaseFilename=ESD")]
    [InlineData("ESD.exe", "OutputBaseFilename=ESD")]
    [InlineData("中文安装包.EXE", "OutputBaseFilename=中文安装包")]
    public void Generate_NormalizesOptionalExeExtension(
        string configuredName,
        string expectedLine)
    {
        var project = new ProjectConfig
        {
            Basic = new()
            {
                ClientName = "Client",
                ClientVersion = "1.0.0",
                OutputFileName = configuredName
            }
        };

        var script = new InnoScriptGenerator().Generate(
            project,
            @"C:\Payload",
            @"C:\Output");

        Assert.Contains(expectedLine, script);
        Assert.DoesNotContain("OutputBaseFilename=ESD.exe", script);
    }

    [Fact]
    public void Generate_AlwaysLetsInstallerUserChooseBothShortcutTasks()
    {
        var project = new ProjectConfig
        {
            Basic = new() { ClientName = "Client", ClientVersion = "1.0.0" }
        };

        var script = new InnoScriptGenerator().Generate(project, @"C:\Payload", @"C:\Output");

        Assert.Contains(
            "Name: \"desktopicon\"; Description: \"创建桌面快捷方式\"; Flags: checkedonce",
            script);
        Assert.Contains(
            "Name: \"startmenuicon\"; Description: \"创建开始菜单快捷方式\"; Flags: checkedonce",
            script);
        Assert.Contains("Tasks: desktopicon", script);
        Assert.Contains("Tasks: startmenuicon", script);
    }

    [Fact]
    public void Generate_ConfiguresChineseStandardWindowsInstaller()
    {
        var project = new ProjectConfig
        {
            Basic = new()
            {
                ClientName = "Client",
                DisplayName = "测试客户端",
                ClientVersion = "1.0.0",
                Description = "测试说明"
            },
            Installation = new() { AllowInstallDirectorySelection = true }
        };

        var script = new InnoScriptGenerator().Generate(project, @"C:\Payload", @"C:\Output");

        Assert.Contains("DisableDirPage=no", script);
        Assert.Contains("SetupLogging=yes", script);
        Assert.Contains("UsePreviousTasks=yes", script);
        Assert.Contains("CreateUninstallRegKey=yes", script);
        Assert.Contains("UninstallDisplayName=测试客户端", script);
        Assert.Contains(
            "Name: \"chinesesimp\"; MessagesFile: \"compiler:Default.isl\"",
            script);
        Assert.Contains("WizardSelectTasks=选择附加任务", script);
    }

    [Fact]
    public void Generate_CanLockInstallerDirectoryPage()
    {
        var project = new ProjectConfig
        {
            Basic = new() { ClientName = "Client", ClientVersion = "1.0.0" },
            Installation = new() { AllowInstallDirectorySelection = false }
        };

        var script = new InnoScriptGenerator().Generate(project, @"C:\Payload", @"C:\Output");

        Assert.Contains("DisableDirPage=yes", script);
    }

    [Fact]
    public void Generate_UninstallRemovesShortcutsUserDataAndEntireInstallDirectory()
    {
        var project = new ProjectConfig
        {
            Basic = new()
            {
                ClientName = "Client",
                DisplayName = "最后防线",
                ClientVersion = "1.0.0"
            },
            Installation = new()
            {
                PreserveUserConfiguration = true,
                AskToPreserveUserDataOnUninstall = true
            }
        };

        var script = new InnoScriptGenerator().Generate(
            project,
            @"C:\Payload",
            @"C:\Output");

        Assert.Contains("[UninstallRun]", script);
        Assert.Contains("Parameters: \"--clear-user-data\"", script);
        Assert.Contains("[UninstallDelete]", script);
        Assert.Contains(
            "Type: files; Name: \"{autodesktop}\\最后防线.lnk\"",
            script);
        Assert.Contains(
            "Type: files; Name: \"{autoprograms}\\最后防线.lnk\"",
            script);
        Assert.Contains("Type: filesandordirs; Name: \"{app}\"", script);
        Assert.Contains(
            "Type: dirifempty; Name: \"{localappdata}\\MCCPBuilder\\SavedLogins\"",
            script);
        Assert.Contains(
            "Type: filesandordirs; Name: \"{localappdata}\\MCCPBuilder\\LaunchLogs\"",
            script);
    }

    [Fact]
    public void Generate_SupportsSilentInPlaceLauncherUpgrade()
    {
        var project = new ProjectConfig
        {
            Basic = new()
            {
                ClientName = "Client",
                ClientVersion = "8.0.0",
                LauncherVersion = "2.3.4"
            }
        };

        var script = new InnoScriptGenerator().Generate(
            project,
            @"C:\BootstrapPayload",
            @"C:\Output");

        Assert.Contains("AppVersion=2.3.4", script);
        Assert.Contains("UsePreviousAppDir=yes", script);
        Assert.Contains(
            "CloseApplicationsFilter=Launcher.exe",
            script);
        Assert.Contains(
            "Parameters: \"--post-update\"",
            script);
        Assert.Contains(
            "{param:MCCPSELFUPDATE|0}",
            script);
        Assert.DoesNotContain("[InstallDelete]", script);
    }
}
