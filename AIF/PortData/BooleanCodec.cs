using System;
using System.Text;

namespace Mpai.Aif.PortData;

// boolean - a truth value, as the AIF's type system carries it (M3245; the author).
//
// NOT AN MPAI DATA TYPE, and so no schema to check it against: Multimodal Access
// Control gives at its boundary whether Identity Reconciliation found the person in
// the gallery - its Identification - and a client branches on it. Inside and on the
// wire it is the same JSON literal, true or false.
public sealed class BooleanCodec : IPortDataCodec
{
    public string DataType => "boolean";

    public byte[] ToWire(string internalJson) => Encoding.UTF8.GetBytes(Parse(internalJson) ? "true" : "false");

    public string ToInternal(byte[] wire) => Parse(Encoding.UTF8.GetString(wire)) ? "true" : "false";

    private static bool Parse(string text) =>
        bool.TryParse(text.Trim().Trim('"'), out var value)
            ? value
            : throw new FormatException($"'{text}' is not a boolean.");
}
