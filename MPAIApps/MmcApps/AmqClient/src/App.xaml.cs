using System;
using System.IO;
using System.Windows;

namespace MmcAmq;

// AN AIM IS SILENT UNTIL A HOST ASKS TO HEAR IT. AimLog installs no sink by
// default - deliberately, since a library must not decide how its messages are
// presented - so a windowed application that never attaches one receives nothing
// from any AIM it runs. Every warning those AIMs were written to give goes
// nowhere, and a fault announces itself only by its consequences: an avatar that
// does not speak, an enrolment that half-completes.
//
// The messages go where the crash log goes: one file beside the executable, so
// that "it did not work and said nothing" becomes "it did not work and here is
// what it said".
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Mpai.Core.AimLog.Sink = (aim, message) =>
        {
            try
            {
                File.AppendAllText(
                    Program.CrashLog,
                    $"{DateTime.Now:o}  [{aim}]  {message}{Environment.NewLine}");
            }
            catch
            {
                // A log that cannot be written must not stop the application.
            }
        };
    }
}