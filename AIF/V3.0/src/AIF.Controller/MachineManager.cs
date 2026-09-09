namespace AIF.Controller;

public sealed class MachineManager
{
    public void StartMachine(
        MachineInstance machine)
    {
        machine.State =
            MachineState.Running;
    }

    public void PauseMachine(
        MachineInstance machine)
    {
        machine.State =
            MachineState.Paused;
    }

    public void ResumeMachine(
        MachineInstance machine)
    {
        machine.State =
            MachineState.Running;
    }

    public void StopMachine(
        MachineInstance machine)
    {
        machine.State =
            MachineState.Stopped;
    }
}