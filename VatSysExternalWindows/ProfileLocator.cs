using System.IO;
using System.Reflection;
using vatsys;

namespace VatSysExternalWindows;

/// <summary>
/// Resolves the directory of the currently loaded vatSys profile the same way
/// vatSys does: Properties.Settings.Default.DatasetPath is set when a profile
/// is chosen (profiles can live anywhere, not just under Documents), with the
/// standard "vatSys Files\Profiles" layout as a fallback.
/// </summary>
public static class ProfileLocator
{
    /// <summary>Returns the loaded profile's directory, or "" if none could be resolved.</summary>
    public static string Resolve()
    {
        // vatsys.Properties.Settings is internal — read DatasetPath via reflection.
        try
        {
            var settingsType = typeof(Profile).Assembly.GetType("vatsys.Properties.Settings");
            var defaultProp = settingsType?.GetProperty("Default",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var settings = defaultProp?.GetValue(null);
            var pathProp = settings?.GetType().GetProperty("DatasetPath",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pathProp?.GetValue(settings) is string path &&
                !string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                return path;
            }
        }
        catch { }

        try
        {
            var name = Profile.Name;
            if (!string.IsNullOrEmpty(name))
            {
                var dir = Path.Combine(Helpers.GetFilesFolder(), "Profiles", name);
                if (Directory.Exists(dir)) return dir;
            }
        }
        catch { }

        // Last resort: any profile folder containing a Positions.xml
        try
        {
            var profilesRoot = Path.Combine(Helpers.GetFilesFolder(), "Profiles");
            if (Directory.Exists(profilesRoot))
            {
                foreach (var dir in Directory.GetDirectories(profilesRoot))
                {
                    if (File.Exists(Path.Combine(dir, "Positions.xml")))
                        return dir;
                }
            }
        }
        catch { }

        return "";
    }
}
