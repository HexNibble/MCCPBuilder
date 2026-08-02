using System.Text;
using MCCPBuilder.Models;

namespace MCCPBuilder.Packaging;

public sealed class InnoScriptGenerator
{
    public string Generate(ProjectConfig project, string payloadDirectory, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(project);
        var appId = CreateStableAppId(project.Basic.Publisher, project.Basic.ClientName);
        var outputBaseName = Escape(NormalizeOutputBaseName(
            project.Basic.OutputFileName));
        var displayName = Escape(string.IsNullOrWhiteSpace(project.Basic.DisplayName)
            ? project.Basic.ClientName
            : project.Basic.DisplayName);
        var defaultDirectory = project.Installation.DefaultInstallDirectory
            .Replace("{clientName}", project.Basic.ClientName, StringComparison.OrdinalIgnoreCase);
        var launchPrivilegeFlag =
            project.Installation.RunLauncherAsAdministrator
                ? ""
                : " runasoriginaluser";

        var script = new StringBuilder();
        script.AppendLine("[Setup]");
        script.AppendLine("AppId={{" + appId + "}");
        script.AppendLine($"AppName={displayName}");
        script.AppendLine($"AppVersion={Escape(project.Basic.LauncherVersion)}");
        script.AppendLine($"AppVerName={displayName} {Escape(project.Basic.LauncherVersion)}");
        script.AppendLine($"AppPublisher={Escape(project.Basic.Publisher)}");
        script.AppendLine($"AppComments={Escape(project.Basic.Description)}");
        script.AppendLine($"DefaultDirName={Escape(defaultDirectory)}");
        script.AppendLine($"DisableDirPage={(project.Installation.AllowInstallDirectorySelection ? "no" : "yes")}");
        script.AppendLine("DisableProgramGroupPage=yes");
        script.AppendLine("DisableWelcomePage=no");
        script.AppendLine("DisableFinishedPage=no");
        script.AppendLine($"OutputDir={Quote(outputDirectory)}");
        script.AppendLine($"OutputBaseFilename={outputBaseName}");
        script.AppendLine("PrivilegesRequired=admin");
        script.AppendLine("ArchitecturesAllowed=x64compatible");
        script.AppendLine("ArchitecturesInstallIn64BitMode=x64compatible");
        script.AppendLine("MinVersion=10.0");
        script.AppendLine("Uninstallable=yes");
        script.AppendLine($"UninstallDisplayName={displayName}");
        script.AppendLine("UninstallDisplayIcon={app}\\Launcher.exe");
        script.AppendLine("CreateUninstallRegKey=yes");
        script.AppendLine("SetupLogging=yes");
        script.AppendLine("UsePreviousAppDir=yes");
        script.AppendLine("UsePreviousTasks=yes");
        script.AppendLine("UsePreviousLanguage=yes");
        script.AppendLine("AllowCancelDuringInstall=yes");
        script.AppendLine("CloseApplications=yes");
        script.AppendLine("CloseApplicationsFilter=Launcher.exe");
        script.AppendLine("RestartApplications=no");
        script.AppendLine("DirExistsWarning=auto");
        script.AppendLine("SolidCompression=yes");
        script.AppendLine("Compression=lzma2");
        script.AppendLine("WizardStyle=modern");
        if (!string.IsNullOrWhiteSpace(project.Basic.InstallerIconPath))
            script.AppendLine($"SetupIconFile={Quote(project.Basic.InstallerIconPath)}");

        script.AppendLine();
        script.AppendLine("[Languages]");
        script.AppendLine("Name: \"chinesesimp\"; MessagesFile: \"compiler:Default.isl\"");

        script.AppendLine();
        script.AppendLine("[Messages]");
        script.AppendLine("SetupAppTitle=安装程序");
        script.AppendLine("SetupWindowTitle=安装 - %1");
        script.AppendLine("UninstallAppTitle=卸载程序");
        script.AppendLine("UninstallAppFullTitle=卸载 %1");
        script.AppendLine("InformationTitle=提示");
        script.AppendLine("ConfirmTitle=确认");
        script.AppendLine("ErrorTitle=错误");
        script.AppendLine("ButtonBack=< 上一步");
        script.AppendLine("ButtonNext=下一步 >");
        script.AppendLine("ButtonInstall=安装");
        script.AppendLine("ButtonCancel=取消");
        script.AppendLine("ButtonFinish=完成");
        script.AppendLine("ButtonBrowse=浏览...");
        script.AppendLine("ClickNext=单击“下一步”继续，或单击“取消”退出安装程序。");
        script.AppendLine("WelcomeLabel1=欢迎使用 [name] 安装向导");
        script.AppendLine("WelcomeLabel2=此向导将在您的电脑上安装 [name/ver]。%n%n建议继续前关闭其他应用程序。");
        script.AppendLine("WizardSelectDir=选择安装位置");
        script.AppendLine("SelectDirDesc=请选择 [name] 的安装位置。");
        script.AppendLine("SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹。");
        script.AppendLine("SelectDirBrowseLabel=单击“下一步”继续；如需更改位置，请单击“浏览”。");
        script.AppendLine("WizardSelectTasks=选择附加任务");
        script.AppendLine("SelectTasksDesc=请选择安装时要执行的附加任务。");
        script.AppendLine("SelectTasksLabel2=选择需要的附加任务，然后单击“下一步”。");
        script.AppendLine("WizardReady=准备安装");
        script.AppendLine("ReadyLabel1=安装程序已准备好在您的电脑上安装 [name]。");
        script.AppendLine("ReadyLabel2a=单击“安装”继续；如需检查或更改设置，请单击“上一步”。");
        script.AppendLine("ReadyMemoDir=安装位置：");
        script.AppendLine("ReadyMemoTasks=附加任务：");
        script.AppendLine("WizardInstalling=正在安装");
        script.AppendLine("InstallingLabel=请稍候，安装程序正在安装 [name]。");
        script.AppendLine("FinishedHeadingLabel=[name] 安装完成");
        script.AppendLine("FinishedLabel=安装程序已在您的电脑上安装 [name]。");
        script.AppendLine("ClickFinish=单击“完成”退出安装程序。");
        script.AppendLine("ExitSetupTitle=退出安装");
        script.AppendLine("ExitSetupMessage=安装尚未完成。现在退出将不会完成程序安装。%n%n确定要退出吗？");
        script.AppendLine("ConfirmUninstall=确定要完全删除 %1 吗？安装目录中的游戏文件、配置和存档也会全部删除。");

        script.AppendLine();
        script.AppendLine("[Tasks]");
        script.AppendLine("Name: \"desktopicon\"; Description: \"创建桌面快捷方式\"; Flags: checkedonce");
        script.AppendLine("Name: \"startmenuicon\"; Description: \"创建开始菜单快捷方式\"; Flags: checkedonce");

        script.AppendLine();
        script.AppendLine("[Files]");
        script.AppendLine($"Source: \"{Escape(Path.Combine(payloadDirectory, "*"))}\"; DestDir: \"{{app}}\"; Flags: ignoreversion recursesubdirs createallsubdirs");
        script.AppendLine();
        script.AppendLine("[Icons]");
        script.AppendLine($"Name: \"{{autodesktop}}\\{displayName}\"; Filename: \"{{app}}\\Launcher.exe\"; WorkingDir: \"{{app}}\"; Tasks: desktopicon");
        script.AppendLine($"Name: \"{{autoprograms}}\\{displayName}\"; Filename: \"{{app}}\\Launcher.exe\"; WorkingDir: \"{{app}}\"; Tasks: startmenuicon");
        script.AppendLine();
        script.AppendLine("[Run]");
        if (project.Installation.LaunchAfterInstall)
            script.AppendLine($"Filename: \"{{app}}\\Launcher.exe\"; Description: \"立即启动\"; Flags: nowait postinstall skipifsilent{launchPrivilegeFlag}");
        script.AppendLine($"Filename: \"{{app}}\\Launcher.exe\"; Parameters: \"--post-update\"; Flags: nowait skipifdoesntexist{launchPrivilegeFlag}; Check: IsSelfUpdate");

        script.AppendLine();
        script.AppendLine("[UninstallRun]");
        script.AppendLine("Filename: \"{app}\\Launcher.exe\"; Parameters: \"--clear-user-data\"; Flags: runhidden waituntilterminated; RunOnceId: \"ClearLauncherUserData\"");

        script.AppendLine();
        script.AppendLine("[UninstallDelete]");
        script.AppendLine($"Type: files; Name: \"{{autodesktop}}\\{displayName}.lnk\"");
        script.AppendLine($"Type: files; Name: \"{{autoprograms}}\\{displayName}.lnk\"");
        script.AppendLine("Type: filesandordirs; Name: \"{app}\"");
        script.AppendLine("Type: dirifempty; Name: \"{localappdata}\\MCCPBuilder\\SavedLogins\"");
        script.AppendLine("Type: filesandordirs; Name: \"{localappdata}\\MCCPBuilder\\LaunchLogs\"");
        script.AppendLine("Type: dirifempty; Name: \"{localappdata}\\MCCPBuilder\"");
        script.AppendLine("Type: dirifempty; Name: \"{localappdata}\\MCCBuilder\\SavedLogins\"");
        script.AppendLine("Type: filesandordirs; Name: \"{localappdata}\\MCCBuilder\\LaunchLogs\"");
        script.AppendLine("Type: dirifempty; Name: \"{localappdata}\\MCCBuilder\"");
        script.AppendLine();
        script.AppendLine("[Code]");
        script.AppendLine("function IsSelfUpdate: Boolean;");
        script.AppendLine("begin");
        script.AppendLine("  Result := (ExpandConstant('{param:MCCPSELFUPDATE|0}') = '1') or");
        script.AppendLine("    (ExpandConstant('{param:MCCSELFUPDATE|0}') = '1');");
        script.AppendLine("end;");
        return script.ToString();
    }

    public static string Escape(string value) => value.Replace("\"", "\"\"");
    public static string Quote(string value) => $"\"{Escape(value)}\"";

    public static string NormalizeOutputBaseName(string value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    private static Guid CreateStableAppId(string publisher, string clientName)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{publisher}\n{clientName}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
