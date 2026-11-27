using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VatSysExternalWindows;

/// <summary>The window page's files, embedded in the plugin DLL (see the
/// EmbeddedResource item in the project file).</summary>
internal static class WebAssets
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, byte[]?> Cache = new();

    /// <summary>The embedded file <c>Web/&lt;name&gt;</c>, or null if there is none.</summary>
    public static byte[]? Get(string name)
    {
        lock (Lock)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;

            byte[]? bytes = null;
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Web/" + name);
                if (stream != null)
                {
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }
            }
            catch { }

            Cache[name] = bytes;
            return bytes;
        }
    }
}
