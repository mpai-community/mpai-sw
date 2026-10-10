namespace AIF.Controller;

public sealed class AimRegistrationResult
{
    public string AIMName { get; init; } = string.Empty;

    public List<string> SubAIMs { get; } = new();

    public int TopologyConnections { get; set; }
}