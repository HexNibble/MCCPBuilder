using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MCCPBuilder.Core;

namespace MCCPBuilder.Launcher;

public partial class LoginWindow : Window
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly SecureLoginStore _loginStore;
    private readonly string _installationDirectory;
    private readonly bool _isTestChannel;
    private SavedLoginRecord? _savedLogin;

    public bool ChannelSwitchRequested { get; private set; }

    internal LoginWindow(
        IReadOnlyList<LoginProviderRuntimeConfig> providers,
        string applicationDirectory,
        LauncherAppearanceRuntimeConfig appearance,
        string installationDirectory,
        bool testChannelAvailable,
        bool isTestChannel)
    {
        _installationDirectory = installationDirectory;
        _isTestChannel = isTestChannel;
        _loginStore = new SecureLoginStore(applicationDirectory);
        InitializeComponent();
        ApplyLauncherIcon();
        ApplyAppearance(applicationDirectory, appearance);
        ChannelSwitchButton.Visibility = testChannelAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChannelSwitchButton.Content = isTestChannel
            ? "切换到正式版"
            : "切换到测试版";
        ChannelBadgeText.Text = isTestChannel
            ? "测试通道"
            : "正式通道";
        ProviderBox.ItemsSource = providers;

        _savedLogin = _loginStore.Load();
        var savedProvider = _savedLogin is null
            ? null
            : providers.FirstOrDefault(
                provider => CreateProviderKey(provider).Equals(
                    _savedLogin.ProviderKey,
                    StringComparison.Ordinal));
        if (_savedLogin is not null && savedProvider is null)
        {
            try
            {
                _loginStore.Delete();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                ShowError($"旧登录方式已不可用，但无法清除其保存信息：{exception.Message}");
            }

            _savedLogin = null;
        }

        ProviderBox.SelectedItem =
            savedProvider ??
            providers.FirstOrDefault(provider => provider.IsDefault) ??
            providers.FirstOrDefault();
        if (_savedLogin is not null)
        {
            UsernameBox.Text = _savedLogin.Username;
            RememberLoginCheckBox.IsChecked = true;
        }

        UpdateProviderUi();
    }

    private void ApplyLauncherIcon()
    {
        var icon = LauncherIconService.TryLoadExecutableIcon();
        if (icon is null)
        {
            return;
        }

        Icon = icon;
        LauncherIconImage.Source = icon;
        LauncherIconFallback.Visibility = Visibility.Collapsed;
    }

    private void ChannelSwitch_Click(object sender, RoutedEventArgs e)
    {
        LauncherChannelService.SelectTestChannel(
            _installationDirectory,
            !_isTestChannel);
        ChannelSwitchRequested = true;
        DialogResult = false;
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void ToggleMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
            return;
        }

        SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeWindowGlyph is null || RestoreWindowGlyph is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeWindowGlyph.Visibility =
            maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreWindowGlyph.Visibility =
            maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeWindowButton.ToolTip =
            maximized ? "还原" : "最大化";
        UpdateWindowFrame(maximized);
    }

    private void WindowFrame_SizeChanged(
        object sender,
        SizeChangedEventArgs e) =>
        UpdateWindowFrame(WindowState == WindowState.Maximized);

    private void UpdateWindowFrame(bool maximized)
    {
        const double normalRadius = 12;
        WindowFrame.CornerRadius =
            new CornerRadius(maximized ? 0 : normalRadius);
        WindowFrame.Padding = new Thickness(0);
        WindowSurface.CornerRadius =
            new CornerRadius(maximized ? 0 : normalRadius);

        if (maximized ||
            WindowSurface.ActualWidth <= 0 ||
            WindowSurface.ActualHeight <= 0)
        {
            BackgroundRoot.Clip = null;
            return;
        }

        BackgroundRoot.Clip = new RectangleGeometry(
            new Rect(
                0,
                0,
                WindowSurface.ActualWidth,
                WindowSurface.ActualHeight),
            normalRadius,
            normalRadius);
    }

    private void ApplyAppearance(
        string applicationDirectory,
        LauncherAppearanceRuntimeConfig appearance)
    {
        var title = string.IsNullOrWhiteSpace(appearance.WindowTitle)
            ? "Minecraft 登录"
            : appearance.WindowTitle.Trim();
        Title = title;
        LauncherTitleText.Text = title;

        if (string.IsNullOrWhiteSpace(appearance.BackgroundImage))
        {
            return;
        }

        try
        {
            var applicationRoot =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(applicationDirectory)) +
                Path.DirectorySeparatorChar;
            var imagePath = Path.GetFullPath(
                Path.Combine(applicationRoot, appearance.BackgroundImage));
            if (!imagePath.StartsWith(
                    applicationRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(imagePath))
            {
                return;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(imagePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            BackgroundRoot.Background = new ImageBrush(image)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            NotSupportedException or UriFormatException or
            ArgumentException or InvalidOperationException or FormatException)
        {
            Debug.WriteLine($"无法加载启动器背景图片：{exception.Message}");
        }
    }

    internal LoginSession? Session { get; private set; }
    private LoginProviderRuntimeConfig? SelectedProvider => ProviderBox.SelectedItem as LoginProviderRuntimeConfig;

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateProviderUiWhenReady();
    private void UsernameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateProviderUiWhenReady();
    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e) =>
        UpdateProviderUiWhenReady();
    private void RememberLogin_Click(object sender, RoutedEventArgs e) =>
        UpdateProviderUiWhenReady();

    private void UpdateProviderUiWhenReady()
    {
        if (IsInitialized)
        {
            UpdateProviderUi();
        }
    }

    private void UpdateProviderUi()
    {
        var type = SelectedProvider?.Type ?? "";
        PasswordInput.IsEnabled = !type.Equals("Offline", StringComparison.OrdinalIgnoreCase);
        RememberLoginCheckBox.IsEnabled =
            !type.Equals("Microsoft", StringComparison.OrdinalIgnoreCase);
        ClearSavedLoginButton.Visibility = _savedLogin is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        RegisterButton.Visibility = type.Equals("UnifiedPassport", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (CanUseSavedLogin())
        {
            HintText.Text =
                "已找到本机 AES 加密保存的登录令牌。启动器会先验证令牌，必要时自动刷新；不会保存密码。";
            LoginButton.Content = "使用已保存会话登录并启动";
        }
        else
        {
            HintText.Text = type.Equals("Offline", StringComparison.OrdinalIgnoreCase)
                ? "离线登录必须由你主动选择。用户名不会上传。"
                : type.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
                    ? "Microsoft OAuth 登录尚未实现，本版本不会伪装成正版登录。"
                    : "";
            LoginButton.Content = "登录并启动";
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        var username = UsernameBox.Text.Trim();
        if (provider is null || string.IsNullOrEmpty(username))
        {
            ShowError("请选择登录方式并输入账号或离线游戏名。");
            return;
        }

        if (string.Equals(provider.Type, "Offline", StringComparison.OrdinalIgnoreCase))
        {
            Session = LoginSession.CreateOffline(username);
            PersistLoginChoice(provider, Session);
            DialogResult = true;
            return;
        }

        if (string.Equals(provider.Type, "Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Microsoft OAuth 登录尚未实现，请选择其他已配置的登录方式。");
            return;
        }

        LoginButton.IsEnabled = false;
        try
        {
            if (CanUseSavedLogin())
            {
                Session = await TryReuseSavedSessionAsync(provider);
                if (Session is null)
                {
                    _loginStore.Delete();
                    _savedLogin = null;
                    UpdateProviderUi();
                    ShowError("已保存的登录会话已失效，请重新输入密码登录。");
                    PasswordInput.Focus();
                    return;
                }

                PersistLoginChoice(provider, Session);
                DialogResult = true;
                return;
            }

            if (string.IsNullOrEmpty(PasswordInput.Password))
            {
                ShowError("请输入密码。");
                return;
            }

            Session = await AuthenticateYggdrasilAsync(
                provider,
                username,
                PasswordInput.Password);
            PasswordInput.Clear();
            PersistLoginChoice(provider, Session);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            PasswordInput.Clear();
            ShowError($"登录失败：{DescribeException(exception)}");
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private bool CanUseSavedLogin()
    {
        var provider = SelectedProvider;
        return _savedLogin is not null &&
               provider is not null &&
               RememberLoginCheckBox.IsChecked == true &&
               string.IsNullOrEmpty(PasswordInput.Password) &&
               UsernameBox.Text.Trim().Equals(_savedLogin.Username, StringComparison.Ordinal) &&
               CreateProviderKey(provider).Equals(_savedLogin.ProviderKey, StringComparison.Ordinal);
    }

    private async Task<LoginSession?> TryReuseSavedSessionAsync(
        LoginProviderRuntimeConfig provider)
    {
        var saved = _savedLogin ?? throw new InvalidOperationException("没有可复用的登录会话。");
        var apiRoot = await ResolveApiRootAsync(provider);

        using var validateResponse = await HttpClient.PostAsJsonAsync(
            RequireYggdrasilEndpoint(apiRoot, "authserver/validate"),
            new
            {
                accessToken = saved.AccessToken,
                clientToken = saved.ClientId
            });
        if (validateResponse.IsSuccessStatusCode)
        {
            return ToSession(saved);
        }

        if ((int)validateResponse.StatusCode >= 500)
        {
            throw new InvalidOperationException(
                $"认证服务器暂时无法验证已保存会话（HTTP {(int)validateResponse.StatusCode}）。");
        }

        using var refreshResponse = await HttpClient.PostAsJsonAsync(
            RequireYggdrasilEndpoint(apiRoot, "authserver/refresh"),
            new
            {
                accessToken = saved.AccessToken,
                clientToken = saved.ClientId,
                requestUser = true
            });
        var json = await refreshResponse.Content.ReadAsStringAsync();
        if (!refreshResponse.IsSuccessStatusCode)
        {
            if ((int)refreshResponse.StatusCode is 400 or 401 or 403)
            {
                return null;
            }

            throw new InvalidOperationException(
                TryReadError(json) is { Length: > 0 } error
                    ? error
                    : $"认证服务器刷新会话失败（HTTP {(int)refreshResponse.StatusCode}）。");
        }

        using var document = ParseJsonResponse(refreshResponse, json, "认证服务器");
        return ReadLoginSession(document.RootElement, saved.ClientId);
    }

    private static async Task<LoginSession> AuthenticateYggdrasilAsync(
        LoginProviderRuntimeConfig provider,
        string username,
        string password)
    {
        var apiRoot = await ResolveApiRootAsync(provider);
        var endpoint = RequireYggdrasilEndpoint(apiRoot, "authserver/authenticate");

        using var response = await HttpClient.PostAsJsonAsync(endpoint, new
        {
            agent = new { name = "Minecraft", version = 1 },
            username,
            password,
            clientToken = Guid.NewGuid().ToString(),
            requestUser = true
        });
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(TryReadError(json) is { Length: > 0 } error
                ? error
                : $"认证服务器返回 HTTP {(int)response.StatusCode}。");

        using var document = ParseJsonResponse(response, json, "认证服务器");
        return ReadLoginSession(document.RootElement);
    }

    private static async Task<string> ResolveApiRootAsync(
        LoginProviderRuntimeConfig provider) =>
        string.Equals(provider.Type, "UnifiedPassport", StringComparison.OrdinalIgnoreCase)
            ? await ResolveUnifiedPassportApiRootAsync(provider)
            : string.IsNullOrWhiteSpace(provider.ApiUrl)
                ? provider.ServerUrl ?? ""
                : provider.ApiUrl;

    private static Uri RequireYggdrasilEndpoint(string apiRoot, string relativePath)
    {
        if (!Uri.TryCreate(Combine(apiRoot, relativePath), UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("认证服务器必须是有效的 HTTPS Yggdrasil 地址。");
        }

        return endpoint;
    }

    private static LoginSession ReadLoginSession(
        JsonElement root,
        string fallbackClientToken = "0")
    {
        if (!root.TryGetProperty("selectedProfile", out var profile))
        {
            throw new InvalidDataException("账号没有可用的 Minecraft 角色。");
        }

        return new(
            Required(profile, "name"),
            Required(profile, "id"),
            Required(root, "accessToken"),
            root.TryGetProperty("clientToken", out var clientToken)
                ? clientToken.GetString() ?? fallbackClientToken
                : fallbackClientToken,
            "mojang",
            "0");
    }

    private static async Task<string> ResolveUnifiedPassportApiRootAsync(
        LoginProviderRuntimeConfig provider)
    {
        if (string.IsNullOrWhiteSpace(provider.ServerIdentifier))
        {
            throw new InvalidDataException("统一通行证服务器标识不能为空。");
        }

        var configurationBase = NormalizeUnifiedPassportConfigurationBase(provider.ServerUrl);
        var configurationUrl = Combine(configurationBase, provider.ServerIdentifier) + "/";
        if (!Uri.TryCreate(configurationUrl, UriKind.Absolute, out var configurationUri) ||
            configurationUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("统一通行证配置地址必须使用 HTTPS。");

        using var response = await HttpClient.GetAsync(configurationUri);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(TryReadError(json) is { Length: > 0 } error
                ? error
                : $"统一通行证配置接口返回 HTTP {(int)response.StatusCode}。");

        using var document = ParseJsonResponse(response, json, "统一通行证配置接口");
        if (!document.RootElement.TryGetProperty("apiRoot", out var apiRootProperty) ||
            !Uri.TryCreate(apiRootProperty.GetString(), UriKind.Absolute, out var apiRoot) ||
            apiRoot.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("统一通行证配置未返回有效的 HTTPS apiRoot。");
        return apiRoot.ToString();
    }

    private static string NormalizeUnifiedPassportConfigurationBase(string configuredUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            return "https://auth.mc-user.com:233/";
        }

        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configuredUri) ||
            configuredUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("统一通行证配置地址必须使用 HTTPS。");
        }

        if (!configuredUri.Host.Equals("login.mc-user.com", StringComparison.OrdinalIgnoreCase))
        {
            return configuredUri.ToString();
        }

        var corrected = new UriBuilder(configuredUri)
        {
            Host = "auth.mc-user.com",
            Path = "/",
            Query = "",
            Fragment = ""
        };
        return corrected.Uri.ToString();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCCPBuilder-Launcher/1.0");
        return client;
    }

    private static JsonDocument ParseJsonResponse(
        HttpResponseMessage response,
        string json,
        string stage)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "未知";
            throw new InvalidDataException(
                $"{stage}返回了非 JSON 数据（HTTP {(int)response.StatusCode}，Content-Type: {contentType}）。" +
                "请稍后重试或检查认证服务器状态。",
                exception);
        }
    }

    private static string Combine(string root, string child) =>
        (root ?? "").TrimEnd('/') + "/" + (child ?? "").Trim('/');

    private static string Required(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"认证响应缺少 {property}。");
        return value.GetString()!;
    }

    private static string TryReadError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("errorMessage", out var message))
                return message.GetString() ?? "";
            if (document.RootElement.TryGetProperty("error", out var error))
                return error.GetString() ?? "";
        }
        catch (JsonException)
        {
        }
        return "";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PasswordInput.Clear();
        DialogResult = false;
    }

    private void ClearSavedLogin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _loginStore.Delete();
            _savedLogin = null;
            RememberLoginCheckBox.IsChecked = false;
            UpdateProviderUi();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            ShowError($"无法清除已保存的登录信息：{exception.Message}");
        }
    }

    private void PersistLoginChoice(
        LoginProviderRuntimeConfig provider,
        LoginSession session)
    {
        if (RememberLoginCheckBox.IsChecked != true)
        {
            try
            {
                _loginStore.Delete();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(
                    this,
                    $"登录成功，但无法清除之前保存的登录信息：{exception.Message}\n" +
                    "请点击“清除已保存的登录信息”后重试。",
                    "清除登录信息",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            _savedLogin = null;
            return;
        }

        var record = new SavedLoginRecord(
            1,
            CreateProviderKey(provider),
            session.Username,
            session.Uuid,
            session.AccessToken,
            session.ClientId,
            session.UserType,
            session.Xuid,
            DateTimeOffset.UtcNow);
        try
        {
            _loginStore.Save(record);
            _savedLogin = record;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or CryptographicException or
            System.Security.SecurityException)
        {
            _savedLogin = null;
            MessageBox.Show(
                this,
                $"登录成功，但保存登录信息失败：{exception.Message}\n游戏仍将正常启动。",
                "保存登录信息",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static LoginSession ToSession(SavedLoginRecord saved) =>
        new(
            saved.Username,
            saved.Uuid,
            saved.AccessToken,
            saved.ClientId,
            saved.UserType,
            saved.Xuid);

    private static string CreateProviderKey(LoginProviderRuntimeConfig provider) =>
        string.Join(
            "|",
            provider.Type?.Trim() ?? "",
            provider.ServerUrl?.Trim() ?? "",
            provider.ApiUrl?.Trim() ?? "",
            provider.ServerIdentifier?.Trim() ?? "");

    private static string DescribeException(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return string.Equals(current.Message, exception.Message, StringComparison.Ordinal)
            ? exception.Message
            : $"{exception.Message}；具体原因：{current.Message}";
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        if (provider is null ||
            !string.Equals(provider.Type, "UnifiedPassport", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(provider.ServerIdentifier))
            return;

        var address =
            "https://login.mc-user.com:233/" +
            Uri.EscapeDataString(provider.ServerIdentifier) +
            "/loginreg";
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "登录", MessageBoxButton.OK, MessageBoxImage.Warning);
}

internal sealed record LoginSession(
    string Username,
    string Uuid,
    string AccessToken,
    string ClientId,
    string UserType,
    string Xuid)
{
    public static LoginSession CreateOffline(string username)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        return new(username, Convert.ToHexString(bytes).ToLowerInvariant(), "0", "0", "legacy", "0");
    }
}
