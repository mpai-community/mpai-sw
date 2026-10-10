namespace AIF.Controller;

public sealed class DataObjectMessage
{
    public string DataType { get; init; } =
        string.Empty;

    public string Payload { get; init; } =
        string.Empty;
}