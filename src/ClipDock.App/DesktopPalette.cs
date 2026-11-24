namespace ClipDock.App;

// Shared by the WPF and Avalonia resource dictionaries.
internal static class DesktopPalette
{
    internal static readonly (string Key, string Light, string Dark)[] Brushes =
    [
        ("Accent", "#28734B", "#86D6A4"),
        ("OnAccent", "#FFFFFF", "#102B1B"),
        ("AccentHover", "#21623F", "#A0E5BA"),
        ("AccentPressed", "#1B5134", "#6EBE8C"),
        ("AccentSoft", "#E9F3ED", "#233C2D"),
        ("AccentLine", "#C9E0D1", "#3C624A"),
        ("Ink", "#1B2620", "#E8EEE9"),
        ("Muted", "#5B6862", "#B7C3BB"),
        ("Faint", "#6C7871", "#9DAEA3"),
        ("Page", "#F4F6F5", "#151A17"),
        ("Surface", "#FFFFFF", "#202722"),
        ("Line", "#E3E8E5", "#39433C"),
        ("Field", "#CDD5D0", "#59675E"),
        ("FieldHover", "#A9B4AE", "#85958B"),
        ("Hover", "#F0F3F1", "#303B34"),
        ("Pressed", "#E4E9E6", "#3C4941"),
        ("Track", "#E4EAE6", "#36433B"),
        ("RowHover", "#FAFBFA", "#29322C"),
        ("Scrim", "#401B2620", "#80000000"),
        ("Danger", "#B42318", "#FFACA3"),
        ("DangerSoft", "#FCEFEE", "#412926"),
        ("DangerLine", "#F1C8C4", "#744740"),
        ("DangerInk", "#7A1C14", "#FFD4CE"),
        ("WarnSoft", "#FFF7E6", "#393120"),
        ("WarnLine", "#EED6A0", "#6C5833"),
        ("WarnInk", "#6B4700", "#F4D594")
    ];
}
