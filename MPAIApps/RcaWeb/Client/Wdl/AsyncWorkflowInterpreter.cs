// THE WORKFLOW INTERPRETER, FOR A BROWSER. A copy of Mpai.Rca.WorkflowInterpreter
// in which every call on the North API is awaited, because a browser never lets
// WebAssembly block. Nothing else differs. Once the browser client is proven,
// the two can become one asynchronous interpreter; until then the desktop RCA
// keeps its own, untouched.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Hci.Api;
using Mpai.Wdl;
using Mpai.Rca;
using Mpai.RcaWeb.Mas;

namespace Mpai.RcaWeb.Wdl;

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
public sealed class AsyncWorkflowInterpreter
{
    private readonly IAsyncNorthApi north;
    private readonly DeviceRegistry devices;
    private readonly Action<string> say;      // a line for whoever is watching

    // What has been acquired or received, by the label the workflow gave it.
    private readonly Dictionary<string, (string DataType, string Json)> data =
        new(StringComparer.OrdinalIgnoreCase);

    // The User Agent's own variables. They live here and nowhere else: a Module's
    // memory is the Module's, and the Framework never sees these.
    private readonly Dictionary<string, string> variables =
        new(StringComparer.OrdinalIgnoreCase);

    // Labels an 'acquire ... or ...' waited for and did not get. 'branch on' such
    // a label is false: it names the path that was not taken.
    private readonly HashSet<string> absent = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> running = new(StringComparer.OrdinalIgnoreCase);

    public AsyncWorkflowInterpreter(
        IAsyncNorthApi north,
        DeviceRegistry devices,
        Action<string>? say = null)
    {
        this.north   = north;
        this.devices = devices;
        this.say     = say ?? (_ => { });
    }

    public async Task RunAsync(Workflow workflow, CancellationToken stop)
    {
        current = workflow;
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

            foreach (var module in running.ToList()) await StopModuleAsync(module);
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

    private async Task StartModuleAsync(string module)
    {
        var err = await north.StartFlowAsync(module);
        if (err != AifError.OK)
            throw new InvalidOperationException($"the Controller would not start {module}: {err}.");
        running.Add(module);
        say($"[C] started {module}");
    }

    private async Task StopModuleAsync(string module)
    {
        await north.StopFlowAsync(module);
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
        if (!pending.TryGetValue(Module, out var list))
            pending[Module] = list = new List<NorthApi.Datum>();

        list.Add(new NorthApi.Datum(port.DataType, port.PortNumber, json));
        say($"[C] take {port.DataType}{(port.PortNumber is int n ? ":" + n : "")}");
    }

    private async Task GiveAsync(Step step)
    {
        pending.TryGetValue(Module, out var inputs);
        var result = await north.AdvanceAsync(Module, inputs ?? new List<NorthApi.Datum>());
        pending.Remove(Module);

        if (!result.Ok)
            throw new InvalidOperationException($"{Module} returned {result.Error}.");

        if (result.Suspended)
            throw new InvalidOperationException(
                $"{Module} is waiting for {result.WaitingPort ?? "something the Controller did not name"}, " +
                "which this workflow did not give it.");

        say($"[C] returned: {string.Join(", ", result.Outputs.Select(d => d.DataType + ":" + d.PortNumber))}");
        foreach (var want in step.Ports)
        {
            var json = result.ByType(want.DataType, want.PortNumber);
            if (string.IsNullOrWhiteSpace(json))
            {
                say($"[C] give {want}: nothing");
                continue;
            }
            data[want.Label] = (want.DataType, json);
            say($"[C] give {want}");
        }
    }
    // ---- what the User Agent does itself -----------------------------------

    private async Task StepAsync(Step step, CancellationToken stop)
    {
        switch (step.Kind)
        {
            case StepKind.StartModule:  await StartModuleAsync(Module); break;
            case StepKind.StopModule:   await StopModuleAsync(Module);  break;

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

            case StepKind.Give: await GiveAsync(step); break;

            case StepKind.Acquire when step.Alternatives.Count > 1:
                await FirstOfAsync(step, stop);
                break;

            case StepKind.Acquire:
            {
                var port = step.Port!;
                // WHAT THE WORKFLOW ASKED FOR, IF IT SAID. A client that cannot
                // produce it refuses and names what it can, which is a fault in the
                // App rather than in the run.
                string? json;
                try { json = await devices.AcquireAsync(port.DataType, step.ViaVad, step.Qualifier); }
                catch (NotSupportedException ex)
                {
                    say($"line {step.Line}: {ex.Message}");
                    throw;
                }

                // AN OBJECT WITH NO DATA IS A COUNTER-OFFER. A source asked for a
                // format it has not got answers with the Qualifier of what it does
                // have; the User Agent needs the datum, so it asks again naming
                // what was offered. It is the User Agent's business to reach the
                // goal it was given, and neither the workflow's nor the source's.
                if (json is not null && step.Qualifier is not null)
                {
                    var counter = CounterOffer(port.DataType, json);
                    if (counter is not null)
                    {
                        say("asked again, as offered");
                        json = await devices.AcquireAsync(port.DataType, step.ViaVad, counter);
                    }
                }
                // NOTHING ACQUIRED IS A PATH NOT TAKEN: 'branch on' the label is false.
                if (json is null)
                {
                    data.Remove(port.Label);
                    absent.Add(port.Label);
                    say($"acquire {port}: nothing");
                    break;
                }
                data[port.Label] = (port.DataType, json);
                absent.Remove(port.Label);
                say($"acquire {port}");
                break;
            }

            case StepKind.Type:
            {
                var port = step.Port!;
                var json = await devices.AcquireAsync(port.DataType, false, step.Qualifier);
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

            case StepKind.Await:
                if (devices.Await is not null)
                    await devices.Await(step.Text ?? "Continue");
                break;

            case StepKind.Wait:
                await Task.Delay(step.Duration, stop);
                break;

            case StepKind.Set:
                variables[step.Variable!] = Fill(step.Value ?? "");
                break;

            case StepKind.Loop:
                // 'end' inside the body leaves the loop, and only this loop: a
                // conversation a person has ended should not go round again.
                try
                {
                    while (!stop.IsCancellationRequested)
                        await WalkAsync(step.Body, stop);
                }
                catch (LoopEnded) { say("the App ended its loop"); }
                break;

            case StepKind.Branch:
            {
                // A TEXT TEST READS WHAT WAS SAID. 'contains' matches case-blind on
                // the datum's text; without it the test is a Boolean, which means
                // one thing and is the better test where a Module offers one.
                bool taken;
                if (step.Contains is null) taken = Truth(step.Variable!);
                else
                {
                    var said = data.TryGetValue(step.Variable!, out var d)
                        ? Plain(d.Json) : "";
                    taken = said.Contains(step.Contains, StringComparison.OrdinalIgnoreCase);
                    say($"branch on {step.Variable} contains \"{step.Contains}\": {(taken ? "yes" : "no")} ({said})");
                }
                await WalkAsync(taken ? step.Body : step.Else, stop);
                break;
            }

            // LEAVE THE ENCLOSING LOOP. Thrown rather than returned, so that it
            // unwinds out of whatever block it was in.
            // SAY IT AND WAIT. The words go to the Controller at the Port the
            // Module declares for them; the Speech Object and the Face Descriptors
            // come back and the User Agent renders them. The step does not finish
            // until she has finished speaking: a workflow says one thing after
            // another, and an implementation that overlapped them would not be
            // doing what the workflow says.
            case StepKind.Say:
            {
                var words = Literal(step.Port!.DataType, Fill(step.Literal ?? ""));
                say($"[C] say {step.Port.DataType}:{step.Port.PortNumber}");
                var said = await north.AdvanceAsync(Module, new List<NorthApi.Datum>
                    { new NorthApi.Datum(step.Port.DataType, step.Port.PortNumber, words) });
                if (!said.Ok) throw new InvalidOperationException($"{Module} returned {said.Error}.");

                var spoken = new Dictionary<string, string>();
                foreach (var want in step.Ports!)
                {
                    var got = said.ByType(want.DataType, want.PortNumber);
                    if (string.IsNullOrWhiteSpace(got)) { say($"[C] give {want.DataType}: nothing"); continue; }
                    spoken[want.DataType] = got;
                    say($"[C] give {want.DataType}");
                }
                if (spoken.Count > 0) await devices.PresentAsync(spoken);
                break;
            }

            // The datum names an Application. What running one means belongs to the
            // User Agent, and a User Agent that cannot run one carries on.
            case StepKind.Run:
            {
                var named = step.Labels.FirstOrDefault();
                if (named is null || !data.TryGetValue(named, out var d)) break;
                var app = Plain(d.Json).Trim();
                if (app.Length == 0) { say("run: nothing was chosen"); break; }
                say($"run {app}");
                if (devices.Run is not null) await devices.Run(app);
                break;
            }

            case StepKind.EndLoop:
                throw new LoopEnded();

            default:
                throw new NotSupportedException($"line {step.Line}: {step.Kind} is not executed.");
        }
    }

    private sealed class LoopEnded : Exception { }

    // A COUNTER-OFFER: AN OBJECT WITH A QUALIFIER AND NO DATA. What is passed is
    // always an Object - sometimes data, sometimes a Qualifier, sometimes both.
    // A source that cannot supply what was asked for answers with the Qualifier
    // of what it has and no data, and the User Agent asks again naming that.
    //
    // The Object is deserialised into what it is and asked; no field of the
    // serialisation is named here, because the serialisation is the standard's.
    private static string? CounterOffer(string dataType, string json)
    {
        try
        {
            if (dataType.StartsWith("OSD-BVO", StringComparison.OrdinalIgnoreCase))
            {
                var o = Mpai.Core.MpaiJson.FromJson<Mpai.Core.BasicVisualObject>(json);
                if (o is not null && o.Data.Length == 0 && o.VisualQualifier is not null)
                    return Mpai.Core.MpaiJson.ToJson(o.VisualQualifier);
            }
        }
        catch { /* an Object that will not deserialise is not a counter-offer */ }
        return null;
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

        // A DATUM THAT ARRIVED IS TRUE, unless it is itself a Boolean.
        if (data.TryGetValue(name, out var d))
        {
            var said = d.Json.Trim().Trim('"');
            if (said.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        if (absent.Contains(name)) return false;

        throw new InvalidOperationException(
            $"'{name}' was never obtained, so there is nothing to branch on.");
    }

    // WHICHEVER COMES FIRST. Every alternative is asked for at once; the first to
    // bring a datum is kept and the others are abandoned. A source that brings
    // nothing - silence, an empty line - does not end the wait: another may still
    // come. The labels of the alternatives not taken hold nothing afterwards.
    private async Task FirstOfAsync(Step step, CancellationToken stop)
    {
        foreach (var a in step.Alternatives)
        {
            data.Remove(a.Port!.Label);
            absent.Add(a.Port.Label);
        }
        say("acquire, whichever comes first: " +
            string.Join(" or ", step.Alternatives.Select(a => a.Port!.ToString())));

        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var asked = new Dictionary<Task<string?>, Step>();
        foreach (var a in step.Alternatives)
        {
            try { asked[devices.AcquireAsync(a.Port!.DataType, a.ViaVad, a.Qualifier, abandon.Token)] = a; }
            catch (NotSupportedException ex) { say($"line {step.Line}: {ex.Message}"); abandon.Cancel(); throw; }
        }

        Step?   taken = null;
        string? json  = null;
        var waiting = asked.Keys.ToList();
        while (waiting.Count > 0 && taken is null)
        {
            var done = await Task.WhenAny(waiting);
            waiting.Remove(done);
            string? got = null;
            try { got = await done; }
            catch (OperationCanceledException) { }
            if (got is not null) { taken = asked[done]; json = got; }
        }

        abandon.Cancel();
        foreach (var left in waiting)
            _ = left.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);   // observed, dropped

        if (taken is null) { say("acquire: nothing"); return; }
        data[taken.Port!.Label] = (taken.Port.DataType, json!);
        absent.Remove(taken.Port.Label);
        say($"acquire {taken.Port} (first)");
    }

    private string Known(string label, int line) =>
        data.TryGetValue(label, out var d)
            ? d.Json
            : throw new InvalidOperationException(
                  $"line {line}: '{label}' was never acquired or received.");

    // "{UserName}, welcome." with what is known put in place of the braces.
    // ONE CONTROLLER, ONE MODULE. The workflow declares it once; no step carries
    // it, because a User Agent that named a Module in every request would be
    // saying something the Controller already knows.
    private Workflow? current;
    private string Module => current?.Modules.FirstOrDefault() ?? "";

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