namespace DesktopPet.UI;

internal static class KeyboardLayoutCatalog
{
    public const string FullSizeId = "full-size-104";
    public const string TenkeylessId = "tkl-87";

    private static readonly KeyboardKeySpec[][] MainRows =
    [
        [K("Esc", 27), S(1), K("F1", 112), K("F2", 113), K("F3", 114), K("F4", 115), S(.5), K("F5", 116), K("F6", 117), K("F7", 118), K("F8", 119), S(.5), K("F9", 120), K("F10", 121), K("F11", 122), K("F12", 123)],
        [K("`", 192), K("1", 49), K("2", 50), K("3", 51), K("4", 52), K("5", 53), K("6", 54), K("7", 55), K("8", 56), K("9", 57), K("0", 48), K("-", 189), K("=", 187), K("Back", 8, 2)],
        [K("Tab", 9, 1.5), K("Q", 81), K("W", 87), K("E", 69), K("R", 82), K("T", 84), K("Y", 89), K("U", 85), K("I", 73), K("O", 79), K("P", 80), K("[", 219), K("]", 221), K("\\", 220, 1.5)],
        [K("Caps", 20, 1.8), K("A", 65), K("S", 83), K("D", 68), K("F", 70), K("G", 71), K("H", 72), K("J", 74), K("K", 75), K("L", 76), K(";", 186), K("'", 222), K("Enter", 13, 2.2)],
        [K("Shift", 160, 2.3), K("Z", 90), K("X", 88), K("C", 67), K("V", 86), K("B", 66), K("N", 78), K("M", 77), K(",", 188), K(".", 190), K("/", 191), K("Shift", 161, 2.7)],
        [K("Ctrl", 162, 1.4), K("Win", 91, 1.2), K("Alt", 164, 1.2), K("Space", 32, 6.2), K("Alt", 165, 1.2), K("Win", 92, 1.2), K("Menu", 93, 1.2), K("Ctrl", 163, 1.4)]
    ];

    private static readonly KeyboardKeySpec[][] NavigationRows =
    [
        [K("Prt", 44), K("Scr", 145), K("Pause", 19)],
        [K("Ins", 45), K("Home", 36), K("PgUp", 33)],
        [K("Del", 46), K("End", 35), K("PgDn", 34)],
        [S(3)],
        [S(1), K("↑", 38), S(1)],
        [K("←", 37), K("↓", 40), K("→", 39)]
    ];

    private static readonly KeyboardKeySpec[][] NumpadRows =
    [
        [S(4)],
        [K("Num", 144), K("/", 111), K("*", 106), K("-", 109)],
        [K("7", 103), K("8", 104), K("9", 105), K("+", 107)],
        [K("4", 100), K("5", 101), K("6", 102), S(1)],
        [K("1", 97), K("2", 98), K("3", 99), K("Enter", 0x1000D)],
        [K("0", 96, 2), K(".", 110), S(1)]
    ];

    public static readonly KeyboardLayoutDefinition FullSize = new(
        FullSizeId,
        "104 键",
        [new(15, 0, MainRows), new(3, 7, NavigationRows), new(4, 7, NumpadRows)]);

    public static readonly KeyboardLayoutDefinition Tenkeyless = new(
        TenkeylessId,
        "87 键",
        [new(15, 0, MainRows), new(3, 7, NavigationRows)]);

    public static IReadOnlyList<KeyboardLayoutDefinition> All { get; } = [FullSize, Tenkeyless];

    public static KeyboardLayoutDefinition Find(string? id) =>
        All.FirstOrDefault(layout => string.Equals(layout.Id, id, StringComparison.Ordinal)) ?? FullSize;

    private static KeyboardKeySpec K(string label, int keyCode, double width = 1) => new(label, keyCode, width);
    private static KeyboardKeySpec S(double width) => new(string.Empty, null, width);
}

internal sealed record KeyboardLayoutDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<KeyboardSectionSpec> Sections);

internal sealed record KeyboardSectionSpec(
    double WidthInKeys,
    double GapBefore,
    IReadOnlyList<KeyboardKeySpec[]> Rows);

internal sealed record KeyboardKeySpec(string Label, int? KeyCode, double Width);
