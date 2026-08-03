# MCCPBuilder v0.1.2-beta

这是 MCCPBuilder 的公开测试版本，面向 Windows 10/11 x64 的 Minecraft 定制客户端打包、启动与更新流程。

## 本版内容

- 新增 Forge 官方运行库自动补全：识别版本 JSON 中的 Minecraft、Forge 与 MCP 版本，下载并校验 Forge 官方 Installer，使用强制内置 Java 生成 `slim`、`extra`、`srg` 与 `forge-client` 运行库。
- 升级官方安装完成标记：普通官方库校验大小与 SHA-1，Forge 生成库记录并校验 SHA-256；旧标记或任一库缺失时自动修复。
- 修复官方安装界面长时间停留在 `0 / 1` 的假死观感：Installer 阶段使用不定进度，读取资源清单后显示真实文件总数与预计大小。
- Forge Installer 仅临时创建空 `launcher_profiles.json`，不会复制、读取或保留打包者的账号资料；临时安装器和本次新建的标准版本目录会安全清理。
- 修复官方来源模式误删选中版本目录中额外 JAR/JSON 的问题：现在只排除主 JAR、主 JSON 与 natives，`dac-agent.jar` 等额外组件会进入 Payload。
- 生成前检查新增 `-javaagent`、`-java` 和 `-jar` 文件引用验证；文件缺失、绝对路径不可移植或不会进入最终 Payload 时直接阻止打包。
- 提供可独立运行的 Windows x64 打包器与 Launcher。
- 支持项目保存/打开、指定 Minecraft 版本、登录/Java/安装选项和生成前检查。
- 支持官方 Mojang/Forge 安装流程，以及由打包者选择的 Modrinth、CurseForge 或自有 HTTPS 更新服务器资源模式。
- 支持正式/测试更新通道、启动器与游戏更新、暂停/继续下载及更新状态显示。
- 修复 Launcher UI、简体中文资源检查、指定版本打包和登录配置等问题。
- 新增 Debian/Ubuntu 服务端安装包：包含 `install.sh`、更新服务、systemd 单元、Nginx 示例和发布密钥管理工具。

## 下载与校验

- `MCCPBuilder.App-v0.1.2-beta.exe`：打包器。
- `Launcher.exe`：独立 Launcher 构建产物。
- `MCCPBuilder-Server-v0.1.2-beta.zip`：服务端程序与安装脚本。
- `mccp-server-install.sh`：便于直接检查的独立安装脚本；实际安装时仍应与服务端 ZIP 内其他文件一起使用。
- `MCCPBuilder-v0.1.2-beta-SHA256.txt`：发行文件 SHA-256。

服务端安装示例：

```bash
unzip MCCPBuilder-Server-v0.1.2-beta.zip
cd server
sudo ./install.sh --domain updates.example.com --email admin@example.com
```

## 许可与风险边界

MCCPBuilder 自身采用 GPL-3.0-or-later。Minecraft、Forge、模组、资源包、认证服务和下载平台仍适用各自的许可、EULA 与服务条款。本仓库和 Release 不包含 Minecraft 游戏本体、付费内容、账号凭据或真实发布密钥。

请在使用和分发前阅读 [使用与责任声明](https://github.com/HexNibble/MCCPBuilder/blob/v0.1.2-beta/DISCLAIMER.md) 与 [第三方服务和组件说明](https://github.com/HexNibble/MCCPBuilder/blob/v0.1.2-beta/THIRD_PARTY_NOTICES.md)。这些说明用于明确项目边界，不构成法律意见，也不能保证任何具体使用方式绝对无风险。
