namespace Mpai.Core;

// Resolves the application root at runtime so the app is portable: it walks up
// from the running assembly's location to the first ancestor containing both an
// "AIMs" and a "Models" folder, and treats that as the root. A clone unzipped
// anywhere then finds its own Models/TestData/AMDs/SharedStorage. Falls back to
// the historical dev location.
public static class MpaiPaths
{
    private static string? _root;
    public static string Root => _root ??= FindRoot();

    private static string FindRoot()
    {
        // Single-file apps extract to a temp dir, so AppContext.BaseDirectory is
        // NOT the install folder. Try the real exe location first.
        foreach (var __start in new[] {
                     System.IO.Path.GetDirectoryName(System.Environment.ProcessPath ?? ""),
                     System.AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(__start)) continue;
            var __d = new System.IO.DirectoryInfo(__start);
            while (__d != null)
            {
                if (System.IO.Directory.Exists(System.IO.Path.Combine(__d.FullName, "AIMs")) &&
                    System.IO.Directory.Exists(System.IO.Path.Combine(__d.FullName, "Models")))
                    return __d.FullName;
                __d = __d.Parent;
            }
        }
        var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null)
        {
            if (System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "AIMs")) &&
                System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "Models")))
                return d.FullName;
            d = d.Parent;
        }
        return @"D:\AI";   // dev fallback
    }

    public static string Model(string fileName) => System.IO.Path.Combine(Root, "Models", fileName);
    public static string Gallery       => System.IO.Path.Combine(Root, "TestData", "gallery.json");
    public static string Amds          => System.IO.Path.Combine(Root, "AIMs", "AMDs");
    public static string Settings      => System.IO.Path.Combine(Root, "AIMs", "aim-settings.json");
    public static string Assets        => System.IO.Path.Combine(Root, "UAs", "Assets");
    // The governed Shared Storage area (AIF Shared Storage backing folder).
    public static string SharedStorage => System.IO.Path.Combine(Root, "SharedStorage");
}
