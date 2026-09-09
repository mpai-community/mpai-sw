using System;
using AIF.Store;

namespace AIF.Controller;

// The User Agent, as defined by MPAI-AIF V3.0 Basic API section 3.
// It is the SOLE boundary between the human/OS world and the AIF.
// The application (the human-facing UI) calls ONLY these MPAI_AIFU_* methods;
// it never touches the Controller internals or any AIM directly.
//
// In V3.0 an AI Workflow (Module) is a composite AIM, so "Module" here means the
// composite AIM (e.g. MMC-AMQ-V2.5).
//
// Error convention follows the standard: methods return AifError.OK on success.
public sealed class UserAgent
{
    private readonly AmdStore   _store;
    private Controller?         _controller;
    private readonly Dictionary<int, RunningModule> _running = new();
    private int _nextModuleId = 1;

    public UserAgent(AmdStore store) => _store = store;

    // A running Module (composite AIM): its graph, host, and boundary Ports.
    private sealed class RunningModule
    {
        public required string          Name        { get; init; }
        public required DescriptorGraph Graph       { get; init; }
        public required AimHost         Host        { get; init; }
        public required MachineExecutor Executor    { get; init; }
        public required PortRegistry    Ports       { get; init; }

        // The last suspension point of this Module's resumable run, if any.
        public SuspendedExecution? Suspended { get; set; }
    }

    // â”€â”€ 3.1 General: initialise / destroy the Controller â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // MPAI_AIFU_Controller_Initialize
    public AifError MPAI_AIFU_Controller_Initialize()
    {
        _controller = new Controller(_store);
        return AifError.OK;
    }

    // MPAI_AIFU_Controller_Destroy
    public AifError MPAI_AIFU_Controller_Destroy()
    {
        foreach (var module in _running.Values)
            module.Host.Dispose();
        _running.Clear();
        _controller = null;
        return AifError.OK;
    }

    // â”€â”€ 3.2 Start/Pause/Resume/Stop the Module (composite AIM) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // MPAI_AIFU_MODULE_Start(name, out MODULE_ID)
    public AifError MPAI_AIFU_MODULE_Start(
        string name, IAimProvider provider, AimSettings settings, out int moduleId)
    {
        moduleId = -1;
        if (_controller is null) return AifError.NotInitialized;

        var selected = _store.GetCatalog().FirstOrDefault(c => c.AIMName == name);
        if (selected is null) return AifError.NotFound;

        var identifier = new Identifier
        {
            AIMName          = selected.AIMName,
            ImplementerID    = selected.ImplementerID,
            ImplementationID = selected.ImplementationID
        };

        var graph = _controller.RegisterAim(identifier);
        var host  = new AimHost();
        _controller.Instantiate(graph, provider, settings, host);

        // Declare the composite's boundary Ports from its ExternalPorts.
        var ports = new PortRegistry();
        foreach (var p in graph.Root.Ports)
            ports.Declare(p.Name, p.Direction, p.DataType);

        moduleId = _nextModuleId++;
        _running[moduleId] = new RunningModule
        {
            Name     = name,
            Graph    = graph,
            Host     = host,
            Executor = new MachineExecutor(host),
            Ports    = ports
        };
        return AifError.OK;
    }

    // MPAI_AIFU_MODULE_Pause
    public AifError MPAI_AIFU_MODULE_Pause(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        foreach (var p in module.Graph.Root.Children)
            module.Host.PauseAim(p.AIMName);
        return AifError.OK;
    }

    // MPAI_AIFU_MODULE_Resume
    public AifError MPAI_AIFU_MODULE_Resume(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        foreach (var p in module.Graph.Root.Children)
            module.Host.ResumeAim(p.AIMName);
        return AifError.OK;
    }

    // MPAI_AIFU_MODULE_Stop
    public AifError MPAI_AIFU_MODULE_Stop(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        module.Host.Dispose();
        _running.Remove(moduleId);
        return AifError.OK;
    }

    // â”€â”€ 3.3 Inquire about AIM state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // MPAI_AIFU_AIM_GetStatus(MODULE_ID, name, out status)
    public AifError MPAI_AIFU_AIM_GetStatus(int moduleId, string name, out AimState status)
    {
        status = AimState.Idle;
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        status = module.Host.GetState(name);
        return AifError.OK;
    }

    // â”€â”€ Boundary Port access (section 4.6, used across the boundary) â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // The User Agent writes a data object to a composite input Port, and reads
    // a data object from a composite output Port. This is how the folder
    // screenshot goes in and the RecognisedText comes back out.

    // MPAI_AIFM_Port_Input_Write (exercised by the User Agent via Controller)
    public AifError PortInputWrite(int moduleId, string portName, Message message)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        if (!module.Ports.Has(portName)) return AifError.NotFound;
        module.Ports.InputWrite(portName, message);
        return AifError.OK;
    }

    // MPAI_AIFM_Port_Output_Read
    public async Task<(AifError, Message?)> PortOutputReadAsync(
        int moduleId, string portName, CancellationToken token = default)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return (AifError.NotFound, null);
        if (!module.Ports.Has(portName)) return (AifError.NotFound, null);
        var msg = await module.Ports.OutputReadAsync(portName, token);
        return (AifError.OK, msg);
    }

    // MPAI_AIFM_Port_Probe
    public bool PortProbe(int moduleId, string portName) =>
        _running.TryGetValue(moduleId, out var module) &&
        module.Ports.Has(portName) && module.Ports.Probe(portName);

    // â”€â”€ Resumable run: the User Agent writes boundary PORTS and reacts â”€â”€â”€â”€â”€â”€
    // The UA supplies data on the composite's boundary input ports and reacts
    // to the composite's requests for more input. It never names an AIM nor
    // orders execution - the Controller/executor runs the AIMs per the Topology.

    public sealed class RunOutcome
    {
        public required bool Suspended { get; init; }
        // The boundary input port the composite is waiting for (if suspended).
        public string? WaitingPort { get; init; }
        // Partial outputs the composite can already expose (e.g. OCR listing).
        public IReadOnlyDictionary<string, string>? PartialOutputs { get; init; }
        // Final outputs when the run completed.
        public Message? Completed { get; init; }
    }

    // Start the Module's resumable run, writing one or more boundary input ports.
    // The executor runs everything runnable and suspends on the first boundary
    // port it still needs.
    public async Task<(AifError, RunOutcome?)> RunAsync(
        int moduleId, IReadOnlyDictionary<string, string> boundaryPorts)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return (AifError.NotFound, null);

        var result = await module.Executor.ExecuteResumableAsync(
            module.Graph,
            new Message
            {
                MessageId   = Guid.NewGuid().ToString(),
                MessageType = "AMQ",
                Ports       = new Dictionary<string, string>(boundaryPorts)
            });

        return Outcome(module, result);
    }

    // Resume a suspended Module, writing one or more further boundary input ports.
    public async Task<(AifError, RunOutcome?)> ResumeAsync(
        int moduleId, IReadOnlyDictionary<string, string> boundaryPorts)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return (AifError.NotFound, null);
        if (module.Suspended is null) return (AifError.Failed, null);

        var result = await module.Executor.ResumeAsync(module.Suspended, boundaryPorts);
        return Outcome(module, result);
    }

    private static (AifError, RunOutcome?) Outcome(RunningModule module, ExecutionResult result)
    {
        if (result.IsSuspended)
        {
            module.Suspended = result.Suspended;
            return (AifError.OK, new RunOutcome
            {
                Suspended      = true,
                WaitingPort    = result.Suspended!.WaitingPort,
                PartialOutputs = result.Suspended!.PartialOutputs
            });
        }

        module.Suspended = null;
        return (AifError.OK, new RunOutcome
        {
            Suspended = false,
            Completed = result.Completed
        });
    }

    // TryGetRuntime USED to live here, handing an Module's AimHost and PortRegistry
    // to whoever asked. Its own comment said "not part of the public MPAI_AIFU_*
    // surface", which was the warning: it let a User Agent register an AIM into a
    // running Module and invoke it outside the Topology that governs it. Nothing in
    // the AMD would mention that AIM, and nothing could refuse it.
    //
    // Its one caller, AmqWorkflow, needed MMC-OCR - which is not a SubAIM of
    // MMC-AMQ. That is now an Module of the User Agent's own, UAG-OCR-V1.0, started
    // and run through this same public API. Removing the method is what makes the
    // guarantee real: an escape hatch that exists is an escape hatch that will be
    // used.
}

// Standard-style error codes.
public enum AifError
{
    OK = 0,
    NotInitialized,
    NotFound,
    Failed
}
