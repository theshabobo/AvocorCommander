namespace AvocorCommander.Models;

public sealed class PanelScene
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = "Scene";
    public string Description { get; set; } = "";
    public int    SortOrder   { get; set; }
}

public sealed class PanelPage
{
    public int    Id        { get; set; }
    public int    SceneId   { get; set; }
    public string Name      { get; set; } = "Page";
    public int    SortOrder { get; set; }
}

public sealed class PanelButton
{
    public int    Id         { get; set; }
    public int?   PageId     { get; set; }   // null  → bottom-bar button
    public int?   SceneId    { get; set; }   // set   → bottom-bar (scene-scoped)
    public string ButtonType { get; set; } = "grid"; // "grid" | "bottom"
    public string Label      { get; set; } = "";
    public string Icon       { get; set; } = "▶";
    public string Color      { get; set; } = "#3A7BD5";
    public bool   IsToggle   { get; set; }
    public int    GridRow    { get; set; }
    public int    GridCol    { get; set; }
    public int    SortOrder  { get; set; }
}

public sealed class PanelButtonAction
{
    public int    Id          { get; set; }
    public int    ButtonId    { get; set; }
    public int    Phase       { get; set; }  // 0 = A, 1 = B
    public int    DeviceId    { get; set; }
    public string DeviceName  { get; set; } = "";
    public string CommandCode { get; set; } = "";
    public string CommandName { get; set; } = "";
    public string CommandFormat { get; set; } = "HEX";
    public int    SortOrder   { get; set; }
}

/// <summary>Pre-defined icon set for panel buttons.</summary>
public static class PanelIcons
{
    public static readonly List<(string Name, string Symbol)> All = new()
    {
        ("Power",        "⏻"),
        ("Play",         "▶"),
        ("Pause",        "⏸"),
        ("Stop",         "⏹"),
        ("Volume Up",    "🔊"),
        ("Volume Down",  "🔉"),
        ("Mute",         "🔇"),
        ("HDMI",         "⬛"),
        ("Display PC",   "🖥"),
        ("Brightness",   "☀"),
        ("Contrast",     "◑"),
        ("Home",         "⌂"),
        ("Settings",     "⚙"),
        ("Input",        "↵"),
        ("Screen",       "▣"),
        ("Refresh",      "↻"),
        ("Up",           "▲"),
        ("Down",         "▼"),
        ("Left",         "◀"),
        ("Right",        "▶"),
        ("OK",           "✔"),
        ("Back",         "↩"),
        ("Menu",         "☰"),
        ("Info",         "ℹ"),
        ("Freeze",       "❄"),
        ("Blank",        "◻"),
        ("Aspect",       "⊡"),
        ("Zoom",         "⊕"),
        ("Split",        "⊟"),
        ("Lock",         "🔒"),
        ("Star",         "★"),
        ("Lightning",    "⚡"),
    };
}

/// <summary>Pre-defined colour swatches for panel buttons.</summary>
public static class PanelColors
{
    public static readonly List<string> All = new()
    {
        "#3A7BD5", // Blue
        "#16C080", // Green
        "#E74C3C", // Red
        "#F39C12", // Orange
        "#9B59B6", // Purple
        "#1ABC9C", // Teal
        "#E67E22", // Amber
        "#2ECC71", // Emerald
        "#E91E63", // Pink
        "#00BCD4", // Cyan
        "#607D8B", // Steel
        "#FF5722", // Deep orange
        "#795548", // Brown
        "#4CAF50", // Leaf green
        "#FF9800", // Yellow-orange
        "#3F51B5", // Indigo
    };
}
