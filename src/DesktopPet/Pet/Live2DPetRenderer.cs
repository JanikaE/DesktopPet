using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Drawing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DesktopPet.Pet;

public sealed class Live2DPetRenderer : IPetRenderer
{
    private const string RuntimeHost = "desktop-pet-runtime.local";
    private const string ModelHost = "desktop-pet-model.local";
    private readonly WebView2CompositionControl _view;
    private readonly PetDefinition _definition;
    private readonly string _runtimeDirectory;
    private readonly TaskCompletionSource _readySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _pointerTimer;
    private CoreWebView2? _core;
    private PetState _pendingState = PetState.Idle;
    private double _lastPointerX = double.NaN;
    private double _lastPointerY = double.NaN;
    private bool _ready;
    private bool _disposed;

    public Live2DPetRenderer(PetDefinition definition)
    {
        _definition = definition;
        if (definition.Live2D is null) throw new InvalidDataException("Live2D 桌宠缺少渲染设置。");
        _runtimeDirectory = GetDefaultRuntimeDirectory();
        var missing = GetMissingRuntimeFiles(_runtimeDirectory);
        if (missing.Count > 0)
            throw new NotSupportedException($"Live2D 运行文件尚未安装：{string.Join("、", missing)}");

        _view = new WebView2CompositionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DefaultBackgroundColor = Color.Transparent,
            AllowExternalDrop = false,
            IsTabStop = false,
            Focusable = false
        };
        _view.Loaded += ViewLoaded;
        _pointerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _pointerTimer.Tick += TrackPointer;
    }

    public FrameworkElement View => _view;
    public Task Ready => _readySource.Task;
    public event Action<string>? Failed;

    public void Show(PetState state)
    {
        _pendingState = state;
        if (_ready) PostState(state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readySource.TrySetCanceled();
        _pointerTimer.Stop();
        _pointerTimer.Tick -= TrackPointer;
        _view.Loaded -= ViewLoaded;
        if (_core is not null)
        {
            _core.WebMessageReceived -= WebMessageReceived;
            _core.ProcessFailed -= ProcessFailed;
            _core = null;
        }
        try { _view.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    public static IReadOnlyList<string> GetMissingRuntimeFiles(string? runtimeDirectory = null)
    {
        runtimeDirectory ??= GetDefaultRuntimeDirectory();
        var required = new[] { "renderer.html", "renderer-host.js", "live2dcubismcore.js", Path.Combine("modules", "src", "adapter.js") };
        return required.Where(file => !File.Exists(Path.Combine(runtimeDirectory, file))).ToArray();
    }

    private static string GetDefaultRuntimeDirectory() =>
        Path.Combine(Path.GetDirectoryName(typeof(Live2DPetRenderer).Assembly.Location)!, "Assets", "Live2D", "Runtime");

    private async void ViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || _view.CoreWebView2 is not null) return;
        try
        {
            await _view.EnsureCoreWebView2Async();
            if (_disposed) return;
            var core = _view.CoreWebView2 ?? throw new InvalidOperationException("WebView2 初始化后没有可用的 CoreWebView2。");
            _core = core;
            ConfigureWebView(core);
            core.SetVirtualHostNameToFolderMapping(RuntimeHost, _runtimeDirectory, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping(ModelHost, _definition.PackageDirectory, CoreWebView2HostResourceAccessKind.Allow);
            _view.Source = BuildRendererUri();
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            ReportFailure($"WebView2 初始化失败：{exception.Message}", exception);
        }
    }

    private void ConfigureWebView(CoreWebView2 core)
    {
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.NavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, RuntimeHost, StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
            {
                if (string.Equals(uri.Host, RuntimeHost, StringComparison.OrdinalIgnoreCase))
                {
                    args.Response = CreateRuntimeResponse(core, uri);
                    return;
                }
                if (string.Equals(uri.Host, ModelHost, StringComparison.OrdinalIgnoreCase)) return;
            }
            args.Response = core.Environment.CreateWebResourceResponse(Stream.Null, 403, "Forbidden", "Content-Type: text/plain");
        };
        core.WebResourceResponseReceived += (_, args) =>
        {
            if (args.Response.StatusCode >= 400 && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) &&
                (string.Equals(uri.Host, RuntimeHost, StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Host, ModelHost, StringComparison.OrdinalIgnoreCase)))
                ReportFailure($"Live2D 资源加载失败（HTTP {args.Response.StatusCode}）：{uri.AbsolutePath}");
        };
        core.WebMessageReceived += WebMessageReceived;
        core.ProcessFailed += ProcessFailed;
    }

    private CoreWebView2WebResourceResponse CreateRuntimeResponse(CoreWebView2 core, Uri uri)
    {
        var relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
        if (!Path.HasExtension(relative)) relative += ".js";
        var root = Path.GetFullPath(_runtimeDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
            return core.Environment.CreateWebResourceResponse(Stream.Null, 404, "Not Found", "Content-Type: text/plain");
        var contentType = Path.GetExtension(resolved).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            _ => "text/plain; charset=utf-8"
        };
        return core.Environment.CreateWebResourceResponse(
            File.Open(resolved, FileMode.Open, FileAccess.Read, FileShare.Read),
            200,
            "OK",
            $"Content-Type: {contentType}\r\nAccess-Control-Allow-Origin: *\r\nCross-Origin-Resource-Policy: cross-origin");
    }

    private Uri BuildRendererUri()
    {
        var live2D = _definition.Live2D!;
        var modelRelative = Path.GetRelativePath(_definition.PackageDirectory, live2D.ModelPath).Replace(Path.DirectorySeparatorChar, '/');
        var config = JsonSerializer.Serialize(new
        {
            modelUrl = $"https://{ModelHost}/{Uri.EscapeDataString(modelRelative).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}",
            scale = live2D.Scale,
            offsetX = live2D.OffsetX,
            offsetY = live2D.OffsetY,
            motions = live2D.Motions.ToDictionary(pair => pair.Key.ToString().ToLowerInvariant(), pair => pair.Value),
            pointerTracking = live2D.PointerTracking.Select(setting => new
            {
                parameterId = setting.ParameterId,
                impact = setting.Impact,
                reflect = setting.Reflect,
                source = setting.Source
            })
        });
        var encoded = Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(config)));
        return new Uri($"https://{RuntimeHost}/renderer.html?config={encoded}");
    }

    private void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!string.Equals(new Uri(e.Source).Host, RuntimeHost, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            if (!message.RootElement.TryGetProperty("type", out var type)) return;
            if (type.GetString() == "ready")
            {
                _ready = true;
                _readySource.TrySetResult();
                PostState(_pendingState);
                TrackPointer(this, EventArgs.Empty);
                _pointerTimer.Start();
            }
            else if (type.GetString() == "fault")
            {
                var detail = message.RootElement.TryGetProperty("message", out var messageProperty)
                    ? messageProperty.GetString()
                    : null;
                ReportFailure(string.IsNullOrWhiteSpace(detail) ? "Live2D 渲染页加载失败。" : detail);
            }
        }
        catch (JsonException) { }
    }

    private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _ready = false;
        ReportFailure($"Live2D WebView2 进程异常退出：{e.ProcessFailedKind}");
    }

    private void ReportFailure(string message, Exception? exception = null)
    {
        _ready = false;
        var failure = exception ?? new InvalidOperationException(message);
        _readySource.TrySetException(failure);
        System.Diagnostics.Debug.WriteLine($"Live2D renderer failed: {message}\n{exception}");
        Failed?.Invoke(message);
    }

    private void PostState(PetState state)
    {
        if (_disposed || _core is null) return;
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", state = state.ToString().ToLowerInvariant() }));
    }

    private void TrackPointer(object? sender, EventArgs e)
    {
        if (_disposed || !_ready || _core is null || !_view.IsVisible || _view.ActualWidth <= 0 || _view.ActualHeight <= 0) return;
        var cursor = Forms.Cursor.Position;
        var local = _view.PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        // Match Cubism's device-to-screen transform: both axes use the canvas height
        // as their unit, so a portrait canvas has a narrower horizontal range.
        var deviceToScreenScale = 2 / _view.ActualHeight;
        var x = Math.Clamp((local.X - _view.ActualWidth / 2) * deviceToScreenScale, -1, 1);
        var y = Math.Clamp((_view.ActualHeight / 2 - local.Y) * deviceToScreenScale, -1, 1);
        if (Math.Abs(x - _lastPointerX) < 0.002 && Math.Abs(y - _lastPointerY) < 0.002) return;
        _lastPointerX = x;
        _lastPointerY = y;
        try
        {
            _core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "pointer", x, y }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            _pointerTimer.Stop();
        }
    }
}
