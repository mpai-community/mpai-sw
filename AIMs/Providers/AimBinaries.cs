using System;
using System.Collections.Generic;
using Mpai.Aims.Asr;
using Mpai.Aims.Tts;
using Mpai.Aims.Ttt;
using Mpai.Mmc.Edp;
using Mpai.Mmc.Nlu;
using Mpai.Mmc.Pmx;
using Mpai.Mmc.Spe;
using Mpai.Mmc.Tiq;
using Mpai.Paf.Fpe;
using Mpai.Paf.Gfd;
using Mpai.Paf.Gbd;
using Mpai.Paf.Sas;
using Mpai.Paf.Sar;
using Mpai.Paf.Psd;

namespace Mpai.Providers;

// WHAT IMPLEMENTS EACH AIM (M3223 3.2): the assembly of its processor - the binary
// its L3 names (Mpai.Mmc.Asr, ...). A Controller that verifies what it runs
// measures this file before it builds the AIM, and a host measures it before it
// places one.
public static class AimBinaries
{
    private static readonly Dictionary<string, Type> Processors = new(StringComparer.Ordinal)
    {
        ["1MMC-ASR-V2.5-I01"] = typeof(AsrAimProcessor),
        ["1MMC-EDP-V2.5-I01"] = typeof(EdpAimProcessor),
        ["1MMC-TTS-V2.5-I01"] = typeof(TtsAimProcessor),
        ["1MMC-TIQ-V2.5-I01"] = typeof(TiqAimProcessor),
        ["1MMC-TTT-V2.5-I01"] = typeof(TttAimProcessor),
        ["1MMC-NLU-V2.5-I01"] = typeof(NluAimProcessor),
        ["1MMC-SPE-V2.5-I01"] = typeof(SpeAimProcessor),
        ["1MMC-PMX-V2.5-I01"] = typeof(PmxAimProcessor),
        ["1PAF-FPE-V1.6-I01"] = typeof(FpeAimProcessor),
        ["1MMC-PDX-V2.5-I01"] = typeof(PsdAimProcessor),
        ["1PAF-GFD-V1.6-I01"] = typeof(GfdAimProcessor),
        ["1PAF-GBD-V1.6-I01"] = typeof(GbdAimProcessor),
        ["1PAF-SAS-V1.6-I01"] = typeof(SasAimProcessor),
        ["1PAF-SAR-V1.6-I01"] = typeof(SarAimProcessor),
        ["1PAF-FIR-V1.6-I01"] = typeof(Mpai.Paf.Fir.FirAimProcessor),
        ["1MMC-SIR-V2.5-I01"] = typeof(Mpai.Mmc.Sir.SirAimProcessor),
        ["1OSD-IDR-V1.5-I01"] = typeof(Mpai.Osd.Idr.IdrAimProcessor),
        ["1PAF-EFD-V1.6-I01"] = typeof(Mpai.Paf.Efd.EfdAimProcessor),
        ["1MMC-ESD-V2.5-I01"] = typeof(Mpai.Mmc.Esd.EsdAimProcessor),
    };

    public static string? Of(string aimName) => Processors.TryGetValue(aimName, out var type) ? type.Assembly.Location : null;
}
