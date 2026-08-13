using System.Windows.Media;

namespace ClaudeSessions;

/// <summary>
/// The two colour sets, keyed by the brush names Theme.xaml declares. Applying one mutates each
/// named SolidColorBrush's Color in place — resource lookups return shared instances, so every
/// control that resolved the brush via StaticResource repaints without a DynamicResource anywhere.
/// </summary>
public static class ThemePalette
{
    public static readonly Dictionary<string, Color> Dark = Parse(new()
    {
        ["WindowBrush"] = "#FF15151A",
        ["PanelBrush"] = "#FF1B1B21",
        ["CardBrush"] = "#FF212129",
        ["BorderBrush2"] = "#FF2E2E38",
        ["TextBrush"] = "#FFE7E7EC",
        ["MutedBrush"] = "#FF9E9EAC",
        ["FaintBrush"] = "#FF80808F",
        ["UserBrush"] = "#FF7FA6FF",
        ["ClaudeBrush"] = "#FFD97757",
        ["ThinkBrush"] = "#FF9A8CC7",
        ["ToolBrush"] = "#FF5FB0A6",
        ["ErrorBrush"] = "#FFE8695F",
        ["LiveBrush"] = "#FF4ADE80",
        ["LinkBrush"] = "#FF7FA6FF",
        ["CodeBackgroundBrush"] = "#FF101015",
        ["MatchBrush"] = "#7FE8B84B",
        ["HoverBrush"] = "#FF272730",
        ["SelectedBrush"] = "#FF2F3140",
        ["UserBubbleBrush"] = "#FF1C2233",
        ["ThinkBgBrush"] = "#FF1A1823",
        ["ThinkBorderBrush"] = "#FF2A2536",
        ["ErrorBgBrush"] = "#FF251A1A",
        ["ErrorBorderBrush"] = "#FF5A2B2B",
        ["ScrollThumbBrush"] = "#FF3A3A46",
        ["ScrollThumbHoverBrush"] = "#FF52525F",
        ["ScrollThumbDragBrush"] = "#FF6A6A78",
        ["PillCheckedBorderBrush"] = "#FF4A4A5C",
        ["SearchFieldBrush"] = "#FF121216",
        ["FocusRingBrush"] = "#FF4E6BAF",
        ["CodeTextBrush"] = "#FFC9C9D4",
        ["TooltipBgBrush"] = "#FF2A2A33",
        ["ContextMenuBgBrush"] = "#FF23232B",
    });

    /// <summary>Warm paper. Accent hues match the dark set but are deepened to hold contrast on ivory.</summary>
    public static readonly Dictionary<string, Color> Light = Parse(new()
    {
        ["WindowBrush"] = "#FFFAF6EE",
        ["PanelBrush"] = "#FFF1E9DA",
        ["CardBrush"] = "#FFFFFFFF",
        ["BorderBrush2"] = "#FFE1D3B8",
        ["TextBrush"] = "#FF3A332B",
        ["MutedBrush"] = "#FF6E6152",
        ["FaintBrush"] = "#FF8C7A5C",
        ["UserBrush"] = "#FF2F5FB0",
        ["ClaudeBrush"] = "#FFB85A2E",
        ["ThinkBrush"] = "#FF6C55A6",
        ["ToolBrush"] = "#FF227B70",
        ["ErrorBrush"] = "#FFB8402F",
        ["LiveBrush"] = "#FF1E8E4F",
        ["LinkBrush"] = "#FF2F5FB0",
        ["CodeBackgroundBrush"] = "#FFF0E8D9",
        ["MatchBrush"] = "#7FE8B84B",
        ["HoverBrush"] = "#FFE7DAC2",
        ["SelectedBrush"] = "#FFE9DCC0",
        ["UserBubbleBrush"] = "#FFFFFFFF",
        ["ThinkBgBrush"] = "#FFF3EFFA",
        ["ThinkBorderBrush"] = "#FFDCD1EE",
        ["ErrorBgBrush"] = "#FFFBEAE5",
        ["ErrorBorderBrush"] = "#FFE3A99A",
        ["ScrollThumbBrush"] = "#FFD8C9A9",
        ["ScrollThumbHoverBrush"] = "#FFC7B491",
        ["ScrollThumbDragBrush"] = "#FFB7A17C",
        ["PillCheckedBorderBrush"] = "#FFC9B48C",
        ["SearchFieldBrush"] = "#FFE9DEC9",
        ["FocusRingBrush"] = "#FF2F5FB0",
        ["CodeTextBrush"] = "#FF4F4636",
        ["TooltipBgBrush"] = "#FFFDF8EC",
        ["ContextMenuBgBrush"] = "#FFFEFBF3",
    });

    private static Dictionary<string, Color> Parse(Dictionary<string, string> hex) =>
        hex.ToDictionary(kv => kv.Key, kv => (Color)ColorConverter.ConvertFromString(kv.Value)!);
}
