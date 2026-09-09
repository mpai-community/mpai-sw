using System;

namespace Mpai.Core;

// Where an AIM reports what it did.
//
// A library must not decide how its messages are presented: a console host
// prints them, a window shows them in a status line, a service writes them to
// a log, and a test ignores them. AIMs therefore report HERE, and the host
// decides what, if anything, happens next.
//
// No sink is installed by default, so an AIM is silent until a host asks to
// hear it.
public static class AimLog
{
    // (AIM identifier, message)
    public static Action<string, string>? Sink { get; set; }

    public static void Write(
        string aimIdentifier,
        string message)
    {
        Sink?.Invoke(aimIdentifier, message);
    }

    // Convenience for hosts that simply want the messages on the console.
    public static void ToConsole()
    {
        Sink = (aim, message) =>
            Console.WriteLine($"[{aim}] {message}");
    }
}

