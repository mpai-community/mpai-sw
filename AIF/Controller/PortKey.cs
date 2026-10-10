namespace AIF.Controller;

// THE KEY OF A PORT IN A MESSAGE. An AIM receives its inputs, and returns its
// outputs, in Message.Ports keyed "DataType#PortNumber": the Port's (first) Data
// Type and its number among the AIM's Ports of the same Direction and Data Type
// (its declared PortNumber, else its position, from 1). No name: the executor and
// the AIM (through AimPortReader) compute the same key from the same normalised
// L3.
public static class PortKey
{
    public static string Of(string dataType, int portNumber) => $"{dataType}#{portNumber}";
}
