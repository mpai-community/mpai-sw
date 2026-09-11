namespace Mpai.Core;

// Resolves the application root at runtime so the app is portable.
//
// The root is the first ancestor of the running executable that contains the
// parts present in EVERY clone: the "AIMs" folder AND the "UAs" folder. Models/
// and SharedStorage/ are machine-local (distributed separately) and are NOT
// required to locate the root. A clone unzipped anywhere therefore finds its own
// AIMs/AMDs, aim-settings.json and UAs/Assets, whether or not models are present.
public static class MpaiPaths
{
    private static string? _root;
    public static string Root => _root ??= FindRoot();

    // A directory is the application root if it holds both the AIMs and the
    // User-Agent side. These ship in the repository; Models/ need not.
    private static bool IsRoot(string dir) =>
        System.IO.Directory.Exists(System.IO.Path.Combine(dir, "AIMs")) &&
        System.IO.Directory.Exists(System.IO.Path.Combine(dir, "UAs"));

    private static string FindRoot()
    {
        // Single-file apps extract to a temp dir, so AppContext.BaseDirectory is
        // NOT the install folder; try the real executable location first.
        var starts = new[]
        {
            System.IO.Path.GetDirectoryName(System.Environment.ProcessPath ?? ""),
            System.AppContext.BaseDirectory
        };

        // 1) The repository root: an ancestor holding both AIMs and UAs.
        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (var d = new System.IO.DirectoryInfo(start); d != null; d = d.Parent)
                if (IsRoot(d.FullName)) return d.FullName;
        }

        // 2) Looser: an ancestor holding AIMs (in case the UAs layout differs).
        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (var d = new System.IO.DirectoryInfo(start); d != null; d = d.Parent)
                if (System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "AIMs")))
                    return d.FullName;
        }

        // 3) Last resort: the executable's own folder. NEVER a hard-coded path.
        return System.IO.Path.GetDirectoryName(System.Environment.ProcessPath ?? "")
               ?? System.AppContext.BaseDirectory;
    }

    public static string Model(string fileName) => System.IO.Path.Combine(Root, "Models", fileName);
    public static string Gallery       => System.IO.Path.Combine(Root, "TestData", "gallery.json");
    public static string Amds          => System.IO.Path.Combine(Root, "AIMs", "AMDs");
    public static string Settings      => System.IO.Path.Combine(Root, "AIMs", "aim-settings.json");
    public static string Assets        => System.IO.Path.Combine(Root, "UAs", "Assets");
    // The governed Shared Storage area (AIF Shared Storage backing folder).
    public static string SharedStorage => System.IO.Path.Combine(Root, "SharedStorage");
}
