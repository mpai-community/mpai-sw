using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mpai.Wdl;

// READS A WORKFLOW DESCRIPTION. Line-oriented, and deliberately strict: a line it
// does not recognise is an error naming the line number, not a line skipped. A
// workflow obtained from a Service and run by an interpreter must fail where it is
// wrong, not where the consequence shows.
//
// A continuation is a line that does not begin with a step keyword, which is what
// lets a long "take" carry its text over several lines as the reference workflows
// do. Indentation is for the reader, not for the reader of this file.
public sealed class WorkflowReader
{
    public sealed class WorkflowSyntaxError : Exception
    {
        public WorkflowSyntaxError(int line, string what)
            : base("line " + line + ": " + what) { }
    }

    private static readonly Regex Header =
        new(@"^workflow\s+(?<name>\S+)\s+over\s+(?<modules>.+)$", RegexOptions.Compiled);

    private static readonly Regex Ask =
        new(@"^ask\s+Controller\s+to\s+(?<verb>start|stop|pause|resume|take|give)\b\s*(?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Datum =
        new(@"^(?<label>[A-Za-z_][\w]*)\s*\(\s*(?<type>[^):]+?)\s*(?::\s*(?<pn>\d+)\s*)?\)\s*(?:=\s*(?<lit>.*))?$",
            RegexOptions.Compiled);

    public Workflow Read(string text)
    {
        var lines = Normalise(text);
        if (lines.Count == 0) throw new WorkflowSyntaxError(0, "the workflow is empty.");

        var m = Header.Match(lines[0].Text.Trim());
        if (!m.Success)
            throw new WorkflowSyntaxError(lines[0].Line,
                "a workflow begins 'workflow <name> over <Module>[, <Module>...]'.");

        var name    = m.Groups["name"].Value;
        var modules = m.Groups["modules"].Value.Split(',')
                       .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        var onStart = new List<Step>();
        var onStop  = new List<Step>();
        List<Step>? current = null;

        int i = 1;
        while (i < lines.Count)
        {
            var line = lines[i].Text.Trim();

            if (line.Equals("on Start:", StringComparison.OrdinalIgnoreCase)) { current = onStart; i++; continue; }
            if (line.Equals("on Stop:",  StringComparison.OrdinalIgnoreCase)) { current = onStop;  i++; continue; }

            if (current is null)
                throw new WorkflowSyntaxError(lines[i].Line, "a step outside 'on Start:' or 'on Stop:'.");

            current.Add(ReadOne(lines, ref i));
        }

        return new Workflow { Name = name, Modules = modules, OnStart = onStart, OnStop = onStop };
    }

    // READS ONE STEP, AND WHERE IT IS A BLOCK, WHAT IS INSIDE IT. A block owns
    // every following line indented further than itself:
    //
    //     loop until Stop:                branch on Identification {
    //         acquire ...                     present ...
    //         ask Controller to ...       } else {
    //                                         display ...
    //                                     }
    //
    // Indentation is the structure in the first form and braces in the second,
    // which is the notation the four reference workflows already use. A block
    // that ends because the indent fell back is closed without ceremony.
    private Step ReadOne(List<SourceLine> lines, ref int i)
    {
        var here = lines[i];
        var line = here.Text.Trim();

        if (line.StartsWith("loop ", StringComparison.OrdinalIgnoreCase))
        {
            if (!line.TrimEnd().EndsWith(":", StringComparison.Ordinal))
                throw new WorkflowSyntaxError(here.Line, "'loop until Stop:' expects a ':'.");
            i++;
            var body = ReadBlock(lines, ref i, here.Indent);
            return new Step { Kind = StepKind.Loop, Body = body, Line = here.Line };
        }

        if (line.StartsWith("branch ", StringComparison.OrdinalIgnoreCase))
        {
            // branch on <name> {
            var m = Regex.Match(line, @"^branch\s+on\s+(?<v>[A-Za-z_][\w]*)\s*\{?\s*$",
                                RegexOptions.IgnoreCase);
            if (!m.Success)
                throw new WorkflowSyntaxError(here.Line, "'branch' expects 'branch on <name> {'.");
            i++;
            var body = ReadBraced(lines, ref i, out var hasElse);
            var alt  = hasElse ? ReadBraced(lines, ref i, out _) : new List<Step>();
            return new Step { Kind = StepKind.Branch, Variable = m.Groups["v"].Value,
                              Body = body, Else = alt, Line = here.Line };
        }

        i++;
        return ReadStep(here.Line, line);
    }

    // Everything indented further than the line that opened the block.
    private List<Step> ReadBlock(List<SourceLine> lines, ref int i, int openedAt)
    {
        var body = new List<Step>();
        while (i < lines.Count && lines[i].Indent > openedAt)
        {
            var t = lines[i].Text.Trim();
            if (t.Equals("on Start:", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("on Stop:",  StringComparison.OrdinalIgnoreCase)) break;
            body.Add(ReadOne(lines, ref i));
        }
        return body;
    }

    // Everything up to the closing brace. '} else {' both closes and reopens.
    private List<Step> ReadBraced(List<SourceLine> lines, ref int i, out bool hasElse)
    {
        var body = new List<Step>();
        hasElse = false;

        while (i < lines.Count)
        {
            var t = lines[i].Text.Trim();

            if (Regex.IsMatch(t, @"^\}\s*else\s*\{?$", RegexOptions.IgnoreCase))
            { hasElse = true; i++; return body; }

            if (t == "}") { i++; return body; }

            body.Add(ReadOne(lines, ref i));
        }
        throw new WorkflowSyntaxError(lines.Count > 0 ? lines[lines.Count - 1].Line : 0,
                                      "a 'branch' was opened and never closed.");
    }

    private Step ReadStep(int n, string line)
    {
        var ask = Ask.Match(line);
        if (ask.Success)
            return ReadRequest(n, ask.Groups["verb"].Value.ToLowerInvariant(),
                                  ask.Groups["rest"].Value.Trim());

        var (verb, rest) = Split(line);
        switch (verb.ToLowerInvariant())
        {
            case "acquire":
            {
                var viaVad = rest.EndsWith("via VAD", StringComparison.OrdinalIgnoreCase);
                if (viaVad) rest = rest.Substring(0, rest.Length - "via VAD".Length).Trim();
                return new Step { Kind = StepKind.Acquire, Port = ReadDatum(n, rest).Port,
                                  ViaVad = viaVad, Line = n };
            }
            case "type":
                return new Step { Kind = StepKind.Type, Port = ReadDatum(n, rest).Port, Line = n };

            case "prompt":
                return new Step { Kind = StepKind.Prompt, Text = Unquote(rest), Line = n };

            case "display":
                return new Step { Kind = StepKind.Display, Labels = Names(rest), Line = n };

            case "present":
                return new Step { Kind = StepKind.Present, Labels = Names(rest), Line = n };

            case "wait":
                return new Step { Kind = StepKind.Wait, Duration = ReadDuration(n, rest), Line = n };

            case "set":
            {
                var eq = rest.IndexOf('=');
                if (eq < 0) throw new WorkflowSyntaxError(n, "'set' expects '<name> = <value>'.");
                return new Step { Kind = StepKind.Set,
                                  Variable = rest.Substring(0, eq).Trim(),
                                  Value = Unquote(rest.Substring(eq + 1).Trim()), Line = n };
            }
            default:
                throw new WorkflowSyntaxError(n, "'" + verb + "' is not a step of this notation.");
        }
    }
    private Step ReadRequest(int n, string verb, string rest)
    {
        switch (verb)
        {
            case "start":  return new Step { Kind = StepKind.StartModule,  Module = rest, Line = n };
            case "stop":   return new Step { Kind = StepKind.StopModule,   Module = rest, Line = n };
            case "pause":  return new Step { Kind = StepKind.PauseModule,  Module = rest, Line = n };
            case "resume": return new Step { Kind = StepKind.ResumeModule, Module = rest, Line = n };

            case "take":
            {
                // <datum> for <Module>
                int at = rest.LastIndexOf(" for ", StringComparison.Ordinal);
                if (at < 0) throw new WorkflowSyntaxError(n, "'take' expects '<datum> for <Module>'.");
                var d = ReadDatum(n, rest.Substring(0, at).Trim());
                return new Step { Kind = StepKind.Take, Port = d.Port, Literal = d.Literal,
                                  Module = rest.Substring(at + 5).Trim(), Line = n };
            }

            case "give":
            {
                // from <Module>: <datum>, <datum>, ...
                if (!rest.StartsWith("from ", StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowSyntaxError(n, "'give' expects 'from <Module>: <datum>[, <datum>...]'.");
                int colon = rest.IndexOf(':');
                if (colon < 0) throw new WorkflowSyntaxError(n, "'give' expects a ':' after the Module.");

                var module = rest.Substring(5, colon - 5).Trim();
                var ports  = SplitData(rest.Substring(colon + 1))
                             .Select(d => ReadDatum(n, d).Port).ToList();
                if (ports.Count == 0) throw new WorkflowSyntaxError(n, "'give' names no datum.");
                return new Step { Kind = StepKind.Give, Module = module, Ports = ports, Line = n };
            }
            default:
                throw new WorkflowSyntaxError(n, "the Controller is not asked to '" + verb + "'.");
        }
    }

    private (PortRef Port, string? Literal) ReadDatum(int n, string s)
    {
        var m = Datum.Match(s.Trim());
        if (!m.Success)
            throw new WorkflowSyntaxError(n, "'" + s.Trim() + "' is not '<label> (<DataType>[:<PortNumber>])'.");

        var pn = m.Groups["pn"].Success
            ? int.Parse(m.Groups["pn"].Value, CultureInfo.InvariantCulture) : 1;
        var port = new PortRef(m.Groups["label"].Value, m.Groups["type"].Value.Trim(), pn);
        var lit  = m.Groups["lit"].Success ? Unquote(m.Groups["lit"].Value.Trim()) : null;
        return (port, lit);
    }

    // ---- lines -------------------------------------------------------------

    private readonly record struct SourceLine(int Line, string Text, int Indent);

    // Strips comments and blank lines, and joins a continuation onto the line it
    // continues - a line that does not begin with a step keyword. That is what
    // lets a long 'take' carry its text over several lines.
    private static List<SourceLine> Normalise(string text)
    {
        var raw = text.Replace("\r\n", "\n").Split('\n');
        var joined = new List<SourceLine>();

        for (int i = 0; i < raw.Length; i++)
        {
            var s = raw[i];
            int hash = IndexOfComment(s);
            if (hash >= 0) s = s.Substring(0, hash);
            var t = s.Trim();
            if (t.Length == 0) continue;

            int indent = s.Length - s.TrimStart().Length;

            if (joined.Count > 0 && IsContinuation(t))
            {
                var last = joined[joined.Count - 1];
                joined[joined.Count - 1] = new SourceLine(last.Line, last.Text + " " + t, last.Indent);
            }
            else joined.Add(new SourceLine(i + 1, t, indent));
        }
        return joined;
    }

    private static readonly string[] Starters =
    {
        "workflow ", "on Start:", "on Stop:", "ask ", "acquire ", "type ", "prompt ",
        "display ", "present ", "wait ", "set ", "loop ", "branch "
    };

    // A brace stands on its own: it closes a block and is not a continuation
    // of the line above it.
    private static bool IsContinuation(string t) =>
        !t.StartsWith("}", StringComparison.Ordinal) &&
        !Starters.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // A '#' inside quotes is text, not a comment.
    private static int IndexOfComment(string s)
    {
        bool inQuote = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') inQuote = !inQuote;
            else if (s[i] == '#' && !inQuote) return i;
        }
        return -1;
    }

    private static (string Verb, string Tail) Split(string line)
    {
        int sp = line.IndexOf(' ');
        return sp < 0 ? (line, string.Empty)
                      : (line.Substring(0, sp), line.Substring(sp + 1).Trim());
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"'
            ? s.Substring(1, s.Length - 2) : s;

    private static List<string> Names(string s) =>
        s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    // Splits on commas that are not inside parentheses or quotes.
    private static List<string> SplitData(string s)
    {
        var parts = new List<string>();
        int depth = 0; bool q = false; int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"') q = !q;
            else if (!q && c == '(') depth++;
            else if (!q && c == ')') depth--;
            else if (!q && depth == 0 && c == ',') { parts.Add(s.Substring(start, i - start)); start = i + 1; }
        }
        parts.Add(s.Substring(start));
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    private static TimeSpan ReadDuration(int n, string s)
    {
        s = s.Trim();
        if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(s.Substring(0, s.Length - 2), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out var ms))
            return TimeSpan.FromMilliseconds(ms);
        if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out var sec))
            return TimeSpan.FromSeconds(sec);
        throw new WorkflowSyntaxError(n, "'" + s + "' is not a duration, such as 1s or 250ms.");
    }
}