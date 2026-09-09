namespace AIF.Controller;

// The Controller's registry of named boundary Ports for a running composite AIM.
// Exposes the Port operations from MPAI-AIF V3.0 Basic API section 4.6 that the
// User Agent (via the Controller) uses to move data across the composite's
// boundary. AIM-internal routing still happens through the MachineExecutor;
// these Ports are specifically the composite's external boundary.
public sealed class PortRegistry
{
    private readonly Dictionary<string, AifPort> _ports = new();

    // Declare a boundary Port (from the composite AMD's ExternalPorts).
    public AifPort Declare(string name, string direction, string dataType)
    {
        var port = new AifPort(name, direction, dataType);
        _ports[name] = port;
        return port;
    }

    public bool Has(string portName) => _ports.ContainsKey(portName);

    public AifPort Get(string portName)
    {
        if (!_ports.TryGetValue(portName, out var port))
            throw new InvalidOperationException(
                $"No boundary Port named '{portName}'.");
        return port;
    }

    // MPAI_AIFM_Port_Input_Write — write a Message to a named input Port.
    public void InputWrite(string portName, Message message) =>
        Get(portName).Write(message);

    // MPAI_AIFM_Port_Output_Read — blocking read from a named output Port.
    public Task<Message> OutputReadAsync(
        string portName, CancellationToken token = default) =>
        Get(portName).ReadAsync(token);

    // MPAI_AIFM_Port_Probe.
    public bool Probe(string portName) => Get(portName).Probe();

    // MPAI_AIFM_Port_CountPendingMessages.
    public int CountPendingMessages(string portName) =>
        Get(portName).CountPendingMessages();

    // MPAI_AIFM_Port_Reset.
    public void Reset(string portName) => Get(portName).Reset();
}
