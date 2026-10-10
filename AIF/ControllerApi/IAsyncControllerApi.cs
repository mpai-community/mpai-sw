using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Aif.Api;

// THE CONTROLLER API, WITHOUT WAITING. The same calls as IControllerApi,
// awaited. A browser runs WebAssembly on one thread and never lets it block on the
// network, and the workflow interpreter, which serves the browser and the desktop
// alike, holds this type.
public interface IAsyncControllerApi
{
    Task<AifError>             StartFlowAsync(string moduleName);
    Task<ControllerApi.Result> AdvanceAsync(string moduleName, IEnumerable<ControllerApi.Datum> inputs);

    // THE SAME EXCHANGE, SAYING WHAT IS WANTED: the Output Ports the caller will
    // read. An implementation that must ask for each output separately - over
    // MPAI-MAS - asks for these only; one that gets them all at once ignores it.
    Task<ControllerApi.Result> AdvanceAsync(string moduleName, IEnumerable<ControllerApi.Datum> inputs,
                                            IReadOnlyCollection<(string DataType, int PortNumber)> wanted) =>
        AdvanceAsync(moduleName, inputs);

    Task                       StopFlowAsync(string moduleName);
    Task<AifError>             InputWriteAsync(string moduleName, string dataType, int portNumber, string json, int timeoutMs = -1);
    Task<ControllerApi.Read>   OutputReadAsync(string moduleName, string dataType, int portNumber, int timeoutMs = -1);
    Task<AifError>             PauseAsync(string moduleName);
    Task<AifError>             ResumeAsync(string moduleName);
    Task<ControllerApi.ModuleStatus> StatusAsync(string moduleName);
    Task<AifError>             StopAimAsync(string moduleName, string aimName);
    Task<AifError>             SharedStorageInitAsync(string moduleName, string location);

    // A payload placed for a boundary Input Port, its reference returned (M3215
    // 3.6). Not over MPAI-MAS (Phase 15): there a payload goes inline.
    Task<(AifError Error, string? Reference)> PayloadPutAsync(string moduleName, string dataType, int portNumber, ReadOnlyMemory<byte> data) =>
        Task.FromResult((AifError.Failed, (string?)null));
}

public static class ControllerApiAsync
{
    // A synchronous Controller API, awaited: each call is made on a thread of the
    // pool, so whoever awaits it - a window, typically - is never blocked.
    public static IAsyncControllerApi Async(this IControllerApi api) => new OnThePool(api);

    private sealed class OnThePool : IAsyncControllerApi
    {
        private readonly IControllerApi api;
        public OnThePool(IControllerApi api) => this.api = api;

        public Task<AifError> StartFlowAsync(string moduleName) =>
            Task.Run(() => api.StartFlow(moduleName));

        public Task<(AifError Error, string? Reference)> PayloadPutAsync(string moduleName, string dataType, int portNumber, ReadOnlyMemory<byte> data) =>
            api is ControllerApi local
                ? Task.Run(() => local.PayloadPut(moduleName, dataType, portNumber, data))
                : Task.FromResult((AifError.Failed, (string?)null));

        public Task<ControllerApi.Result> AdvanceAsync(string moduleName, IEnumerable<ControllerApi.Datum> inputs) =>
            Task.Run(() => api.Advance(moduleName, inputs));

        public Task StopFlowAsync(string moduleName) =>
            Task.Run(() => api.StopFlow(moduleName));

        public Task<AifError> InputWriteAsync(string moduleName, string dataType, int portNumber, string json, int timeoutMs = -1) =>
            Task.Run(() => api.InputWrite(moduleName, dataType, portNumber, json, timeoutMs));

        public Task<ControllerApi.Read> OutputReadAsync(string moduleName, string dataType, int portNumber, int timeoutMs = -1) =>
            Task.Run(() => api.OutputRead(moduleName, dataType, portNumber, timeoutMs));

        public Task<AifError> PauseAsync(string moduleName) => Task.Run(() => api.Pause(moduleName));

        public Task<AifError> ResumeAsync(string moduleName) => Task.Run(() => api.Resume(moduleName));

        public Task<ControllerApi.ModuleStatus> StatusAsync(string moduleName) => Task.Run(() => api.Status(moduleName));

        public Task<AifError> StopAimAsync(string moduleName, string aimName) => Task.Run(() => api.StopAim(moduleName, aimName));

        public Task<AifError> SharedStorageInitAsync(string moduleName, string location) => Task.Run(() => api.SharedStorageInit(moduleName, location));
    }
}
