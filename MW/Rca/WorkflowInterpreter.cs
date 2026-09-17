using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Hci.Api;
using Mpai.Wdl;

namespace Mpai.Rca;

// EXECUTES A WORKFLOW DESCRIPTION. Every step is either a request to the
// Controller or an act of the User Agent's own, so this is two switch statements
// and a dictionary of what has been acquired so far.
//
// It knows no application. The workflow names the Modules; the device registry
// names the devices; the North API is whichever one it was handed - in process or
// across a network, since a workflow cannot tell and should not be able to.
//
// A LABEL IS FOR THE READER AND DOES NOT ROUTE. What a datum is addressed by is
// its Data Type and Port Number. The label is how one step refers to what an
// earlier step obtained, and nothing else: the interpreter keeps a table from
// label to datum, and the Controller never sees a label at all.
public sealed class WorkflowInterpreter
{
    private readonly INorthApi     north;
    private readonly DeviceRegistry devices;
    private readonly Action<string> say;      // a line for whoever is watching

    // What has been acquired or received, by the label the workflow gave it.
    private readonly Dictionary<string, (string DataType, string Json)> data =
        new(StringComparer.OrdinalIgnoreCase);

    // The User Agent's own variables. They live here and nowhere else: a Module's
    // memory is the Module's, and the Framework never sees these.
    private readonly Dictionary<string, string> variables =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> running = new(StringComparer.OrdinalIgnoreCase);

    public WorkflowInterpreter(
        INorthApi north,
        DeviceRegistry devices,
        Action<string>? say = null)
    {
        this.north   = north;
        this.devices = devices;
        this.say     = say ?? (_ => { });
    }

    public async Task RunAsync(Workflow workflow, CancellationToken stop)
    {
        say($"workflow {workflow.Name} over {string.Join(", ", workflow.Modules)}");
        try
        {
            await WalkAsync(workflow.OnStart, stop);
        }
        finally
        {
            // What On Stop says, and then whatever is still running: a workflow
            // that failed mid-way must not leave a Module started.
            try { await WalkAsync(workflow.OnStop, CancellationToken.None); }
            catch (Exception ex) { say("on Stop: " + ex.Message); }

            foreach (var module in running.ToList()) StopModule(module);
        }
    }

    private async Task WalkAsync(IReadOnlyList<Step> steps, CancellationToken stop)
    {
        foreach (var step in steps)
        {
            if (stop.IsCancellationRequested && step.Kind != StepKind.StopModule) return;
            await StepAsync(step, stop);
        }
    }

    // ---- what is asked of the Controller -----------------------------------

    private void StartModule(string module)
    {
        var err = north.StartFlow(module);
        if (err != AifError.OK)
            throw new InvalidOperationException($"the Controller would not start {module}: {err}.");
        running.Add(module);
        say($"[C] started {module}");
    }

    private void StopModule(string module)
    {
        north.StopFlow(module);
        running.Remove(module);
        say($"[C] stopped {module}");
    }

    // Data given to the Controller for a Module accumulate until something is
    // asked for from that Module: a Module runs when its required inputs are
    // present, so the request that asks for an output is the one that runs it.
    private readonly Dictionary<string, List<NorthApi.Datum>> pending =
        new(StringComparer.OrdinalIgnoreCase);

    private void Take(Step step, string json)
    {
        var port = step.Port!;
        if (!pending.TryGetValue(step.Module!, out var list))
            pending[step.Module!] = list = new List<NorthApi.Datum>();

        list.Add(new NorthApi.Datum(port.DataType, port.PortNumber, json));
        say($"[C] take {port} for {step.Module}");
    }

    private void Give(Step step)
    {
        pending.TryGetValue(step.Module!, out var inputs);
        var result = north.Advance(step.Module!, inputs ?? new List<NorthApi.Datum>());
        pending.Remove(step.Module!);

        if (!result.Ok)
            throw new InvalidOperationException($"{step.Module} returned {result.Error}.");

        if (result.Suspended)
            throw new InvalidOperationException(
                $"{step.Module} is waiting for {result.WaitingPort ?? "something the Controller did not name"}, " +
                "which this workflow did not give it.");

        foreach (var want in step.Ports)
        {
            var json = result.ByType(want.DataType, want.PortNumber);
            if (string.IsNullOrWhiteSpace(json))
            {
                say($"[C] give {want} from {step.Module}: nothing");
                continue;
            }
            data[want.Label] = (want.DataType, json);
            say($"[C] give {want} from {step.Module}");
        }
    }
    // ---- what the User Agent does itself -----------------------------------

    private async Task StepAsync(Step step, CancellationToken stop)
    {
        switch (step.Kind)
        {
            case StepKind.StartModule:  StartModule(step.Module!); break;
            case StepKind.StopModule:   StopModule(step.Module!);  break;

            // The Controller has MPAI_AIFU_MODULE_Pause and _Resume and the North
            // API does not expose them. No reference workflow asks, so this refuses
            // plainly rather than pretending: a workflow that pauses a Module and
            // silently did not would be worse than one that stops.
            case StepKind.PauseModule:
            case StepKind.ResumeModule:
                throw new NotSupportedException(
                    $"line {step.Line}: the North API does not yet offer pause or resume. " +
                    "The Controller has them; the seam has not been widened to pass them on.");

            case StepKind.Take:
            {
                // An inline value is the workflow's own words; otherwise the datum
                // is one an earlier step obtained, found by its label.
                var json = step.Literal is not null
                    ? Literal(step.Port!.DataType, Fill(step.Literal))
                    : Known(step.Port!.Label, step.Line);
                Take(step, json);
                break;
            }

            case StepKind.Give: Give(step); break;

            case StepKind.Acquire:
            {
                var port = step.Port!;
                var json = await devices.AcquireAsync(port.DataType, step.ViaVad);
                if (json is null)
                {
                    say($"acquire {port}: nothing");
                    break;
                }
                data[port.Label] = (port.DataType, json);
                say($"acquire {port}");
                break;
            }

            case StepKind.Type:
            {
                var port = step.Port!;
                var json = await devices.AcquireAsync(port.DataType, false);
                if (json is not null) { data[port.Label] = (port.DataType, json); say($"type {port}"); }
                break;
            }

            case StepKind.Prompt:
                await devices.PresentAsync(new Dictionary<string, string> { ["Prompt"] = Fill(step.Text ?? "") });
                break;

            case StepKind.Display:
            {
                var shown = new Dictionary<string, string>();
                foreach (var label in step.Labels)
                    if (data.TryGetValue(label, out var d)) shown["Display:" + d.DataType] = d.Json;
                    else shown["Display"] = Fill("{" + label + "}");
                await devices.PresentAsync(shown);
                break;
            }

            case StepKind.Present:
            {
                // Everything named is presented together: a speech object and its
                // face descriptors are one utterance, not two.
                var together = new Dictionary<string, string>();
                foreach (var label in step.Labels)
                    if (data.TryGetValue(label, out var d)) together[d.DataType] = d.Json;
                await devices.PresentAsync(together);
                say("present " + string.Join(", ", step.Labels));
                break;
            }

            case StepKind.Wait:
                await Task.Delay(step.Duration, stop);
                break;

            case StepKind.Set:
                variables[step.Variable!] = Fill(step.Value ?? "");
                break;

            case StepKind.Loop:
                while (!stop.IsCancellationRequested)
                    await WalkAsync(step.Body, stop);
                break;

            case StepKind.Branch:
                await WalkAsync(Truth(step.Variable!) ? step.Body : step.Else, stop);
                break;

            default:
                throw new NotSupportedException($"line {step.Line}: {step.Kind} is not executed.");
        }
    }

    // ---- the small helpers -------------------------------------------------

    // A branch tests a value, never a presence. An output Port that produced
    // nothing can mean many things - not recognised, not run, an input missing -
    // and a workflow that read meaning into silence would be reading its own
    // assumptions. A Boolean means one thing.
    private bool Truth(string name)
    {
        if (variables.TryGetValue(name, out var v))
            return v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        if (data.TryGetValue(name, out var d))
            return d.Json.Trim().Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase);

        throw new InvalidOperationException(
            $"'{name}' was never obtained, so there is nothing to branch on.");
    }

    private string Known(string label, int line) =>
        data.TryGetValue(label, out var d)
            ? d.Json
            : throw new InvalidOperationException(
                  $"line {line}: '{label}' was never acquired or received.");

    // "{UserName}, welcome." with what is known put in place of the braces.
    private string Fill(string text)
    {
        foreach (var kv in variables)
            text = text.Replace("{" + kv.Key + "}", kv.Value, StringComparison.OrdinalIgnoreCase);

        foreach (var kv in data)
            text = text.Replace("{" + kv.Key + "}", Plain(kv.Value.Json), StringComparison.OrdinalIgnoreCase);

        return text;
    }

    // The words inside a Text Object, for putting into a sentence.
    private static string Plain(string json)
    {
        try
        {
            var t = Mpai.Core.MpaiJson.FromJson<Mpai.Core.BasicTextObject>(json);
            return t?.GetText() ?? "";
        }
        catch { return ""; }
    }

    // A literal in a workflow is words; the Data Type says what to make of them.
    private static string Literal(string dataType, string text) =>
        dataType.StartsWith("OSD-BTO", StringComparison.OrdinalIgnoreCase)
            ? Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicTextObject.FromText(text))
            : throw new NotSupportedException(
                  $"a workflow may write a literal for a Text Object; {dataType} must be acquired.");
}