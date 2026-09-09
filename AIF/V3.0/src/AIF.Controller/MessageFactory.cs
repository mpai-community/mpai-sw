namespace AIF.Controller;

public sealed class MessageFactory
{
    private int nextId = 1;

    public Message Create(
        string messageType,
        string payload)
    {
        var message =
            new Message
            {
                MessageId =
                    $"MSG#{nextId}",

                MessageType =
                    messageType,

                Payload =
                    payload
            };

        nextId++;

        return message;
    }
}