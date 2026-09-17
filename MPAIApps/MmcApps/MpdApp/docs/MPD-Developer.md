# MPD - Developer Guidelines (Software Architecture)

**MPD** (Multimodal Personal Status-based Dialogue) is affective dialogue: the
CAV perceives the **meaning** and the **feeling** of what a person says and replies
aware of both, spoken by an expressive Speaking Avatar. It is **MAD plus a
Personal-Status front-end** - the dialogue and rendering half is the same as MAD;
MPD inserts perception (NLU + PSE) before the dialogue AIM so the reply is shaped
by the person's emotional state and the machine renders its own. This document is
for developers; see the MAD Developer guide for the dialogue/rendering half.

## 1. MPAI-AIF in brief
- **AIM** - a typed processing unit. A port is addressed by its **data type**
  (and a Port Number only where a type repeats); the port's name is a human
  label, never an address.
- **Module (AIW)** - a composite AIM defined by an **L3** JSON (`AIMs/AMDs/`);
  `Direction` is `Input` or `Output`.
- **Controller** - builds the Module from its L3 and runs it, routing by data
  type (endpoints resolved once from `ExternalPorts`/`InternalTypes`). No port
  name is read at runtime.
- **North API** (`MW/HciApi`, `NorthApi`) - the UA-facing interface: the UA
  supplies/reads `Datum(DataType, PortNumber, json)`.
- **User Agent (UA)** - acquires speech and a webcam face, delivers the avatar,
  and orchestrates; described by a WDL `.orch` guidebook.

## 2. The MPD Module - `MMC-MPD-V2.5`
L3: `AIMs/AMDs/1MMC-MPD-V2.5-I01.json`. The pipeline is ASR -> NLU -> PSE -> EDP -> RSR:

| Sub-AIM | Role | Engine |
|---|---|---|
| `MMC-ASR-V2.5` | Speech -> text | Whisper (multilingual) |
| `MMC-NLU-V2.5` | Meaning + Text Personal Status | first-pass tagger + affect lexicon |
| `MMC-PSE-V2.5` | Personal Status Extraction (composite) | ESI (speech) + EFI (face) + PSM (multiplex) |
| `MMC-EDP-V2.5` | Affective dialogue | local LLM via Ollama; memory in the Summary |
| `PAF-RSR-V1.6` | Response & Scene Rendering | speaks + renders the machine Personal Status |

**Boundary inputs (by data type):** `InputSpeech` (OSD-BSO), `InputSpeechTime`
(OSD-STM), `InputFace` (OSD-BVO), `Summary` (MMC-SUM) - all optional.
**Boundary outputs:** `OutputSpeech` (OSD-BSO / OSD-SPO), `OutputFaceDescriptors`
(PAF-FDO), `EditedSummary` (MMC-SUM).

Personal Status flows inside the Module: NLU produces a Text Personal Status;
`InputSpeech` also feeds PSE, and `InputFace` feeds PSE; PSE multiplexes the Text,
Speech and Face Personal Statuses into one Entity Personal Status, which EDP
consumes to reply with affect and to emit its **own** machine Personal Status;
RSR renders that on the avatar's face and voice.

### PSE is a nested composite
`MMC-PSE-V2.5` is itself a composite (`AIMs/AMDs/1MMC-PSE-V2.5-I01.json`) of
**MMC-ESI** (speech affect), **MMC-EFI** (face affect) and **MMC-PSM** (multiplex).
Every PSE input is optional and PSM combines whatever is present ("it combines; it
does not compute"), so MPD degrades gracefully: with no face, it uses text + speech
affect; with no speech-emotion model, it uses text + face. Gesture (MMC-GPS) is a
declared but unsupplied optional input (not implemented).

## 3. The User Agent
`MPAIApps/HCIApps/MpdApp/src/` - namespace `MpdApp`; provider `MpdProvider.cs`;
UA `MainWindow.xaml.cs`. It drives the Module through the **North API** by data type:

- **Start (welcome/load):** `StartFlow("MMC-MPD-V2.5")` loads the models behind a
  spoken welcome; when ready the avatar announces the service and listening begins
  automatically (the Stop button appears once the user has spoken).
- **Each turn:** capture speech and a webcam **face**; `Advance` with `(OSD-BSO)`
  + `(OSD-STM)` + `(OSD-BVO)`; read `(OSD-BSO)` reply + `(PAF-FDO)` avatar. The
  Module stays alive so EDP keeps the running Summary as memory.

`MpdProvider` supplies the leaves: ASR, NLU, ESI, EFI, PSM, EDP, and the RSR
leaves PSD/TTS/GFD, reusing the framework's factories.

## 4. Affect in the voice
`MMC-TTS` accepts an optional Speech Personal Status and maps its emotion to Piper
prosody (speaking rate + variation), so the voice - not only the face - carries the
feeling. Piper is prosody-shaping, not an emotional synthesiser, so the effect is
"livelier/slower with mood" rather than fully dramatic. Absent Personal Status =>
neutral synthesis (so MAD/MAT/MAC are unchanged).

`MMC-EDP` returns a single compact JSON object (response, emotion, attitude,
summary) in one call; the processor extracts the reply robustly and never lets JSON
reach the spoken text, and the user's affect conditions the reply's tone without
being named back to the user.

## 5. Files this app needs (build closure)
- **App:** `MPAIApps/HCIApps/MpdApp/*`
- **AIF:** `AIF/V3.0/src/{AIF.Controller, AIF.Store, AIF.SharedStorage, AIF.GlobalStorage}`
- **UA library / North API:** `UAs/Lib/UaKit`, `MW/HciApi`
- **AIMs:** `AIMs/Core`; leaves `MMC/V2.5/{ASR, NLU, ESI, EFI, PSM, EDP, TTS}`,
  `PAF/V1.6/{PSD, GFD}`; audio devices `CAE3/V1.0/AOA(.Windows)`,
  `MMC/V2.5/SOD(.Windows)`; webcam `CVE/V1.0/VOA.Windows`
- **L3s:** `1MMC-MPD`, `1MMC-PSE`, and the sub-AIM AMDs
- **Orchestration:** `UAs/Orchestration/HCI-MPD.orch`
- **Settings:** `AIMs/aim-settings.json` - `MMC-ASR` (Whisper), `MMC-EDP`
  (`OllamaModel`), `MMC-ESI` (`W2v2Model`), `MMC-EFI` (`HseModel`), `MMC-TTS` (Piper).

## 6. Build & run
```
D:\BI\MPAIApps\HCIApps\MpdApp\MpdAppBuild.bat     # produces MpdApp.exe
# start Ollama first (serve + model), then:
D:\BI\MPAIApps\HCIApps\MpdApp\MpdApp.exe
```
MPD needs **Ollama running** with the configured model, a **multilingual Whisper**
model, and the two emotion models below.

## Models & Prerequisites

The application code is in this package; the model files are **not** (they are large
and separately licensed). Obtain each, place it under `Models\`, or set the
corresponding key in `AIMs\aim-settings.json`. Verify each download with
`Get-FileHash <file> -Algorithm SHA256`.

| Model | Settings key | File | Notes |
|---|---|---|---|
| Whisper (multilingual) | `MMC-ASR-V2.5.ModelPath` | `ggml-small.bin` | Multilingual build. |
| Local LLM | `MMC-EDP-V2.5.OllamaModel` | (Ollama model) | e.g. `llama3.2:3b`, served on 127.0.0.1:11434. |
| Speech emotion (wav2vec2) | `MMC-ESI-V2.5.W2v2Model` | `w2v2-emotion\model.onnx` | audeering w2v2 dimensional emotion; ships as `w2v2-emotion.zip`. |
| Face emotion (HSEmotion) | `MMC-EFI-V2.5.HseModel` | `hsemotion_enet_b0_8_va_mtl.onnx` | EfficientNet-B0 face affect. |
| Piper voice(s) | `MMC-TTS-V2.5` `Voice:<lang>` | `<lang>_*.onnx` (+ `.json`) | one per output language. |
