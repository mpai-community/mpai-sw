using System;
using System.IO;
using Mpai.Wdl;

// Reads every workflow description it is given and prints what it understood.
// Not a test of behaviour - a test that the notation and the reader agree.
internal static class Program
{
    private static int Main(string[] args)
    {
        var dir = args.Length > 0 ? args[0] : @"D:\BI\UAs\Orchestration";
        var reader = new WorkflowReader();
        int bad = 0;

        foreach (var path in Directory.EnumerateFiles(dir, "*.orch"))
        {
            Console.WriteLine("=== " + Path.GetFileName(path));
            try
            {
                var w = reader.Read(File.ReadAllText(path));
                Console.WriteLine("    workflow " + w.Name + " over " + string.Join(", ", w.Modules));
                Console.WriteLine("    on Start: " + w.OnStart.Count + " steps   on Stop: " + w.OnStop.Count + " steps");
                foreach (var s in w.OnStart) Console.WriteLine("      " + Describe(s));
            }
            catch (Exception ex) { bad++; Console.WriteLine("    " + ex.Message); }
            Console.WriteLine();
        }
        return bad;
    }

    private static string Describe(Step s)
    {
        switch (s.Kind)
        {
            case StepKind.StartModule: return "[C] start   " + s.Module;
            case StepKind.StopModule:  return "[C] stop    " + s.Module;
            case StepKind.Take:        return "[C] take    " + s.Port + " for " + s.Module +
                                              (s.Literal is null ? "" : "  = \"" + Short(s.Literal) + "\"");
            case StepKind.Give:        return "[C] give    from " + s.Module + ": " + string.Join(", ", s.Ports);
            case StepKind.Acquire:     return "    acquire " + s.Port + (s.ViaVad ? " via VAD" : "");
            case StepKind.Type:        return "    type    " + s.Port;
            case StepKind.Prompt:      return "    prompt  \"" + Short(s.Text ?? "") + "\"";
            case StepKind.Present:     return "    present " + string.Join(", ", s.Labels);
            case StepKind.Display:     return "    display " + string.Join(", ", s.Labels);
            case StepKind.Wait:        return "    wait    " + s.Duration.TotalSeconds + "s";
            default:                   return "    " + s.Kind;
        }
    }

    private static string Short(string t) =>
        t.Length <= 46 ? t.Replace("\n", " ") : t.Substring(0, 46).Replace("\n", " ") + "...";
}