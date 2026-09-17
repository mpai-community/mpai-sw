using System.Collections.Generic;

using AIF.Controller;

namespace Mpai.Hci.Api;

// The North API, as the User Agent depends on it.
//
// Two implementations: NorthApi, in process, and RemoteNorthApi, across a
// network. The UA holds this type and does not know which it has - which is the
// whole point of a typed, stateless seam.
public interface INorthApi
{
    AifError StartFlow(string moduleName);
    NorthApi.Result Advance(string moduleName, IEnumerable<NorthApi.Datum> inputs);
    void StopFlow(string moduleName);
}