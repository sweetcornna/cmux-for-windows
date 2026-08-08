using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CmuxGui.Services;

internal sealed class MuxSnapshot
{
    [JsonPropertyName("workspaces")]
    public List<WorkspaceSnapshot> Workspaces { get; init; } = [];

    [JsonPropertyName("screens")]
    public List<ScreenSnapshot> Screens { get; init; } = [];

    [JsonPropertyName("panes")]
    public List<PaneSnapshot> Panes { get; init; } = [];

    [JsonPropertyName("tabs")]
    public List<TabSnapshot> Tabs { get; init; } = [];

    [JsonPropertyName("terminals")]
    public List<TerminalSnapshot> Terminals { get; init; } = [];

    public static MuxSnapshot Parse(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<MuxSnapshot>(json)
        ?? throw new InvalidOperationException("The cmux topology snapshot was empty.");
}

internal sealed class WorkspaceSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("focused")]
    public bool Focused { get; init; }
}

internal sealed class ScreenSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("workspace_id")]
    public string WorkspaceId { get; init; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("focused")]
    public bool Focused { get; init; }

    [JsonPropertyName("layout")]
    public LayoutSnapshot Layout { get; init; } = new();
}

internal sealed class LayoutSnapshot
{
    [JsonPropertyName("active_pane_id")]
    public string? ActivePaneId { get; init; }

    [JsonPropertyName("zoomed_pane_id")]
    public string? ZoomedPaneId { get; init; }

    [JsonPropertyName("root")]
    public LayoutNodeSnapshot Root { get; init; } = new();
}

internal sealed class LayoutNodeSnapshot
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("tab_ids")]
    public List<string> TabIds { get; init; } = [];

    [JsonPropertyName("active_tab_id")]
    public string? ActiveTabId { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("ratio")]
    public double Ratio { get; init; } = 0.5;

    [JsonPropertyName("first")]
    public LayoutNodeSnapshot? First { get; init; }

    [JsonPropertyName("second")]
    public LayoutNodeSnapshot? Second { get; init; }

    [JsonPropertyName("pane_ids")]
    public List<string> PaneIds { get; init; } = [];

    [JsonPropertyName("expanded_pane_id")]
    public string? ExpandedPaneId { get; init; }

    [JsonPropertyName("columns")]
    public List<ViewportColumnSnapshot> Columns { get; init; } = [];
}

internal sealed class ViewportColumnSnapshot
{
    [JsonPropertyName("width")]
    public double Width { get; init; } = 1;

    [JsonPropertyName("root")]
    public LayoutNodeSnapshot Root { get; init; } = new();
}

internal sealed class PaneSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("screen_id")]
    public string ScreenId { get; init; } = string.Empty;

    [JsonPropertyName("focused")]
    public bool Focused { get; init; }
}

internal sealed class TabSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("pane_id")]
    public string PaneId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("focused")]
    public bool Focused { get; init; }

    [JsonPropertyName("content_kind")]
    public string ContentKind { get; init; } = string.Empty;

    [JsonPropertyName("content_id")]
    public string ContentId { get; init; } = string.Empty;
}

internal sealed class TerminalSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }
}
