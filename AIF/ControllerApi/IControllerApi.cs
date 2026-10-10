using System.Collections.Generic;

using AIF.Controller;

namespace Mpai.Aif.Api;

// The Controller API (formerly called the North API), as the User Agent depends on it.
//
// Two implementations: ControllerApi, in process, and RemoteControllerApi, across a
// network. The UA holds this type and does not know which it has - which is the
// whole point of a typed, stateless seam.
public interface IControllerApi
{
    AifError StartFlow(string moduleName);
    ControllerApi.Result Advance(string moduleName, IEnumerable<ControllerApi.Datum> inputs);
    void StopFlow(string moduleName);

    // The data path of M3203 3.3 (M3213 3.2): a write and a read per boundary
    // Port, with named outcomes and a timeout in milliseconds (0: do not wait;
    // negative: wait without limit).
    AifError InputWrite(string moduleName, string dataType, int portNumber, string json, int timeoutMs = -1);
    ControllerApi.Read OutputRead(string moduleName, string dataType, int portNumber, int timeoutMs = -1);

    // MPAI_AIFU_MODULE_Pause and _Resume (M3213 3.4): the whole Module, at every
    // depth, until Resume.
    AifError Pause(string moduleName);
    AifError Resume(string moduleName);

    // The status of each AIM of the Module and the reports of its last run, and
    // MPAI_AIFU_AIM_Stop (M3213 3.5).
    ControllerApi.ModuleStatus Status(string moduleName);
    AifError StopAim(string moduleName, string aimName);

    // MPAI_AIFU_SharedStorage_Init (M3203 3.4.1, M3213 3.6): where the Module's
    // Shared Storage is held.
    AifError SharedStorageInit(string moduleName, string location);
}