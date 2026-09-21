using System;
using System.Collections.Generic;

namespace Mpai.Mas.PortData;

// MAS "port-data" codec for ONE Data Type.
//
// TWO SERIALISATIONS, NOT ONE. Inside a process an Object is whatever this
// implementation's C# happens to serialise to. On the wire it must be the
// instance its MPAI schema describes: the Header is the constant the schema
// pins, the names are the schema's names, and the data sits in the array form
// the schema defines. Another implementer's AIMs will serialise differently
// inside and remain conformant outside; that is the whole point of the
// boundary, and the reason a codec exists rather than a pass-through.
//
// ONE CODEC PER DATA TYPE, AND NO APPLICATION ANYWHERE. A codec knows a Data
// Type. It does not know which Module is running, which application asked, or
// what any port is called. Adding OSD-3OD means adding a codec; it never means
// editing the server that three applications share.
public interface IPortDataCodec
{
    // The Data Type this codec serves, e.g. "OSD-BSO-V1.5". This is the value
    // the schema pins as the Object's Header, and the value the MAS route
    // carries. Never a port name.
    string DataType { get; }

    // MAS port-data bytes -> the internal object JSON the Controller and AIMs
    // read (MpaiJson).
    string ToInternal(byte[] wire);

    // Internal object JSON -> MAS port-data bytes conforming to the schema.
    byte[] ToWire(string internalJson);
}

// The set of Data Types a server or client can carry.
//
// A LOOKUP, NOT A SWITCH. The old MAS server chose its conversion with a switch
// over port NAMES, so it carried cases for ports belonging to Modules it was not
// running, and any type without a case fell through to "send whatever the client
// chose" - which made one client's private serialisation the de facto protocol.
// Here an unregistered Data Type is an error the caller can report, not a silent
// pass-through.
public sealed class PortDataCodecs
{
    private readonly Dictionary<string, IPortDataCodec> byDataType =
        new(StringComparer.Ordinal);

    // The codecs both ends need to speak the types in use today. A new Data Type
    // is added here and in a file of its own; nothing else changes.
    public static PortDataCodecs Default() =>
        new PortDataCodecs()
            .Register(new BasicSpeechObjectCodec())
            .Register(new BasicTextObjectCodec())
            .Register(new BasicVisualObjectCodec())
            .Register(new FaceDescriptorsObjectCodec())
            .Register(new SimpleTimeCodec())
            .Register(new SelectorCodec())
            .Register(new SummaryCodec());

    public PortDataCodecs Register(
        IPortDataCodec codec)
    {
        byDataType[codec.DataType] = codec;
        return this;
    }

    public bool Knows(
        string dataType) =>
        byDataType.ContainsKey(dataType);

    public IReadOnlyCollection<string> KnownDataTypes => byDataType.Keys;

    // Throws rather than guessing. A caller at an HTTP boundary should turn this
    // into 415 Unsupported Media Type: the peer asked for a Data Type this build
    // cannot carry, and saying so is more useful than delivering bytes that
    // happen to be shaped like something else.
    // EVERY CROSSING IS CHECKED AGAINST THE PUBLISHED SCHEMA. The serialisers
    // convert between the internal representation and the schema instance, and
    // nothing verified the second half of that claim until now. Checked here
    // rather than at each call site so that the server, the client and the
    // round-trip test are all covered by one place.
    public byte[] ToWire(string dataType, string internalJson)
    {
        var wire = For(dataType).ToWire(internalJson);
        PortDataSchema.Check(dataType, "produced", wire);
        return wire;
    }

    public string ToInternal(string dataType, byte[] wire)
    {
        PortDataSchema.Check(dataType, "received", wire);
        return For(dataType).ToInternal(wire);
    }

    public IPortDataCodec For(
        string dataType) =>
        byDataType.TryGetValue(dataType, out var codec)
            ? codec
            : throw new NotSupportedException(
                  $"No port-data codec for Data Type '{dataType}'. " +
                  $"Known: {string.Join(", ", byDataType.Keys)}.");
}