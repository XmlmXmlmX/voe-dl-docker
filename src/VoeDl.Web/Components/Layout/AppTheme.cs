using MudBlazor;

namespace VoeDl.Web.Components.Layout;

/// <summary>
/// Central MudTheme — indigo primary, custom job-status palette, slightly
/// rounded shapes. Used by MainLayout.razor.
/// </summary>
public static class AppTheme
{
    public static MudTheme Build() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary       = "#594ae2",
            PrimaryDarken = "#4b3fc7",
            PrimaryLighten= "#7c70ec",
            Secondary     = "#ff4081",
            Info          = "#2196f3",
            Success       = "#00c853",
            Warning       = "#ff9800",
            Error         = "#f44336",
            Background    = "#f5f5f5",
            Surface       = "#ffffff",
            AppbarBackground = "#594ae2",
            AppbarText    = "#ffffff",
            DrawerBackground = "#ffffff",
            TextPrimary   = "#212121",
            TextSecondary = "#616161",
            ActionDefault = "#9e9e9e",
            DividerLight  = "rgba(0,0,0,0.08)",
        },
        PaletteDark = new PaletteDark
        {
            Primary       = "#7c70ec",
            PrimaryDarken = "#594ae2",
            PrimaryLighten= "#a899ff",
            Secondary     = "#ff4081",
            Info          = "#2196f3",
            Success       = "#00c853",
            Warning       = "#ff9800",
            Error         = "#f44336",
            Background    = "#1a1a1f",
            Surface       = "#27272e",
            AppbarBackground = "#27272e",
            AppbarText    = "#ffffff",
            DrawerBackground = "#27272e",
            TextPrimary   = "#f5f5f5",
            TextSecondary = "#b0b0b8",
            ActionDefault = "#7a7a85",
            DividerLight  = "rgba(255,255,255,0.10)",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Roboto", "Helvetica", "Arial", "sans-serif"],
            },
        },
    };
}
