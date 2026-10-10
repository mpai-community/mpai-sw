namespace AIF.Controller;

// A no-op stand-in used for smoke-testing a pipeline without a real AIM.
public sealed class DummyTiqAdapter
    : IAimProcessor
{
    public string InstanceId
    {
        get;
    }

    public DummyTiqAdapter(
        string instanceId)
    {
        InstanceId =
            instanceId;
    }

    public Task<Message> ProcessAsync(
        Message message)
    {
        return Task.FromResult(
            new Message
            {
                MessageId = message.MessageId,
                MessageType = message.MessageType,
                DataType = message.DataType,
                Payload = $"TIQ answered: {message.Payload}",
                Ports = message.Ports
            });
    }
}

