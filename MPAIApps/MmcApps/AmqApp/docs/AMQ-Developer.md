# AMQ - Developer Guidelines (Software Architecture)

**AMQ** (Audio-Visual Multimodal Question Answering) answers a question about an
image. The user shows a picture, asks aloud or in writing, and a Speaking Avatar
answers. This document is for developers.

This is the **standalone** application: it runs its Module in process. Driving a
Module over a network is `AmqClient`, a different application for a different
user, documented separately.

---

## 1. MPAI-AIF in brief

- **AIM** - a typed processing unit. **A Port is addressed by its Data Type and
  its Port Number**; the Port's name is a label for the reader and never an
  address. A Module may declare two Ports of one Data Type, and a name cannot
  distinguish them.
- **Module** - a composite AIM defined by an **L3** descriptor (JSON, under
  `AIMs/AMDs/`): its sub-AIMs, its boundary Ports, its internal Topology.
- **Controller** - builds the Module from its L3 through a *provider* and runs
  it, routing by (Data Type, Port Number). Renaming a Port changes nothing.
- **North API** (`MW/HciApi`, `INorthApi`) - the User-Agent-facing interface. The
  User Agent supplies and reads `Datum(DataType, PortNumber, json)`.
- **User Agent** - acquires speech, presents the avatar, and drives the
  Controller. It is not part of the Module.
- **Qualifiers** - every Object carries a Qualifier stating what its Data is: the
  sampling frequency, the precision, the container. An Object cannot be
  constructed without one, and a Qualifier that states no format is refused.

---

## 2. The AMQ Module - `MMC-AMQ-V2.5`

L3: `AIMs/AMDs/1MMC-AMQ-V2.5-I01.json`. Sub-AIMs:

| Sub-AIM | Role | Engine |
|---|---|---|
| `MMC-ASR-V2.5` | Speech to text - the spoken question | whisper.cpp (`whisper-cli` + a `ggml` model) |
| `MMC-TIQ-V2.5` | Text and Image Question - the answer | BLIP VQA, ONNX Runtime |
| `MMC-TTS-V2.5` | Text to speech - the spoken answer | Piper |

**Boundary in:** the image (`OSD-BVO-V1.5`), and the question either as speech
(`OSD-BSO-V1.5`) or as text (`OSD-BTO-V1.5`).
**Boundary out:** the answer as text (`OSD-BTO-V1.5`) and as speech
(`OSD-BSO-V1.5`).

The question may arrive either way. `MMC-ASR` is skipped when the question was
typed - an AIM whose inputs are absent does not run, and the Controller reports
it rather than failing.

---

## 3. The welcome, and `PAF-RSR-V1.6`

The avatar's greeting is rendered by **PAF-RSR** (Response and Scene Rendering),
a composite realised by `PAF-PSD` + `MMC-TTS` + `PAF-GFD`. The User Agent starts
it, supplies the words, collects the speech and the face descriptors, presents
them, and stops it.

**PAF-RSR declares `OSD-BTO-V1.5` twice**: Port 1 feeds Text-To-Speech, Port 2
feeds Generative Face Description. Both must be supplied. Supply only the first
and the avatar speaks with a face that does not move to the words; supply only
the second and it mouths in silence. This is why a Port is addressed by its
number and not by its name.

---

## 4. The User Agent

`MPAIApps/MmcApps/AmqApp/src/` - namespace `MmcAmq`.

| File | What it is |
|---|---|
| `MainWindow.xaml(.cs)` | The window and the flow: load an image, ask, present the answer. |
| `AmqProvider.cs` | The provider: which implementation each AIM name resolves to. |
| `App.xaml.cs` | Installs the AIM log sink, so an AIM that reports is heard. |
| `Program.cs` | Where the crash log is written. |

The User Agent constructs `NorthApi` directly:

```csharp
_north = new NorthApi(AmdDir, SettingsPath, store => new AmqProvider(store));
```

It has no knowledge of MPAI-MAS. The networked client is a separate application.

---

## 5. Build closure

`AmqApp.csproj` references these transitively - 20 projects, computed by
following `ProjectReference` rather than listed by hand:

```
AIF/V3.0/src/AIF.Controller       the runtime
AIF/V3.0/src/AIF.Store            AIM Metadata
AIF/V3.0/src/AIF.SharedStorage    governed storage
AIF/V3.0/src/AIF.GlobalStorage    (legacy name for the same; see Notes)
AIMs/Core                         Data Types, Qualifiers, JSON, paths
AIMs/MMC/V2.5/{ASR,TIQ,TTS,SOA,SOD,SOD.Windows}
AIMs/PAF/V1.6/{PSD,GFD}
AIMs/CAE3/V1.0/{AOA,AOA.Windows,AOD}
AIMs/OSD/V1.5/TOD
MW/HciApi                         INorthApi
UAs/Lib/UaKit                     the Speaking-Avatar host
MPAIApps/MmcApps/AmqApp           the application
```

Also required at run time: `AIMs/AMDs/` (the L3s of `MMC-AMQ`, `MMC-ASR`,
`MMC-TIQ`, `MMC-TTS`, `PAF-RSR`, `PAF-PSD`, `PAF-GFD`, `MMC-SOA`),
`AIMs/aim-settings.json`, `schemas/`, and `UAs/Assets/`.

Build with `MPAIApps\MmcApps\AmqApp\AmqAppBuild.bat`. It resolves its own
location, builds in place, and produces
`src\bin\Release\net10.0-windows10.0.19041.0\AmqApp.exe`.

---

## 6. Models and prerequisites

Not distributed with the software. Obtain each separately and place it under
`Models\` beside the application root, or name it by key in
`AIMs/aim-settings.json`.

| Model | Settings key | Under `Models\` |
|---|---|---|
| whisper.cpp executable | `MMC-ASR-V2.5` / `ExecutablePath` | `Whisper\bin\whisper-cli.exe` |
| whisper model | `MMC-ASR-V2.5` / `ModelPath` | `Whisper\models\ggml-*.bin` |
| BLIP vision | `MMC-TIQ-V2.5` / `VisionModel` | `BLIP\onnx\blip_vision_model.onnx` |
| BLIP text encoder | `MMC-TIQ-V2.5` / `EncoderModel` | `BLIP\onnx\blip_text_encoder_wrapper.onnx` |
| BLIP decoder | `MMC-TIQ-V2.5` / `DecoderModel` | `BLIP\onnx\blip_decoder_context_dynamic.onnx` |
| BLIP vocabulary | `MMC-TIQ-V2.5` / `VocabFile` | `BLIP\blip-vqa-base\vocab.txt` |
| Piper executable | `MMC-TTS-V2.5` / `PiperExecutable` | `Piper\piper_windows_amd64\piper\piper.exe` |
| Piper voice | `MMC-TTS-V2.5` / `Voice:en` | `Piper\voices\en_US-amy-medium\*.onnx` |

Verify a download with `Get-FileHash <file> -Algorithm SHA256`.

**An ONNX model is often two files.** The `.onnx` holds the network's structure
and may be only a megabyte; the `.onnx.data` beside it holds the weights and may
be hundreds. A copy that brings one and not the other produces a model that loads
and fails at first use.

**A setting that names a path is resolved against the application's own root.** A
relative path works wherever the folder is placed; an absolute one binds the
installation to one machine.

**espeak-ng** is optional. Without it `PAF-GFD` cannot obtain phonemes and the
avatar's mouth follows the spelling rather than the sounds. The AIM says so once
per run.

---

## 7. What it reports, and where

Every AIM reports through `AimLog`; the window installs a sink writing to
`amq-crash.log` beside the executable. A windowed application has no console, so
an AIM that wrote to one would be talking to nobody.

| Line | Meaning |
|---|---|
| `[MMC-ASR-V2.5] heard: ...` | What the speech recogniser made of the question. |
| `[MMC-TTS-V2.5] the ... voice failed: ...` | Text-to-speech could not synthesise. |
| `[PAF-GFD-V1.6] phonemes unavailable ...` | espeak-ng absent; mouth follows spelling. |
| `[CVE-VOA-V1.0] acquired ... bytes` | A camera frame, with its size and whether a Qualifier was built. |
| `suspended, waiting for OSD-...#n` | A required boundary input was never supplied. The Port named is the diagnosis. |

---

## 8. Notes

`AIF.GlobalStorage` is the former name of Shared Storage. It is referenced and
unused, and is a candidate for removal.

`MMC-SOD`, `CAE-AOA`, `CAE-AOD` and `OSD-TOD` are constructed directly by the
User Agent rather than named in a Module's Metadata - acquisition and delivery
are the User Agent's business, and the Controller never sees them. An
application's AIM set therefore cannot be computed from its Metadata alone.

---

## Licence

BSD 3-Clause. See `LICENSE`.
