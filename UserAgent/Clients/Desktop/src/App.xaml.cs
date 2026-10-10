using System;
using System.IO;
using System.Windows;

namespace MpaiRca;

// An AIM is silent until a host asks to hear it, so this host asks.
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Mpai.Core.AimLog.Sink = (aim, message) =>
        {
            try
            {
                File.AppendAllText(Program.CrashLog,
                    $"{DateTime.Now:o}  [{aim}]  {message}{Environment.NewLine}");
            }
            catch { }
        };
    }
}