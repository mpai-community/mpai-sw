namespace AIF.Controller;

// A self-bootstrapping AIM plug-in. An AIM assembly implements this so the
// middleware can discover and construct it WITHOUT a compile-time reference: the
// provider scans the deployed assemblies, finds the IAimPlugin implementations,
// maps AimName -> plugin, and calls Create. Each plug-in builds its own
// dependencies (models, gallery, ...) inside Create, from the settings it is given.
// This is what lets an AIM be an independent, Contracts-only distributable DLL.
public interface IAimPlugin
{
    // The standard AIM name this plug-in provides, e.g. "OSD-IDR-V1.5".
    string AimName { get; }

    // Build the processor. Ports are already resolved from the AIM's metadata;
    // settings carry any implementation configuration (model paths, etc.).
    IAimProcessor Create(
        AimPortReader ports,
        IReadOnlyDictionary<string, string> settings);
}
