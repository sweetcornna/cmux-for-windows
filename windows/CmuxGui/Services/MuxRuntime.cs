using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using CmuxGui.Interop;

namespace CmuxGui.Services;

internal sealed class MuxRuntime : IDisposable
{
    internal readonly record struct WorkspaceInfo(
        ulong Id,
        string PublicId,
        string Name,
        bool Active);

    private IntPtr _handle;

    private MuxRuntime(IntPtr handle)
    {
        _handle = handle;
    }

    public static MuxRuntime Open(string sessionName = "cmux-gui", bool persistent = true)
    {
        var name = Encoding.UTF8.GetBytes(sessionName);
        var handle = persistent
            ? CmuxNative.MuxOpenNamed(name, (nuint)name.Length)
            : CmuxNative.MuxOpenTransientNamed(name, (nuint)name.Length);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The cmux session could not be opened.");
        }
        return new MuxRuntime(handle);
    }

    public IReadOnlyList<WorkspaceInfo> Workspaces()
    {
        var count = CmuxNative.MuxWorkspaceCount(_handle);
        if (count < 0)
        {
            throw new InvalidOperationException("The cmux workspace list could not be read.");
        }
        var workspaces = new List<WorkspaceInfo>(count);
        for (var index = 0; index < count; index++)
        {
            if (CmuxNative.MuxWorkspaceGet(_handle, (nuint)index, out var workspace) < 0)
            {
                throw new InvalidOperationException("A cmux workspace could not be read.");
            }
            workspaces.Add(new WorkspaceInfo(
                workspace.Id,
                CmuxNative.PublicIdOf(workspace),
                CmuxNative.NameOf(workspace),
                workspace.Active != 0));
        }
        return workspaces;
    }

    public WorkspaceInfo CreateWorkspace(string name)
    {
        var result = ResourceRequest("workspace.create", new()
        {
            ["machine"] = "current",
            ["session"] = "current",
            ["name"] = name,
            ["initial_content"] = "empty",
        }, mutation: true);
        var publicId = result.GetProperty("value").GetProperty("workspace_id").GetString()
            ?? throw new InvalidOperationException("The cmux workspace response omitted its identity.");
        return Workspaces().Single(workspace => workspace.PublicId == publicId);
    }

    public MuxSnapshot Snapshot()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var required = CmuxNative.MuxSnapshotJson(_handle, Span<byte>.Empty, 0);
            if (required < 0)
            {
                throw new InvalidOperationException("The cmux topology snapshot could not be sized.");
            }
            var buffer = new byte[required];
            var written = CmuxNative.MuxSnapshotJson(_handle, buffer, (nuint)buffer.Length);
            if (written == CmuxNative.ErrorCapacity)
            {
                continue;
            }
            if (written < 0)
            {
                throw new InvalidOperationException("The cmux topology snapshot could not be read.");
            }
            return MuxSnapshot.Parse(buffer.AsSpan(0, written));
        }
        throw new InvalidOperationException("The cmux topology changed too quickly to snapshot.");
    }

    private JsonElement ResourceRequest(
        string operation,
        Dictionary<string, object?> parameters,
        bool mutation)
    {
        var requestId = $"gui-{Guid.NewGuid():N}";
        var envelope = new Dictionary<string, object?>
        {
            ["protocol"] = "cmux.protocol/2",
            ["type"] = "request",
            ["id"] = requestId,
            ["operation"] = operation,
            ["params"] = parameters,
        };
        if (mutation)
        {
            envelope["idempotency_key"] = requestId;
        }

        var request = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var status = CmuxNative.MuxResourceRequestJson(
            _handle,
            request,
            (nuint)request.Length,
            out var response);
        if (status < 0 || response == IntPtr.Zero)
        {
            throw new InvalidOperationException($"The cmux operation {operation} could not be executed.");
        }

        try
        {
            var required = CmuxNative.JsonResponseCopy(response, Span<byte>.Empty, 0);
            if (required < 0)
            {
                throw new InvalidOperationException($"The cmux operation {operation} response could not be sized.");
            }
            var bytes = new byte[required];
            var written = CmuxNative.JsonResponseCopy(response, bytes, (nuint)bytes.Length);
            if (written < 0)
            {
                throw new InvalidOperationException($"The cmux operation {operation} response could not be read.");
            }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, written));
            var root = document.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
            {
                var error = root.GetProperty("error");
                var code = error.GetProperty("code").GetString() ?? "operation.failed";
                var message = error.GetProperty("message").GetString() ?? operation;
                throw new InvalidOperationException($"{code}: {message}");
            }
            return root.GetProperty("result").Clone();
        }
        finally
        {
            CmuxNative.JsonResponseFree(response);
        }
    }

    private bool TryMutation(string operation, Dictionary<string, object?> parameters)
    {
        try
        {
            ResourceRequest(operation, parameters, mutation: true);
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"{operation} failed: {ex.Message}");
            return false;
        }
    }

    public IntPtr OpenWorkspace(ulong workspace, ushort cols, ushort rows, string? cwd)
    {
        var bytes = string.IsNullOrWhiteSpace(cwd) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(cwd);
        return CmuxNative.MuxWorkspaceOpen(
            _handle,
            workspace,
            cols,
            rows,
            bytes,
            (nuint)bytes.Length);
    }

    public IntPtr OpenTab(string tab, ushort cols, ushort rows)
    {
        var bytes = Encoding.UTF8.GetBytes(tab);
        return CmuxNative.MuxTabOpen(_handle, bytes, (nuint)bytes.Length, cols, rows);
    }

    public bool SetCellPixelSize(ushort widthPx, ushort heightPx) =>
        Check(CmuxNative.MuxSetCellPixelSize(_handle, widthPx, heightPx), "terminal cell pixels");

    public bool TryGetPresentation(out CmuxNative.Presentation presentation) =>
        CmuxNative.MuxPresentation(_handle, out presentation) == 0;

    public bool ApplyTerminalAppearance()
    {
        var settings = AppSettings.Current;
        var config = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.Theme))
        {
            var text = ThemeCatalog.Read(settings.Theme);
            if (text is not null)
            {
                config.AppendLine(text);
            }
            else
            {
                CmuxGui.Diag.Log($"theme '{settings.Theme}' not found");
            }
        }
        if (!string.IsNullOrWhiteSpace(settings.TerminalBackground))
        {
            config.AppendLine($"background = {settings.TerminalBackground}");
        }
        if (!string.IsNullOrWhiteSpace(settings.TerminalForeground))
        {
            config.AppendLine($"foreground = {settings.TerminalForeground}");
        }

        var bytes = Encoding.UTF8.GetBytes(config.ToString());
        return Check(
            CmuxNative.MuxApplyThemeText(_handle, bytes, (nuint)bytes.Length),
            "terminal appearance");
    }

    public bool CreateTerminal(string workspace, string? cwd = null)
    {
        var workspaceBytes = Encoding.UTF8.GetBytes(workspace);
        var cwdBytes = string.IsNullOrWhiteSpace(cwd) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(cwd);
        return CmuxNative.MuxWorkspaceCreateTerminal(
            _handle,
            workspaceBytes,
            (nuint)workspaceBytes.Length,
            cwdBytes,
            (nuint)cwdBytes.Length) == 0;
    }

    public bool CreateTab(string pane) => TryMutation("tab.create_terminal", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["pane"] = pane,
    });

    public bool CreateBrowser(string pane, string url) => TryMutation("tab.create_browser", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["pane"] = pane,
        ["url"] = url,
        ["backend"] = "native",
    });

    public bool CreateScreen(string workspace, string? name = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["machine"] = "current",
            ["session"] = "current",
            ["workspace"] = workspace,
        };
        if (!string.IsNullOrWhiteSpace(name))
        {
            parameters["name"] = name;
        }
        return TryMutation("screen.create", parameters);
    }

    public bool FocusScreen(string screen) => TryMutation("screen.focus", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["screen"] = screen,
    });

    public bool RenameScreen(string screen, string? name) => TryMutation("screen.rename", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["screen"] = screen,
        ["name"] = name,
    });

    public bool CloseScreen(string screen) => TryMutation("screen.close", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["screen"] = screen,
    });

    public bool SplitPane(string pane, string direction) => TryMutation("pane.split", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["pane"] = pane,
        ["direction"] = direction,
    });

    public bool RenamePane(string pane, string? name) => TryMutation("pane.rename", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["pane"] = pane,
        ["name"] = name,
    });

    public bool FocusPane(string pane) => TryMutation("pane.focus", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["pane"] = pane,
    });

    public bool FocusPaneDirection(string pane, string direction) =>
        TryMutation("pane.focus_direction", new()
        {
            ["machine"] = "current",
            ["session"] = "current",
            ["pane"] = pane,
            ["direction"] = direction,
        });

    public bool ZoomPane(string pane, bool? enabled = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["machine"] = "current",
            ["session"] = "current",
            ["pane"] = pane,
        };
        if (enabled is not null)
        {
            parameters["enabled"] = enabled;
        }
        return TryMutation("pane.zoom", parameters);
    }

    public bool SetSplitRatio(string pane, string split, double ratio) =>
        TryMutation("pane.split_ratio.set", new()
        {
            ["machine"] = "current",
            ["session"] = "current",
            ["pane"] = pane,
            ["split_id"] = split,
            ["ratio"] = ratio,
        });

    public bool SetViewportWidth(string pane, ushort columns) =>
        TryMutation("pane.viewport_width.set", new()
        {
            ["machine"] = "current",
            ["session"] = "current",
            ["pane"] = pane,
            ["columns"] = columns,
        });

    public bool ClosePane(string pane) => TryMutation("pane.close", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["pane"] = pane,
    });

    public bool SelectTab(string tab) => TryMutation("tab.focus", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["tab"] = tab,
    });

    public bool RenameTab(string tab, string? name) => TryMutation("tab.rename", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["tab"] = tab,
        ["name"] = name,
    });

    public bool MoveTab(
        string tab,
        string destinationWorkspace,
        string destinationScreen,
        string destinationPane,
        int index) => TryMutation("tab.move", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["tab"] = tab,
        ["destination_workspace"] = destinationWorkspace,
        ["destination_screen"] = destinationScreen,
        ["destination_pane"] = destinationPane,
        ["index"] = index,
    });

    public bool CloseTab(string tab) => TryMutation("tab.close", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["tab"] = tab,
    });

    public bool SelectWorkspace(string workspace) => TryMutation("workspace.focus", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["workspace"] = workspace,
    });

    public bool RenameWorkspace(string workspace, string name) =>
        TryMutation("workspace.rename", new()
        {
            ["machine"] = "current",
            ["session"] = "current",
            ["workspace"] = workspace,
            ["name"] = name,
        });

    public bool MoveWorkspace(string workspace, int index) => TryMutation("workspace.move", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["workspace"] = workspace,
        ["index"] = index,
    });

    public bool CloseWorkspace(string workspace) => TryMutation("workspace.close", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["workspace"] = workspace,
    });

    public bool NavigateBrowser(string browser, string url) => TryMutation("browser.navigate", new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["browser"] = browser,
        ["url"] = url,
    });

    public bool BrowserBack(string browser) => BrowserAction("browser.back", browser);

    public bool BrowserForward(string browser) => BrowserAction("browser.forward", browser);

    public bool ReloadBrowser(string browser) => BrowserAction("browser.reload", browser);

    private bool BrowserAction(string operation, string browser) => TryMutation(operation, new()
    {
        ["machine"] = "current",
        ["session"] = "current",
        ["browser"] = browser,
    });

    private bool Check(int result, string operation)
    {
        if (result == 0)
        {
            return true;
        }
        var required = CmuxNative.MuxLastError(_handle, Span<byte>.Empty, 0);
        var detail = "unknown engine error";
        if (required > 0)
        {
            var buffer = new byte[required];
            var written = CmuxNative.MuxLastError(_handle, buffer, (nuint)buffer.Length);
            if (written >= 0)
            {
                detail = Encoding.UTF8.GetString(buffer.AsSpan(0, written));
            }
        }
        CmuxGui.Diag.Log($"{operation} failed: {detail}");
        return false;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }
        CmuxNative.MuxFree(_handle);
        _handle = IntPtr.Zero;
    }
}
