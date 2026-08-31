using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Speech2Issues.Core.Configuration;

namespace Speech2Issues.App.Services;

public sealed record AppThemeInfo(string Id, string DisplayName, string IconUri, int RequiredCreatedTasks = 0);

public static class ThemeService
{
    public const string DefaultThemeId = "indigo";
    public const string AstolfoPinkThemeId = "astolfo-pink";
    public const int AstolfoUnlockTaskCount = ThemeUnlockPolicy.AstolfoRequiredCreatedTasks;

    private static IReadOnlyList<AppThemeInfo> AllThemes { get; } =
    [
        new(DefaultThemeId, "Индиго", "pack://application:,,,/Speech2Issues;component/Assets/Speech2Issues-icon.png"),
        new("graphite", "Графит", "pack://application:,,,/Speech2Issues;component/Assets/Speech2Issues-icon-graphite.png"),
        new("light", "Светлая", "pack://application:,,,/Speech2Issues;component/Assets/Speech2Issues-icon-light.png"),
        new("emerald", "Изумруд", "pack://application:,,,/Speech2Issues;component/Assets/Speech2Issues-icon-emerald.png"),
        new("amber", "Янтарь", "pack://application:,,,/Speech2Issues;component/Assets/Speech2Issues-icon-amber.png"),
        new(AstolfoPinkThemeId, "Розовая Astolfo", "pack://application:,,,/Speech2Issues;component/Assets/Speech2Issues-icon-astolfo-pink.png", AstolfoUnlockTaskCount),
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Palettes =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultThemeId] = Palette(
                "#0E1116", "#161A22", "#1C212B", "#242A36", "#2C3340",
                "#262C38", "#343C4B", "#E8ECF2", "#8B95A5",
                "#6366F1", "#7C83F5", "#5458D6", "#E5484D", "#F25B5F", "#30C08B"),
            ["graphite"] = Palette(
                "#101214", "#181B1F", "#20242A", "#2A2F36", "#343A43",
                "#2F353D", "#48515D", "#F3F5F7", "#9AA3AE",
                "#697386", "#8290A2", "#566170", "#E0555D", "#F06A72", "#45C5C8"),
            ["light"] = Palette(
                "#F4F7FB", "#FFFFFF", "#EAF0F7", "#DDE7F2", "#CEDAE8",
                "#CBD5E1", "#94A3B8", "#1B2638", "#5F6F84",
                "#4F7FEA", "#6594F4", "#3F6CD0", "#D8465A", "#EB5A6D", "#2E9E70"),
            ["emerald"] = Palette(
                "#081713", "#10231E", "#173029", "#204038", "#2A5147",
                "#24463D", "#397064", "#ECFFF8", "#8FB9AA",
                "#159A78", "#22B88F", "#117B61", "#E4515E", "#F06973", "#68D5B5"),
            ["amber"] = Palette(
                "#181109", "#251A0E", "#322313", "#45301A", "#593E21",
                "#4D351C", "#75512A", "#FFF7E8", "#C9A77C",
                "#E58A1F", "#F6A83A", "#C96F12", "#E8533F", "#F46C56", "#E7B84E"),
            [AstolfoPinkThemeId] = Palette(
                "#160F18", "#211722", "#2B1D2D", "#38243A", "#493047",
                "#4A2C43", "#68405F", "#FFF2FA", "#C39AAF",
                "#F06AB4", "#FF82C5", "#D9529D", "#F05B78", "#FF758F", "#58D6A8"),
        };

    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static event Action<string>? ThemeChanged;

    public static IReadOnlyList<AppThemeInfo> GetAvailableThemes(int createdTaskCount) =>
        AllThemes.Where(x => x.Id != AstolfoPinkThemeId || ThemeUnlockPolicy.IsAstolfoUnlocked(createdTaskCount)).ToArray();

    public static AppThemeInfo Find(string? themeId, int createdTaskCount) =>
        GetAvailableThemes(createdTaskCount).FirstOrDefault(x => string.Equals(x.Id, themeId, StringComparison.OrdinalIgnoreCase)) ?? AllThemes[0];

    public static string Normalize(string? themeId, int createdTaskCount) => Find(themeId, createdTaskCount).Id;

    public static ImageSource GetIcon(string? themeId)
    {
        var theme = AllThemes.FirstOrDefault(x => string.Equals(x.Id, themeId, StringComparison.OrdinalIgnoreCase)) ?? AllThemes[0];
        if (IconCache.TryGetValue(theme.Id, out var cached)) return cached;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(theme.IconUri, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        IconCache[theme.Id] = image;
        return image;
    }

    public static void Apply(string? themeId, int createdTaskCount)
    {
        var normalized = Normalize(themeId, createdTaskCount);
        var resources = System.Windows.Application.Current.Resources;
        foreach (var (brushKey, colorValue) in Palettes[normalized])
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorValue);
            if (resources[brushKey] is SolidColorBrush { IsFrozen: false } brush)
                brush.Color = color;
            else
                resources[brushKey] = new SolidColorBrush(color);

            var colorKey = brushKey[..^"Brush".Length] + "Color";
            if (resources.Contains(colorKey)) resources[colorKey] = color;
        }

        foreach (Window window in System.Windows.Application.Current.Windows)
            window.Icon = GetIcon(normalized);

        ThemeChanged?.Invoke(normalized);
    }

    internal static string GetIconUri(string? themeId) =>
        (AllThemes.FirstOrDefault(x => string.Equals(x.Id, themeId, StringComparison.OrdinalIgnoreCase)) ?? AllThemes[0]).IconUri;

    private static IReadOnlyDictionary<string, string> Palette(
        string background, string surface, string surface2, string surface3, string surface4,
        string border, string borderHover, string text, string muted,
        string accent, string accentHover, string accentPressed,
        string danger, string dangerHover, string success) =>
        new Dictionary<string, string>
        {
            ["BgBrush"] = background,
            ["SurfaceBrush"] = surface,
            ["Surface2Brush"] = surface2,
            ["Surface3Brush"] = surface3,
            ["Surface4Brush"] = surface4,
            ["BorderBrush"] = border,
            ["BorderHoverBrush"] = borderHover,
            ["TextBrush"] = text,
            ["MutedBrush"] = muted,
            ["AccentBrush"] = accent,
            ["AccentHoverBrush"] = accentHover,
            ["AccentPressedBrush"] = accentPressed,
            ["DangerBrush"] = danger,
            ["DangerHoverBrush"] = dangerHover,
            ["SuccessBrush"] = success,
        };
}
