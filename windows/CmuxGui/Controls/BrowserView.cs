using System;
using CmuxGui.Input;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CmuxGui.Controls;

internal sealed class BrowserView : UserControl, IDisposable
{
    private readonly MuxRuntime _mux;
    private readonly WebView2 _webView = new();
    private readonly TextBox _address = new();
    private readonly Button _back = new();
    private readonly Button _forward = new();
    private readonly Button _reload = new();
    private readonly Button _go = new();
    private readonly ProgressRing _progress = new() { Width = 18, Height = 18 };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error };
    private string _browserId;
    private string _lastPersistedUrl;
    private string? _requestedUrl;
    private bool _native;
    private bool _ready;
    private bool _disposed;
    private readonly Func<int, bool, bool, bool> _handleAccelerator;

    public BrowserView(
        MuxRuntime mux,
        BrowserSnapshot snapshot,
        Func<int, bool, bool, bool> handleAccelerator)
    {
        _mux = mux;
        _handleAccelerator = handleAccelerator;
        _browserId = snapshot.Id;
        _lastPersistedUrl = snapshot.Url;
        _native = snapshot.Source == "native";

        _back.Content = new SymbolIcon(Symbol.Back);
        _forward.Content = new SymbolIcon(Symbol.Forward);
        _reload.Content = new SymbolIcon(Symbol.Refresh);
        _go.Content = new SymbolIcon(Symbol.Go);
        _back.Click += (_, _) => GoBack();
        _forward.Click += (_, _) => GoForward();
        _reload.Click += (_, _) => Reload();
        _go.Click += (_, _) => NavigateAddress();
        _address.KeyDown += OnAddressKeyDown;
        _address.KeyUp += OnAddressKeyUp;
        _webView.KeyDown += OnWebViewKeyDown;
        _webView.KeyUp += OnWebViewKeyUp;

        var toolbar = new Grid
        {
            ColumnSpacing = 4,
            Padding = new Thickness(8, 6, 8, 6),
        };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(_back);
        Grid.SetColumn(_forward, 1);
        toolbar.Children.Add(_forward);
        Grid.SetColumn(_reload, 2);
        toolbar.Children.Add(_reload);
        Grid.SetColumn(_progress, 3);
        toolbar.Children.Add(_progress);
        Grid.SetColumn(_address, 4);
        toolbar.Children.Add(_address);
        Grid.SetColumn(_go, 5);
        toolbar.Children.Add(_go);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(toolbar);
        Grid.SetRow(_error, 1);
        root.Children.Add(_error);
        Grid.SetRow(_webView, 2);
        root.Children.Add(_webView);
        Content = root;

        _webView.NavigationStarting += (_, args) =>
        {
            _requestedUrl = args.Uri;
            _address.Text = args.Uri;
            _progress.IsActive = true;
        };
        _webView.NavigationCompleted += (_, args) =>
        {
            _requestedUrl = null;
            _progress.IsActive = false;
            if (args.IsSuccess && _webView.Source is { } source)
            {
                _address.Text = source.AbsoluteUri;
                _error.IsOpen = false;
                if (_native
                    && !string.Equals(_lastPersistedUrl, source.AbsoluteUri, StringComparison.Ordinal)
                    && _mux.NavigateBrowser(_browserId, source.AbsoluteUri))
                {
                    _lastPersistedUrl = source.AbsoluteUri;
                }
            }
            else
            {
                _error.Message = args.WebErrorStatus.ToString();
                _error.IsOpen = true;
            }
            UpdateNavigationState();
        };
        Loaded += OnLoaded;
        Relocalize();
        Update(snapshot);
    }

    public string? DocumentTitle => _webView.CoreWebView2?.DocumentTitle;

    public event Action? DocumentTitleChanged;

    public void Update(BrowserSnapshot snapshot)
    {
        _browserId = snapshot.Id;
        _native = snapshot.Source == "native";
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            _error.Message = snapshot.Error;
            _error.IsOpen = true;
        }
        if (string.IsNullOrWhiteSpace(snapshot.Url))
        {
            return;
        }
        if (!_ready)
        {
            _address.Text = snapshot.Url;
            return;
        }
        if (!string.Equals(_lastPersistedUrl, snapshot.Url, StringComparison.Ordinal))
        {
            _lastPersistedUrl = snapshot.Url;
            _address.Text = snapshot.Url;
            if (!string.Equals(_requestedUrl, snapshot.Url, StringComparison.Ordinal)
                && !string.Equals(_webView.Source?.AbsoluteUri, snapshot.Url, StringComparison.Ordinal))
            {
                Navigate(snapshot.Url);
            }
        }
    }

    public void Relocalize()
    {
        _address.PlaceholderText = Loc.S("Browser_Address");
        _error.Title = Loc.S("Browser_Error");
        SetButtonName(_back, "Browser_Back");
        SetButtonName(_forward, "Browser_Forward");
        SetButtonName(_reload, "Browser_Reload");
        SetButtonName(_go, "Browser_Go");
    }

    private static void SetButtonName(Button button, string key)
    {
        var name = Loc.S(key);
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_ready || _disposed)
        {
            return;
        }
        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.DocumentTitleChanged += OnDocumentTitleChanged;
            _ready = true;
            Navigate(_address.Text);
        }
        catch (Exception ex)
        {
            _error.Message = ex.Message;
            _error.IsOpen = true;
        }
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            NavigateAddress();
            args.Handled = true;
            return;
        }
        if (_handleAccelerator((int)args.Key, true, args.KeyStatus.WasKeyDown))
        {
            args.Handled = true;
        }
    }

    private void OnAddressKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (_handleAccelerator((int)args.Key, false, false))
        {
            args.Handled = true;
        }
    }

    private void OnWebViewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_handleAccelerator((int)args.Key, true, args.KeyStatus.WasKeyDown))
        {
            args.Handled = true;
        }
    }

    private void OnWebViewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (_handleAccelerator((int)args.Key, false, false))
        {
            args.Handled = true;
        }
    }

    internal bool HandleShortcut(ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.BrowserBack:
                GoBack();
                return true;
            case ShortcutAction.BrowserForward:
                GoForward();
                return true;
            case ShortcutAction.BrowserReload:
                Reload();
                return true;
            case ShortcutAction.BrowserFocusAddress:
                _address.Focus(FocusState.Keyboard);
                _address.SelectAll();
                return true;
            default:
                return false;
        }
    }

    private void NavigateAddress()
    {
        var value = _address.Text.Trim();
        if (value.Length == 0)
        {
            return;
        }
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = $"https://{value}";
        }
        Navigate(value);
    }

    private void Navigate(string value)
    {
        if (_ready && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            _requestedUrl = uri.AbsoluteUri;
            _webView.Source = uri;
        }
    }

    private void GoBack()
    {
        if (_webView.CoreWebView2?.CanGoBack == true)
        {
            _webView.CoreWebView2.GoBack();
        }
    }

    private void GoForward()
    {
        if (_webView.CoreWebView2?.CanGoForward == true)
        {
            _webView.CoreWebView2.GoForward();
        }
    }

    private void Reload()
    {
        _webView.CoreWebView2?.Reload();
    }

    private void UpdateNavigationState()
    {
        _back.IsEnabled = _webView.CoreWebView2?.CanGoBack == true;
        _forward.IsEnabled = _webView.CoreWebView2?.CanGoForward == true;
    }

    private void OnDocumentTitleChanged(object? sender, object args) =>
        DocumentTitleChanged?.Invoke();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Loaded -= OnLoaded;
        _address.KeyDown -= OnAddressKeyDown;
        _address.KeyUp -= OnAddressKeyUp;
        _webView.KeyDown -= OnWebViewKeyDown;
        _webView.KeyUp -= OnWebViewKeyUp;
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.DocumentTitleChanged -= OnDocumentTitleChanged;
        }
        _webView.Close();
        Content = null;
    }
}
