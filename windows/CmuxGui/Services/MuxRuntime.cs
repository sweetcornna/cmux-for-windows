using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    public static MuxRuntime Open()
    {
        var handle = CmuxNative.MuxOpen();
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The persistent cmux session could not be opened.");
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
        var bytes = Encoding.UTF8.GetBytes(name);
        var id = CmuxNative.MuxWorkspaceCreate(_handle, bytes, (nuint)bytes.Length);
        if (id == 0)
        {
            throw new InvalidOperationException("The cmux workspace could not be created.");
        }
        return Workspaces().Single(workspace => workspace.Id == id);
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

    public bool CreateTab(string pane)
    {
        var bytes = Encoding.UTF8.GetBytes(pane);
        return Check(
            CmuxNative.MuxPaneCreateTerminal(_handle, bytes, (nuint)bytes.Length),
            $"create tab in {pane}");
    }

    public bool SplitPane(string pane, bool down)
    {
        var bytes = Encoding.UTF8.GetBytes(pane);
        return Check(
            CmuxNative.MuxPaneSplit(
                _handle,
                bytes,
                (nuint)bytes.Length,
                down ? (byte)1 : (byte)0),
            $"split pane {pane}");
    }

    public bool FocusPane(string pane)
    {
        var bytes = Encoding.UTF8.GetBytes(pane);
        return CmuxNative.MuxPaneFocus(_handle, bytes, (nuint)bytes.Length) == 0;
    }

    public bool ClosePane(string pane)
    {
        var bytes = Encoding.UTF8.GetBytes(pane);
        return CmuxNative.MuxPaneClose(_handle, bytes, (nuint)bytes.Length) == 0;
    }

    public bool SelectTab(string tab)
    {
        var bytes = Encoding.UTF8.GetBytes(tab);
        return CmuxNative.MuxTabSelect(_handle, bytes, (nuint)bytes.Length) == 0;
    }

    public bool CloseTab(string tab)
    {
        var bytes = Encoding.UTF8.GetBytes(tab);
        return CmuxNative.MuxTabClose(_handle, bytes, (nuint)bytes.Length) == 0;
    }

    public bool SelectWorkspace(ulong workspace) =>
        CmuxNative.MuxWorkspaceSelect(_handle, workspace) == 0;

    public bool CloseWorkspace(ulong workspace) =>
        CmuxNative.MuxWorkspaceClose(_handle, workspace) == 0;

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
