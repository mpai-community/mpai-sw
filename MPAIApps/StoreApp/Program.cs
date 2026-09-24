using System;
using System.Windows.Forms;

namespace MPAIApps.StoreApp;

// ============================================================================
//  StoreApp - a standing window where an Implementer submits an L3 (an AIM
//  Metadata instance) to the MPAI Store. The Store Service validates it against
//  its L2 and looks for its package - signalling what it finds - and refuses it
//  only if a Sub-AIM's L3 is in neither the package nor the Store.
//
//  The Store's address comes from the environment variable MPAI_STORE
//  (default https://localhost:5020/).
// ============================================================================
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new StoreForm());
    }
}
