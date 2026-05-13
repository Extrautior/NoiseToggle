using System.Reflection;

namespace NoiseToggle;

internal static class AppIcon
{
    public static Icon Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("NoiseToggle.Assets.noisetoggle.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }
}
