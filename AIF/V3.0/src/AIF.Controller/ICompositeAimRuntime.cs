namespace AIF.Controller;

public interface ICompositeAimRuntime
{
    Message Execute(
        MachineInstance machine,
        Message message,
        AimHost host);
}