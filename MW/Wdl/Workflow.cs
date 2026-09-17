using System;
using System.Collections.Generic;

namespace Mpai.Wdl;

// A WORKFLOW IS A USER AGENT'S PROGRAM, READ RATHER THAN COMPILED.
//
// Every step is either a request to the Controller or an act of the User Agent's
// own, and never both. That is the organising rule of the notation, and it is why
// an interpreter is small: the Controller requests map one-to-one onto the
// Controller API, and everything else is a device, a screen or a variable.
//
// Nothing here knows what an application is. A workflow names the Modules it
// drives; this model holds the names and nothing more.

// Where a datum goes or comes from: a Data Type and, where a Module declares more
// than one Port of that Data Type, a Port Number. The label is for the reader.
public sealed record PortRef(string Label, string DataType, int PortNumber)
{
    public override string ToString() =>
        PortNumber == 1 ? Label + " (" + DataType + ")"
                        : Label + " (" + DataType + ":" + PortNumber + ")";
}

public enum StepKind
{
    // --- asked of the Controller ---
    StartModule,      // ask Controller to start M
    StopModule,       // ask Controller to stop M
    PauseModule,      // ask Controller to pause M
    ResumeModule,     // ask Controller to resume M
    Take,             // ask Controller to take L (T:n) [= "literal"] for M
    Give,             // ask Controller to give from M: L (T:n), ...

    // --- the User Agent's own ---
    Acquire,          // acquire L (T) [via VAD]
    Type,             // type L (T)
    Prompt,           // prompt "..."
    Display,          // display L
    Present,          // present L [, L...]
    Wait,             // wait 1s
    Set,              // set V = ...
    Loop,             // loop until Stop:
    Branch            // branch on V { ... } else { ... }
}

public sealed class Step
{
    public required StepKind Kind { get; init; }
    public string?  Module        { get; init; }   // which Module the request names
    public PortRef? Port          { get; init; }   // Take, Acquire, Type
    public string?  Literal       { get; init; }   // Take: an inline value, if given
    public IReadOnlyList<PortRef> Ports  { get; init; } = Array.Empty<PortRef>();  // Give
    public IReadOnlyList<string>  Labels { get; init; } = Array.Empty<string>();   // Present, Display
    public string?  Text          { get; init; }   // Prompt: the words
    public string?  Variable      { get; init; }   // Set, Branch
    public string?  Value         { get; init; }   // Set
    public TimeSpan Duration      { get; init; }   // Wait
    public bool     ViaVad        { get; init; }   // Acquire
    public IReadOnlyList<Step> Body { get; init; } = Array.Empty<Step>();  // Loop, Branch
    public IReadOnlyList<Step> Else { get; init; } = Array.Empty<Step>();  // Branch
    public int      Line          { get; init; }   // so a message can name the place

    public bool IsControllerRequest => Kind is
        StepKind.StartModule or StepKind.StopModule or StepKind.PauseModule or
        StepKind.ResumeModule or StepKind.Take or StepKind.Give;
}

// A workflow: the Modules it drives, what it does on Start, and what on Stop.
public sealed class Workflow
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Modules { get; init; } = Array.Empty<string>();
    public IReadOnlyList<Step>   OnStart { get; init; } = Array.Empty<Step>();
    public IReadOnlyList<Step>   OnStop  { get; init; } = Array.Empty<Step>();
}