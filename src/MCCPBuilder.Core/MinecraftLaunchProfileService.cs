using System.Text;
using System.Text.Json;
using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public sealed record GeneratedMinecraftLaunchProfile(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

public sealed class MinecraftLaunchProfileService
{
    private const string GameRootToken = "${MCCP_GAME_ROOT}";
    public static IReadOnlyList<string> Pcl2JvmPresetArguments { get; } =
        Array.AsReadOnly(
        [
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+UseG1GC",
            "-XX:G1NewSizePercent=20",
            "-XX:G1ReservePercent=20",
            "-XX:G1HeapRegionSize=32M",
            "-XX:MaxGCPauseMillis=50",
            "-XX:+PerfDisableSharedMem",
            "-XX:MinHeapFreeRatio=25",
            "-XX:MaxHeapFreeRatio=40",
            "-Dlog4j2.formatMsgNoLookups=true",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",
            "-Dfile.encoding=COMPAT"
        ]);

    public async Task GenerateAsync(
        ProjectConfig project,
        string launcherConfigDirectory,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = ResolveManifest(project);
        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var mainClass = RequiredString(root, "mainClass");
        var versionName = Path.GetFileName(project.Client.VersionDirectory);
        var libraryDirectory = GameRootToken + @"\libraries";
        var gameDirectory = GameRootToken + @"\versions\" + versionName;
        var nativesDirectory = gameDirectory + @"\" + versionName + "-natives";
        var assetsRoot = GameRootToken + @"\assets";
        var classPath = BuildClassPath(project, root);

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${natives_directory}"] = nativesDirectory,
            ["${launcher_name}"] = "MCCPBuilder",
            ["${launcher_version}"] = project.ApplicationVersion,
            ["${classpath}"] = classPath,
            ["${library_directory}"] = libraryDirectory,
            ["${classpath_separator}"] = ";",
            ["${version_name}"] = versionName,
            ["${game_directory}"] = gameDirectory,
            ["${assets_root}"] = assetsRoot,
            ["${assets_index_name}"] = ReadAssetIndex(root),
            ["${auth_player_name}"] = "${MCCP_USERNAME}",
            ["${auth_uuid}"] = "${MCCP_UUID}",
            ["${auth_access_token}"] = "${MCCP_ACCESS_TOKEN}",
            ["${clientid}"] = "${MCCP_CLIENT_ID}",
            ["${auth_xuid}"] = "${MCCP_XUID}",
            ["${user_type}"] = "${MCCP_USER_TYPE}",
            ["${version_type}"] = root.TryGetProperty("type", out var type) ? type.GetString() ?? "release" : "release",
            ["${resolution_width}"] = project.Launch.WindowWidth.ToString(),
            ["${resolution_height}"] = project.Launch.WindowHeight.ToString()
        };

        var arguments = new List<string>
        {
            $"-Xms{project.Launch.MinimumMemoryMb}m",
            $"-Xmx{project.Launch.MaximumMemoryMb}m"
        };
        var versionJvmArguments = ReadArguments(root, "jvm", project).ToArray();
        var configuredJvmArguments = project.Java.Arguments
            .Concat(project.Launch.GcArguments)
            .Concat(project.Launch.JvmArguments)
            .SelectMany(TokenizeCommandLine)
            .ToArray();
        arguments.AddRange(versionJvmArguments);
        if (project.Launch.UsePcl2JvmPreset)
        {
            foreach (var presetArgument in Pcl2JvmPresetArguments)
            {
                if (!versionJvmArguments.Contains(presetArgument, StringComparer.Ordinal) &&
                    !configuredJvmArguments.Contains(presetArgument, StringComparer.Ordinal))
                {
                    arguments.Add(presetArgument);
                }
            }
        }
        arguments.AddRange(configuredJvmArguments);
        arguments.Add(mainClass);
        var gameArguments = ReadArguments(root, "game", project)
            .Concat(project.Launch.GameArguments)
            .ToList();
        if (project.Launch.AutoJoinServer)
        {
            if (!InputValidator.IsValidMinecraftServerAddress(project.Launch.ServerAddress))
            {
                throw new InvalidDataException("自动进入服务器地址格式无效。");
            }

            RemoveArgumentWithValue(gameArguments, "--quickPlayMultiplayer");
            gameArguments.Add("--quickPlayMultiplayer");
            gameArguments.Add(project.Launch.ServerAddress);
        }
        arguments.AddRange(gameArguments);
        arguments = arguments.Select(argument => Replace(argument, replacements)).ToList();

        Directory.CreateDirectory(launcherConfigDirectory);
        var profile = new GeneratedMinecraftLaunchProfile(
            Path.Combine(".minecraft", "versions", versionName),
            arguments);
        await File.WriteAllTextAsync(
            Path.Combine(launcherConfigDirectory, "launch.arguments.json"),
            JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(launcherConfigDirectory, "launch.bat"),
            CreatePortableBatch(),
            new UTF8Encoding(false),
            cancellationToken);
    }

    public static string ResolveManifest(ProjectConfig project)
    {
        if (string.IsNullOrWhiteSpace(project.Client.VersionManifestPath))
        {
            throw new InvalidDataException("未选择有效的 Minecraft 版本 JSON。");
        }

        var manifestPath = Path.GetFullPath(Path.Combine(
            project.Client.MinecraftRootDirectory,
            project.Client.VersionManifestPath));
        if (!InputValidator.IsPathInside(project.Client.VersionDirectory, manifestPath) ||
            !File.Exists(manifestPath))
        {
            throw new InvalidDataException("Minecraft 版本 JSON 不存在或超出版本目录。");
        }

        return manifestPath;
    }

    private static string BuildClassPath(ProjectConfig project, JsonElement root)
    {
        var entries = new List<string>();
        if (root.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateArray())
            {
                if (!RulesAllow(library, project))
                {
                    continue;
                }

                var relativePath = LibraryPath(library);
                if (relativePath is not null)
                {
                    entries.Add(GameRootToken + @"\libraries\" + relativePath.Replace('/', '\\'));
                }
            }
        }

        var clientJar = project.Client.LaunchEntryPath;
        if (string.IsNullOrWhiteSpace(clientJar))
        {
            throw new InvalidDataException("版本 JSON对应的客户端 JAR 不存在。");
        }

        entries.Add(GameRootToken + @"\" + clientJar);
        return string.Join(';', entries);
    }

    private static string? LibraryPath(JsonElement library)
    {
        if (library.TryGetProperty("downloads", out var downloads) &&
            downloads.TryGetProperty("artifact", out var artifact) &&
            artifact.TryGetProperty("path", out var path))
        {
            return path.GetString();
        }

        if (!library.TryGetProperty("name", out var nameProperty))
        {
            return null;
        }

        var parts = (nameProperty.GetString() ?? "").Split(':');
        if (parts.Length < 3)
        {
            return null;
        }

        var classifier = parts.Length >= 4 ? "-" + parts[3] : "";
        return $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{classifier}.jar";
    }

    private static IEnumerable<string> ReadArguments(
        JsonElement root,
        string section,
        ProjectConfig project)
    {
        if (!root.TryGetProperty("arguments", out var arguments) ||
            !arguments.TryGetProperty(section, out var values))
        {
            yield break;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                yield return value.GetString()!;
                continue;
            }

            if (value.ValueKind != JsonValueKind.Object || !RulesAllow(value, project) ||
                !value.TryGetProperty("value", out var conditionalValue))
            {
                continue;
            }

            if (conditionalValue.ValueKind == JsonValueKind.String)
            {
                yield return conditionalValue.GetString()!;
            }
            else if (conditionalValue.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in conditionalValue.EnumerateArray())
                {
                    yield return item.GetString()!;
                }
            }
        }
    }

    private static bool RulesAllow(JsonElement element, ProjectConfig project)
    {
        if (!element.TryGetProperty("rules", out var rules))
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!RuleMatches(rule, project))
            {
                continue;
            }

            allowed = rule.TryGetProperty("action", out var action) &&
                      action.GetString() == "allow";
        }

        return allowed;
    }

    private static bool RuleMatches(JsonElement rule, ProjectConfig project)
    {
        if (rule.TryGetProperty("os", out var os))
        {
            if (os.TryGetProperty("name", out var name) && name.GetString() != "windows")
                return false;
            if (os.TryGetProperty("arch", out var arch) &&
                arch.GetString() is "x86" or "arm" or "arm64")
                return false;
        }

        if (rule.TryGetProperty("features", out var features))
        {
            foreach (var feature in features.EnumerateObject())
            {
                var actual = feature.Name switch
                {
                    "has_custom_resolution" => true,
                    "is_demo_user" => false,
                    _ => false
                };
                if (feature.Value.GetBoolean() != actual)
                    return false;
            }
        }

        return true;
    }

    private static string ReadAssetIndex(JsonElement root)
    {
        if (root.TryGetProperty("assetIndex", out var assetIndex) &&
            assetIndex.TryGetProperty("id", out var id))
        {
            return id.GetString() ?? "";
        }

        return root.TryGetProperty("assets", out var assets) ? assets.GetString() ?? "" : "";
    }

    private static string RequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"版本 JSON缺少 {property}。");
        }

        return value.GetString()!;
    }

    private static string Replace(string value, IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var replacement in replacements)
        {
            value = value.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
        }

        return value;
    }

    private static IReadOnlyList<string> TokenizeCommandLine(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in value)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static void RemoveArgumentWithValue(List<string> arguments, string option)
    {
        for (var index = arguments.Count - 1; index >= 0; index--)
        {
            if (!arguments[index].Equals(option, StringComparison.Ordinal))
            {
                continue;
            }

            arguments.RemoveAt(index);
            if (index < arguments.Count)
            {
                arguments.RemoveAt(index);
            }
        }
    }

    private static string CreatePortableBatch() => """
        @echo off
        setlocal
        set "MCCP_APP_ROOT=%~dp0.."
        "%MCCP_APP_ROOT%\Launcher.exe" --run-generated
        exit /b %ERRORLEVEL%
        """ + Environment.NewLine;
}
