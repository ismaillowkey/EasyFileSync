using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace EasyFileSync.App.WebUi;

/// <summary>
/// Hosts a WebView2 control inside Avalonia via a native child HWND.
/// Uses the raw Core API (no WinForms/WPF dependency).
/// </summary>
public sealed class WebViewHost : NativeControlHost
{
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private IntPtr _parentHwnd;
    private string? _pendingUrl;
    private string? _pendingHtml;

    public event Action<string>? MessageReceived;

    public string? WebRoot { get; set; }
    public string VirtualHostName { get; set; } = "EasyFileSync.local";
    public string StartPage { get; set; } = "index.html";

    public bool IsReady => _core is not null;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _parentHwnd = handle.Handle;

        // A plain child window is enough: WebView2 creates its own child HWND.
        _ = InitializeAsync(_parentHwnd);
        return handle;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateBounds();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateBounds();
    }

    private void UpdateBounds()
    {
        if (_controller is null) return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var size = Bounds.Size;

        _controller.Bounds = new System.Drawing.Rectangle(
            0, 0,
            Math.Max(1, (int)Math.Round(size.Width * scaling)),
            Math.Max(1, (int)Math.Round(size.Height * scaling)));
        _controller.RasterizationScale = (float)scaling;
        _controller.ShouldDetectMonitorScaleChanges = true;
    }

    private async Task InitializeAsync(IntPtr hwnd)
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyFileSync", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
            _core = _controller.CoreWebView2;

            var s = _core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsSwipeNavigationEnabled = false;

            _core.WebMessageReceived += (_, args) =>
            {
                try { MessageReceived?.Invoke(args.WebMessageAsJson); }
                catch { }
            };

            // Keep everything inside the app.
            _core.NewWindowRequested += (_, args) => args.Handled = true;
            _core.NavigationStarting += (_, args) =>
            {
                if (args.Uri.StartsWith("https://" + VirtualHostName, StringComparison.OrdinalIgnoreCase)) return;
                if (args.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
                if (args.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
                args.Cancel = true;
            };

            if (!string.IsNullOrEmpty(WebRoot) && Directory.Exists(WebRoot))
            {
                _core.SetVirtualHostNameToFolderMapping(
                    VirtualHostName, WebRoot, CoreWebView2HostResourceAccessKind.Allow);
                // Disable caching so UI edits are picked up immediately.
                try
                {
                    var cacheSettings = _core.Profile.PreferredColorScheme;
                }
                catch { }

                var bust = DateTime.UtcNow.Ticks;
                _core.Navigate($"https://{VirtualHostName}/{StartPage}?v={bust}");
            }
            else if (_pendingHtml is not null)
            {
                _core.NavigateToString(_pendingHtml);
            }
            else if (_pendingUrl is not null)
            {
                _core.Navigate(_pendingUrl);
            }

            UpdateBounds();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("WebView2 init failed: " + ex);
        }
    }

    public void PostJson(string json)
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(json); }
        catch { }
    }

    public void Navigate(string url)
    {
        if (_core is null) { _pendingUrl = url; return; }
        _core.Navigate(url);
    }

    public void LoadHtml(string html)
    {
        if (_core is null) { _pendingHtml = html; return; }
        _core.NavigateToString(html);
    }

    public async Task<string?> EvalAsync(string js)
    {
        if (_core is null) return null;
        try
        {
            var r = await _core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                System.Text.Json.JsonSerializer.Serialize(new { expression = js, returnByValue = true }));
            return r;
        }
        catch (Exception ex) { return "ERR: " + ex.Message; }
    }

    public void ShowDevTools()
    {
        if (_core is null) return;
        _core.OpenDevToolsWindow();
    }
}