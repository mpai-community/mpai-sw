namespace AIF.Controller;

public sealed class MachineInstance
{
    public string MachineId { get; init; } =
        string.Empty;

    public string AIMName { get; init; } =
        string.Empty;

    // The Module description from which this runtime
    // instance was instantiated.
    public DescriptorGraph DescriptorGraph { get; init; } =
        new();

    public int ConnectionCount { get; set; }

    public MachineState State { get; set; } =
        MachineState.Instantiated;

    public List<AimInstance> AimInstances
    {
        get;
    } = new();

    public List<ChannelInstance> Channels
    {
        get;
    } = new();
}