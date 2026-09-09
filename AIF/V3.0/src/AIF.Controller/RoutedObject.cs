namespace AIF.Controller;

// Internal executor representation of a routed Data Object.
public sealed class RoutedObject
{
    public string DataType { get; init; } =
        string.Empty;

    public string Payload { get; init; } =
        string.Empty;
}