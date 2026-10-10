namespace AIF.Controller;

// Internal executor representation of a routed Data Object: what an AIM produced,
// with the Data Type and Port Number of the Port it was produced on, as the AIM's
// own Metadata declares them (M3213 3.1).
public sealed class RoutedObject
{
    public string DataType { get; init; } =
        string.Empty;

    // Every Data Type the producing Port accepts; empty means DataType alone.
    public IReadOnlyList<string> DataTypes { get; init; } =
        Array.Empty<string>();

    public int PortNumber { get; init; } =
        1;

    public string Payload { get; init; } =
        string.Empty;

    // Whether this is what a Topology end names: its Data Type, on its Port Number.
    public bool IsFrom(Endpoint end) =>
        PortNumber == end.PortNumber &&
        (DataType == end.DataType || DataTypes.Contains(end.DataType));
}
