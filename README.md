# MCCPBuilder

MCCPBuilder 是面向 Windows 10/11 x64 的 Minecraft 定制客户端打包工具。当前处于第一阶段的最小可运行框架：优先验证项目配置、文件筛选、Java 检测和安装脚本生成，不包含正式安装器的视觉美化。

## 当前能力

- 分层的 WPF、配置模型、核心文件处理和安装包生成项目
- 新建、保存和打开 `.mccpproject` JSON 项目
- 基本信息、客户端目录、独立游戏/JVM 参数、登录、Java 与安装选项
- 登录方式按 Microsoft 正版、离线、标准 Authlib Injector、统一通行证（Nide8Auth）排列并可组合启用；最终用户可选择将账号、密码和会话永久保存在本机 AES-256-GCM 加密文件中，项目与安装包中不包含这些登录数据
- 从所选 Minecraft/Forge 版本 JSON自动解析 mainClass、libraries、规则和启动参数，不读取或依赖打包电脑上的 BAT
- 能区分 `.minecraft` 根目录、`versions` 集合目录和具体版本隔离目录
- 将筛选后的 `versions`、`libraries`、`assets` 等客户端文件复制到独立 Payload 临时目录，成功后原子替换最终输出
- Minecraft 内容固定保留在 `ClientPayload\.minecraft` 下，不再把 `versions`、`libraries`、`assets` 铺到软件根目录
- 安全保留未被排除的空目录（例如运行时需要写入本地库的 `版本名-natives`），跳过符号链接和重解析点
- 包含/排除通配符规则与默认隐私排除规则
- 异步目录扫描、取消、敏感登录文件检测
- 选择 JRE ZIP，并检查 Java 主版本、x64 架构和关键运行文件
- 安全解压 JRE，自动剥离单一顶层目录并规范化为 `ClientPayload\JAVA`
- 生成固定使用 `JAVA\bin\java.exe`、禁止系统 Java 回退的启动配置
- 生成自包含 `Launcher.exe`；启动器硬性校验内置 Java 路径，不读取或回退系统 Java
- 独立管理 MC 客户端版本和 Launcher 版本；Launcher 新版会通过已签名安装包在原安装路径自动升级
- BAT、自动生成参数和直接 JAR 三种启动路径均隐藏命令窗口，只显示登录窗口与 Minecraft 图形窗口
- 可由打包者设置最长 128 个字符的自定义游戏窗口标题；留空时保持 Minecraft 默认标题
- 可由打包者设置启动器窗口标题和 PNG/JPG/JPEG/BMP 背景图片；图片会复制到 Payload，最终 Launcher 不依赖打包电脑上的原始绝对路径
- 可分别选择 `.ico` 作为最终 `Launcher.exe` 主程序图标和安装包 EXE 图标
- 生成前错误/警告/提示列表
- 参数化生成 Inno Setup 6 脚本，自动检测或手动选择 `ISCC.exe`，并生成最终安装包
- 基础单元测试

## 系统与工具要求

- Windows 10 x64 或 Windows 11 x64
- 官方 .NET 8 SDK x64（只有 .NET Runtime 不足以构建）
- Visual Studio 2022（可选，需安装“.NET 桌面开发”工作负载）
- Inno Setup 6（生成最终安装包 EXE 时需要）

本工具不会静默下载 Java、Inno Setup 或其他未知程序。生成前检查找不到 Inno Setup 时会明确报错并阻止生成，避免只留下脚本而被误认为打包成功。

## 编译和测试

在 PowerShell 中运行：

```powershell
git clone https://github.com/light-emitting-diodes/MCCPBuilder.git
Set-Location .\MCCPBuilder
pwsh -File .\scripts\build.ps1
```

打包器 EXE 已嵌入 Launcher、Core、Models 三个构建项目和
`Directory.Build.props`。生成自定义 `Launcher.exe` 时会将模板释放到
`%TEMP%\MCCPBuilder\LauncherPublish\随机目录`，从该临时目录执行发布，完成或失败
后清理，因此打包器可以复制到桌面、其他磁盘或脱离源码目录运行。发布过程仍需要
电脑安装 .NET 8 SDK；生成最终安装包还需要 Inno Setup 6。

Windows PowerShell 5.1 环境也可使用
`powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1`。

或直接执行：

```powershell
dotnet restore .\MCCPBuilder.sln
dotnet build .\MCCPBuilder.sln -c Debug
dotnet test .\tests\MCCPBuilder.Tests\MCCPBuilder.Tests.csproj
```

## 运行

```powershell
dotnet run --project .\src\MCCPBuilder.App\MCCPBuilder.App.csproj
```

创建项目后，在各标签页填写信息；客户端目录支持中文和空格。在 Java 区域选择一个 Windows x64 JRE 的 ZIP 压缩文件。ZIP 可以直接包含 `bin\java.exe`，也可以在单一顶层目录中包含整个 JRE。构建时顶层目录会被剥离，JRE 统一写入 `output\ClientPayload\JAVA`。

“构建输出”可以选择任意独立目录。绝对路径直接使用；相对路径以正在运行的打包器
EXE 所在目录为基准。例如填写 `output` 时，最终输出为
`<打包器 EXE 所在目录>\output`，不再自动写入源码项目目录。

构建器直接读取所选版本隔离目录中的 Minecraft/Forge JSON，从中解析 `mainClass`、Windows x64规则、libraries、JVM参数和游戏参数，再自动生成短小的 `LauncherConfig\launch.bat` 与 UTF-8 `LauncherConfig\launch.arguments.json`。整个过程不要求选择 BAT，也不保存打包电脑的绝对路径。正常点击 `Launcher.exe` 时会直接读取参数配置并启动 Java，不再经过 BAT 或 `cmd.exe`；BAT只作为兼容和诊断入口保留。Launcher通过 `ProcessStartInfo.ArgumentList` 逐项传参，从而避开命令行长度限制、中文乱码及不同电脑安装路径差异。

当版本隔离目录名与版本 JSON 名不同（例如目录和主 JAR 为 `最后防线`，JSON 为 Forge版本号）时，构建器优先使用“版本隔离目录同名 JAR”作为客户端 classpath入口，JSON同名 JAR仅作为回退，避免两个 Minecraft模块同时进入 Forge模块层。

启动配置页提供“添加 JVM 预设”复选框。启用后会加入常用的 G1GC、堆空闲比例、日志安全和 UTF-8/COMPAT 编码参数；最大内存仍由“最大内存(MB)”单独控制。预设与版本 JSON、Java参数或手工 JVM参数中的完全相同参数会自动去重，手工配置保持更高优先级。

“自定义游戏标题”由打包者填写，最长 128 个字符且不能包含换行等控制字符。留空时保持 Minecraft/Forge 默认标题；填写后 Launcher会在 Java游戏进程运行期间持续设置其可见窗口标题，避免游戏初始化阶段再次覆盖。

Forge 客户端可启用“自定义整行 Forge 标识”，把标题界面的 `MCP 版本号` 整行替换为自定义文字，不再保留 `MCP` 前缀。该功能只修改打包暂存副本中 Forge Universal JAR 的清单及 `BrandingControl` 类常量，不修改源客户端；它可能不兼容会校验 Forge JAR 哈希的反作弊，因此默认关闭。

勾选“自动进入服务器”并填写 Minecraft服务器域名、IP及可选端口后，生成器会加入 `--quickPlayMultiplayer` 参数。地址不能包含 `http://`、`https://`、路径、空白符或无效端口；IPv6地址应使用 `[地址]:端口` 格式。

“启动前自动清理”可分别选择清理缓存（包括常见模组缓存目录）和日志。清理只会在登录成功后、Minecraft进程启动前执行，并严格限制在安装目录的 `.minecraft` 内；不会清理存档、模组、配置、资源包或账号数据。被占用或无权限删除的项目会写入启动日志，但不会阻止游戏启动。

安装程序使用 Inno Setup 6 的标准 Windows 安装向导，并固定通过 UAC 以管理员身份运行；主要向导页面和操作按钮由生成脚本提供中文文字，不依赖构建电脑额外安装中文语言包。打包者可单独选择是否让日常 Launcher 也以管理员身份运行：启用后 Launcher 会在更新检查前请求 UAC，随后由它创建的隐藏 CMD、内置 Java 和 Minecraft 进程都会继承同一个管理员令牌；未启用时，安装完成后的 Launcher 会降回安装前的原用户权限。JAR 文件自身没有 Windows 管理员标记，实际权限取决于承载它的 `java.exe` 进程。桌面快捷方式和开始菜单快捷方式始终作为安装时的用户选项出现，两项默认勾选且可取消，不再由打包者决定是否提供。向导支持安装目录选择、安装进度与取消、安装日志、升级时复用目录和任务选择、Windows 已安装应用注册及标准卸载。

自动更新与卸载是两种不同操作。Launcher 原位升级只替换轻量安装包中的
`Launcher.exe` 和 `LauncherConfig/update.json`，不会运行卸载程序，也不会
删除 `.minecraft`。MC 更新只删除旧清单中由发布者管理且不再需要的文件；
用户自行增加的文件始终保留。存档、配置、截图、资源包、光影包、服务器列表、
地图/路径点数据和登录信息被标记为用户数据，目标已存在时不会被服务器版本覆盖。

卸载固定采用彻底清理模式，不提供保留配置或存档选项。卸载器会先调用 Launcher清除当前客户端保存的登录会话与启动日志，再删除桌面和开始菜单快捷方式以及整个安装目录；因此安装目录中的游戏文件、配置、模组和存档都会删除。Inno Setup会同时移除该产品的 Windows卸载注册表项。

生成前会强制阻止 `nide8auth.cache`、`usercache.json`、`launcher_profiles.json`、`PCL.ini`、截图等认证缓存或用户身份内容进入安装包；新项目的默认排除规则也会排除这些文件以及常见模组缓存和日志目录。

目录选择支持以下入口：

- 选择包含 `.minecraft` 的客户端目录；
- 直接选择 `.minecraft`；
- 选择 `.minecraft\versions`；
- 选择 `.minecraft\versions\具体版本`。

当 `versions` 中只有一个有效版本时会自动选中；有多个版本时必须再选择具体版本隔离目录。打包范围仍以 `.minecraft` 为根，确保 `libraries` 和 `assets` 不会因只选择某个版本目录而遗漏；但 `versions` 下只会打包已选中的具体版本，其他兄弟版本目录会被自动排除，无需再手工添加排除规则。

项目只使用 `.mccpproject` 扩展名，不再接受其他项目扩展名。点击“生成前检查”查看错误、警告和提示。通过检查后点击“开始打包”，框架会生成：

- `output\ClientPayload\JAVA\bin\java.exe`
- `output\ClientPayload\Launcher.exe`
- `output\ClientPayload\.minecraft\versions\...`
- `output\ClientPayload\LauncherConfig\launcher.json`
- `output\ClientPayload\LauncherConfig\client-files.json`
- `output\LauncherConfig\...`
- `output\InstallerSource\setup.iss`
- `output\BuildLogs\build-日期时间.log`
- `output\输出文件名.exe`
- `output\输出文件名.exe.sha256`

`launcher.json` 固定使用相对于安装目录的 `JAVA\bin\java.exe`，Minecraft 路径固定从 `.minecraft` 开始，并将 `allowSystemJavaFallback` 设置为 `false`。`Launcher.exe` 会再次硬性校验 Java 配置，以绝对路径启动 Java，并把子进程的 `JAVA_HOME`、`JRE_HOME` 指向安装目录内的 `JAVA`。内置 `JAVA\bin` 会放在 PATH 最前面，同时保留 Windows 原系统 PATH，避免防作弊、LWJGL 等原生组件因找不到系统 DLL 而出现 `0xc0000142`；这不会产生系统 Java 回退。

“主程序 EXE 图标”和“安装包 EXE 图标”只接受结构有效的 Windows `.ico` 文件。主程序图标在 `dotnet publish` 阶段嵌入单文件 `Launcher.exe`，禁止发布后修改资源以免破坏 .NET 单文件包；安装包图标通过 Inno Setup 的 `SetupIconFile` 写入最终安装程序。

“启动器标题”留空时依次使用“显示名称”“客户端名称”和 `Minecraft 登录`。“启动器背景图片”支持 PNG、JPG、JPEG 和 BMP，最大 50 MB；打包时复制为 `ClientPayload\LauncherConfig\Appearance\background.扩展名`，`launcher.json` 只记录该相对路径。Launcher 会校验路径必须位于安装目录内，图片缺失或损坏时自动使用默认纯色背景。

登录区域依次允许勾选 Microsoft 正版、离线、标准 Authlib Injector 和统一通行证。最终 Launcher启动时必须由用户选择登录方式：离线登录必须主动选择并填写游戏名；标准 Authlib和统一通行证通过 HTTPS Yggdrasil即时认证。统一通行证会先 GET服务器ID配置并采用返回的 `apiRoot`，随后调用 `authserver/authenticate`，启动时同时添加 `-javaagent:nide8auth.jar=服务器ID` 与 `-Dnide8auth.client=true`，并提供官方注册页面入口。

最终用户可以勾选“永久保存账号和密码”。Launcher将账号、密码、角色和会话一起写入 `%LocalAppData%\MCCPBuilder\SavedLogins` 下的 AES-256-GCM 加密文件，密钥由本机 Windows `MachineGuid`、MAC 地址、客户端安装路径和随机盐经 PBKDF2-SHA256 派生；不会写入项目、Payload或日志。下次启动优先使用保存的密码重新调用正常 Yggdrasil认证接口，不再依赖 `validate`/`refresh` 才能启动。登录窗口提供“清除已保存的登录信息”按钮，卸载时也会删除当前客户端保存的信息。MachineGuid和 MAC不是秘密，本机管理员仍有能力还原保存的密码，因此该功能只用于用户明确选择的个人电脑。Microsoft OAuth界面仍在后续实现中，不会用离线占位值伪装成正版登录。

程序会自动从当前用户目录、Program Files 和 PATH 检测官方 Inno Setup 6 的 `ISCC.exe`，也可以在“4-6. 登录 / Java / 安装”的“安装包编译器”区域手动选择。项目 JSON 的 `output.innoCompilerPath` 会保存所选绝对路径，例如：

```json
{
  "output": {
    "outputDirectory": "output",
    "innoCompilerPath": "C:\\Program Files (x86)\\Inno Setup 6\\ISCC.exe"
  }
}
```

## 项目结构

```text
MCCPBuilder/
├─ src/
│  ├─ MCCPBuilder.App/        WPF 界面
│  ├─ MCCPBuilder.Launcher/   强制内置 JRE 的稳定启动入口
│  ├─ MCCPBuilder.Core/       保存、扫描、验证、Java 检测
│  ├─ MCCPBuilder.Packaging/  Inno 脚本与编译器适配
│  └─ MCCPBuilder.Models/     可版本化项目配置
├─ tests/MCCPBuilder.Tests/
├─ installer/
├─ resources/
├─ scripts/
├─ output/
└─ docs/
```

项目配置含 `formatVersion`、创建/修改时间和应用版本。当前支持格式版本 `1.0`；遇到未知版本会明确拒绝，而不是误读。JRE ZIP 路径保存在 `java.javaArchivePath`；移动项目或 ZIP 后必须重新选择和验证。

## 输出

目标输出结构为：

```text
output/
├─ ClientPayload/
├─ LauncherConfig/
├─ InstallerSource/
├─ BuildLogs/
├─ 输出文件名.exe
└─ 输出文件名.exe.sha256
```

构建器先在独立临时目录生成 Payload 和安装包，成功后再原子发布。`LauncherConfig\client-files.json` 保存 Payload 中每个文件的相对路径、大小和 SHA-256；`BuildLogs` 保存开始/结束时间、项目版本、文件数量和大小、排除数量、Java 配置、Inno 编译结果、最终路径及安装包 SHA-256。最终 EXE 旁边的 `.sha256` 文件可供独立校验。输出文件名填写 `ESD` 或 `ESD.exe` 都会规范为单一的 `ESD.exe`。

## 已知限制

- 当前环境必须另行安装官方 .NET 8 SDK 才能编译。
- 已实现 Minecraft 客户端文件复制、内置 JRE 解压、自包含启动器、逐文件清单、完整构建日志、最终安装包与 SHA-256。
- 离线、标准 Authlib和统一通行证的账号交互、会话获取及本机 AES加密登录信息复用已经实现；Microsoft OAuth仍未实现。
- BAT 模式只接受 `echo`、`chcp`、`title`、`cd`、`set`、`pause` 和 Java 启动行；包含 PowerShell、下载器、文件删除等额外命令的脚本会在生成前被拒绝。
- Inno Setup 6 未安装时不能生成最终 EXE。
- Windows 10/11 实机安装、升级和卸载尚未进入验收。

## 后续计划

1. 增补不可写目录、权限、磁盘空间、占用文件与长路径测试。
2. 完成 Microsoft OAuth 登录。
3. 在 Windows 10/11 实机验证安装、升级和卸载。
4. 第一阶段通过功能验收后，再进入正式安装程序 UI。

## 服务器更新模式

当前安装包是轻量引导安装包，不再内置 Minecraft、模组、JRE 或启动参数。
安装包只包含 `Launcher.exe` 和 `LauncherConfig/update.json`。Launcher 每次
由用户打开时都会先访问 HTTPS 更新服务器；检查失败、清单无效、下载失败或
SHA-256 不一致时会阻止登录和启动，不会回退到旧文件继续运行。
`update.json` 同时保存打包者选择的管理员运行策略；需要管理员权限时，
Launcher 会先完成 UAC 提权再联网检查，避免更新、登录或 Java 已启动后才切换权限。
“8. 服务器更新”可选择 `1、2、4、8、16、32、64、128、200` 路文件并行下载，
默认使用 200。Launcher 的 MC 更新判定只精确比较本地状态和服务器清单中的
“客户端版本”；版本号相同时不枚举、不读取，也不计算本地 MC 文件哈希。版本号
不同时按前后清单的路径、大小和 SHA-256 元数据在内存中计算增量，不读取未变化
文件的内容；已有存档、配置等用户数据仍按保护规则保留。下载过程中可以暂停和
继续，并显示所有并行连接的实时合计速度；也可以取消更新，取消后会按更新失败
阻止游戏启动。暂停只作用于当前运行中的下载，关闭 Launcher 后暂不支持跨进程
断点续传。

Launcher 始终使用逐文件并行下载，不再请求或解压 `bundle.tar.gz`。每个需要更新
的文件对应一个 HTTPS 请求，最多同时执行项目配置的 200 个请求；文件数量少于
并发数时只创建实际需要的连接。服务端发布时只验证并解压打包器上传的 ZIP，不再
额外压缩大型流式包，因此可以显著降低发布期间的 CPU、磁盘临时空间和校验负担。
每个下载文件仍会独立检查 Content-Length、实际大小和 SHA-256，全部成功后才执行
原子替换。单个请求连续 30 秒没有收到数据时会主动关闭该连接，删除该次零字节或
半成品暂存文件，并最多尝试 3 次；重试前会回滚该次尝试已计入的下载进度，避免
进度超过 100%。三次均失败才会取消本轮其他下载并保留原客户端。

“仅比较版本号”不会自动修复同版本下被手动删除或改坏的本地文件，因此每次发布
MC 内容变化都必须提高“客户端版本”。对实际下载到暂存目录的数据仍会边接收边
验证 Content-Length、实际大小和 SHA-256；这是传输完整性保护，不会再次扫描
正式目录。任一下载失败时不会把半成品应用到正式目录。该能力属于 Launcher
本身；给已安装的旧客户端启用时，必须提高“启动器版本”并重新生成、发布轻量
安装包，仅重新发布 MC 更新包不会替换旧 Launcher。

项目中的“客户端版本”表示服务器 MC 内容版本，“启动器版本”表示轻量
Launcher/安装包版本，两者都必须使用 `x.y.z`。客户端先比较启动器版本：
服务器版本较新就把安装包下载到当前用户的本地更新缓存，校验大小和 SHA-256，
退出旧 Launcher 后通过相同稳定 `AppId` 在原安装目录静默升级。新版安装完成后
会自动重新打开，再继续检查 MC 内容版本。该流程不会卸载旧版本，因此不会触发
卸载阶段的彻底数据清理。

更新客户端使用 .NET `HttpClient`，支持带 AAAA 记录的域名和 IPv6。IPv6 地址
字面量必须写成 `https://[IPv6地址]/`；实际公网访问还要求服务器拥有公网 IPv6、
Nginx 监听 `[::]:443`、IPv6 防火墙放行，并且 HTTPS 证书覆盖所用域名或 IP。

“开始打包”会生成：

- `output/ClientPayload`：完整客户端内容，只用于创建服务器更新。
- `output/ServerRelease/release-*.zip`：带逐文件 SHA-256 清单的服务器发布包。
- `output/BootstrapPayload`：仅含 Launcher 和更新引导配置。
- `output/最终安装包.exe`：由轻量内容生成的安装程序。

在“8. 服务器更新”中填写 HTTPS 地址、稳定的产品标识，并选择通过 SSH/SFTP
下载的 `mccp-publisher.key`。点击“发布 Launcher 和 MC 更新”后，打包器先读取
服务器当前清单，分别比较 Launcher 版本和 MC 客户端版本。版本相同的部分直接
跳过，不读取、不校验也不上传对应安装包或 ZIP；只有版本发生变化的部分才需要
密钥并执行上传。需要同时上传时先上传 Launcher，再上传 MC 更新 ZIP。两个请求
都使用密钥文件计算 HMAC-SHA256；密钥不会进入项目、Launcher、安装包、URL 或
日志。服务器验证签名、时间戳、随机数、路径、大小和 SHA-256，全部成功后才
切换当前版本。Launcher 与 MC 可以使用相同版本号，它们仍按各自版本独立判断。
Launcher 上传开始前，打包器会强制比较界面填写的启动器版本、
`output/BootstrapPayload/LauncherConfig/update.json` 内嵌版本和
`output/InstallerSource/setup.iss` 的 `AppVersion`。三者不一致时不会连接服务器，
并要求重新点击“开始打包”，防止把旧安装包以新版本号发布后造成循环更新。
MC 和 Launcher 新版本原子切换成功后，服务器只保留该产品的当前版本并自动删除
旧服务端版本，不保留回滚副本；发布失败时不会删除当前有效版本。
窗口底部会显示当前正在进行的步骤；计算哈希和上传时显示已处理 MB、总 MB 与
百分比。连接在上传过程中中断时，错误框会展开内部网络原因，并提示检查本机
代理、HTTPS 证书、服务器磁盘空间及 Nginx 上传限制。正式读取大文件和上传前
还会先请求 `/v1/health`；连接不可用时立即停止，不再先等待大文件哈希完成。

服务器接口：

- `GET /v1/health`
- `GET /v1/products/{productId}/manifest`
- `GET /v1/files/{productId}/{releaseId}/{path}`
- `GET /v1/launchers/{productId}/{version}/setup.exe`
- `POST /v1/publish`
- `POST /v1/products/{productId}/launcher`
- `POST /v1/products/{productId}/policy`

服务端部署与运维说明见 [server/README.md](server/README.md)。发布密钥位于
`/etc/mccp-update/publisher.key`。SSH root 用户可以下载它，也可以执行：

```bash
mccp-update-key show
mccp-update-key rotate
mccp-update-key export /root/mccp-publisher.key
```

`rotate` 会由服务器重新生成 32 字节随机密钥，旧密钥立即失效。

“8. 服务器更新”还可以上传客户端公告与启动控制策略。公告标题和正文由服务端
在客户端每次检查更新时一并返回；启用“禁止客户端启动游戏”后，客户端显示
该自定义弹窗并停止进入登录与 Minecraft 启动流程。取消公告和禁止启动两个
选项后再次上传，即可恢复正常启动；修改策略不需要重新上传整合包。

首次发布应先“开始打包”，再选择密钥并“发布 Launcher 和 MC 更新”，确认对应产品
清单返回 HTTP 200 后再分发安装包。正式或测试产品首次发布前，服务器会返回带有
`no release has been published` 的 HTTP 503；打包器将这一明确响应视为尚无版本
并继续首次上传，Launcher 仍会在正式清单发布完成前阻止游戏启动。其他 HTTP 503
仍按服务器故障处理，不会绕过检查或继续上传。

### 隔离测试通道

测试资格由安装目录中的固定空文件控制：

```text
LauncherConfig\enable-test-channel.mccptest
```

Launcher 启动时会静默检查该文件，不显示资格检测提示。文件存在时，登录窗口会
出现“切换到测试版/切换到正式版”按钮；选择状态保存在隐藏文件
`LauncherConfig\selected-test-channel.mccpstate`。测试通道的 Minecraft、Java、
启动配置、账号信息和更新状态全部位于安装目录的 `TestChannel` 文件夹，正式通道
仍使用原安装目录，两边不会互相覆盖。

删除资格文件后再次启动 Launcher，会先删除整个 `TestChannel` 和隐藏选择状态，
然后自动恢复正式通道。测试目录的递归清理不会跟随目录联接或符号链接到安装目录
之外。资格文件应由测试人员手动放入或通过受控方式分发，不会由正式安装包默认
创建。

打包器第 8 页的“发布测试更新”会使用独立服务器产品标识：

```text
正式产品标识：product-id
测试产品标识：product-id-test
```

测试包固定生成到 `output\ServerReleaseTest\test-release.zip`，不会读取或修改正式
产品清单；测试版本与服务器相同时同样直接跳过上传。“上传/更新正式公告策略”和
“上传/更新测试公告策略”也分别写入各自产品。当前正式与测试通道共用安装目录根部
的 Launcher 程序版本，测试通道只隔离 Minecraft、Java、LauncherConfig、登录信息
和服务端内容更新，避免测试更新替换正式入口程序。

## 开源许可证

MCCPBuilder 采用 **GNU General Public License v3.0 or later**
（`GPL-3.0-or-later`）发布。你可以使用、研究、修改和分发本项目，也可以进行
商业使用；对外分发本项目或其衍生版本时，必须遵守 GPLv3 的源码提供、相同许可、
版权及变更声明要求。完整条款见 [LICENSE](LICENSE)。

测试项目使用的 Microsoft.NET.Test.Sdk、xUnit、xunit.runner.visualstudio 和
coverlet.collector 分别采用 MIT 或 Apache-2.0 许可证，均不随本仓库提交构建产物。

## 备份规则

任何代码、配置、资源或工程文件修改前，都应把当时的完整项目复制到项目目录之外、
带时间戳且不会覆盖旧内容的备份目录。备份验证成功后再修改；纯只读检查不要求备份。
