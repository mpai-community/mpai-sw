# MAT - Developer Guidelines (Software Architecture)

**MAT** (Multimodal Anonymous Translation) is the HCI Module for spoken
translation with a Speaking Avatar. It is the worked example of **MPAI-AIF
(M3175) 4.2**: the User Agent provides an input language, an output language and a
speech (or text) object, and requests the translated speech/text back; the
Controller routes recognition -> translation -> rendering **purely by data type**,
with no notion that this is "translation". This document is for developers.

## 1. MPAI-AIF in brief
- **AIM** - a typed processing unit. **A port is addressed by its data type**
  (and a *Port Number* only where a type repeats on that AIM); the port's *name*
  is a human label, never an address.
- **Module (AIW)** - a composite AIM defined by an **L3** JSON (`AIMs/AMDs/`);
  `Direction` is **`Input`** or **`Output`**.
- **Controller** - builds the Module from its L3 (via a *provider*) and runs it,
  routing **by data type**: a Topology edge's endpoints are resolved once, from
  each AIM's `ExternalPorts` (and the composite's `InternalTypes`), to
  `(DataType, PortNumber)`. No port name is read at runtime; renaming a label
  changes nothing.
- **North API** (`MW/HciApi`, `NorthApi`) - the UA-facing interface. The UA
  supplies/reads `Datum(DataType, PortNumber, json)`; the wire key is
  `DataType#PortNumber`. It names no Module, function, AIM or port.
- **User Agent (UA)** - acquires speech / typed text and delivers the avatar;
  **orchestrates**; described by a **WDL** `.orch` guidebook.

## 2. The MAT Module - `MMC-MAT-V2.5`
L3: `AIMs/AMDs/1MMC-MAT-V2.5-I01.json`. Sub-AIMs (provider leaves):

| Sub-AIM | Role | Engine |
|---|---|---|
| `MMC-ASR-V2.5` | Speech -> text | Whisper (multilingual `ggml-small.bin`) |
| `MMC-TTT-V2.5` | Text -> text translation | M2M100, to the Selector's target language |
| `PAF-RSR-V1.6` (+ `PAF-PSD`, `MMC-TTS`, `PAF-GFD`) | Response & Scene Rendering | speaks the translation in the target voice + lip-sync |

**Boundary inputs (by data type; each occurs once, so no Port Number):**
`InputSpeech` (OSD-BSO, optional), `InputSpeechTime` (OSD-STM, optional),
`LanguageSelector` (OSD-SEL), `TextObject` (OSD-BTO, optional).
**Boundary outputs:** `MachineSpeech` (OSD-BSO / OSD-SPO), `MachineFaceDescriptors`
(PAF-FDO), `TranslatedText` (OSD-BTO).

`InputSpeech` and `TextObject` are the two **alternative** inputs (both
`IsOptional`): a turn supplies one or the other. The `LanguageSelector` (OSD-SEL)
carries **from/to** and is always supplied.

### Internal numbering (invisible to the UA)
Inside the Module the same type repeats, so Port Numbers disambiguate - e.g. TTT
takes `InputText #1` and `RecognisedText #2` (both OSD-BTO), and RSR takes
`TextObject #1` (to TTS) and `#2` (to GFD). These L1/topology numbers are the
standard's; the loader resolves them from the AMDs. Per M3175 4.4, because this
is an automatic sub-sequence not under the UA's step-by-step control, **the UA
cites no numbers** - it supplies the boundary data by type and reads the result.

## 3. The User Agent
`MPAIApps/HCIApps/MatApp/src/` - namespace `HciMat`; provider `MatProvider.cs`;
UA `MainWindow.xaml.cs`, realising `UAs/Orchestration/HCI-MAT.orch`. It drives the
Module through the **North API** by data type:

- **Start (two presses):** press 1 -> `StartFlow("MMC-MAT-V2.5")` (models load) +
  spoken welcome; press 2 -> spoken instructions; the button becomes **Select**.
- **Select:** choose input/output languages (the `LanguageSelector`, OSD-SEL).
- **Typed turn:** `Advance` with `(OSD-SEL)` + `(OSD-BTO)` -> read `(OSD-BTO)`
  TranslatedText -> display it.
- **Spoken turn:** capture speech, **stamp it with the source language** (below),
  `Advance` with `(OSD-SEL)` + `(OSD-BSO)` + `(OSD-STM)` -> read `(OSD-BSO)`
  MachineSpeech + `(PAF-FDO)` + `(OSD-BTO)` -> the avatar speaks; text is shown.
- The Module lives for the app's lifetime (started once); `StopFlow` on close.

### Source language must reach ASR
`MMC-ASR` (Whisper) decodes the language carried by the **input speech object's
own Qualifier** (`SpeechQualifier.Attributes.Metadata.Language.LanguageCode`),
falling back to a static default otherwise. **The UA therefore stamps each
captured speech object with the selected input language** before `Advance`; if it
did not, Whisper would auto-detect / default and could mis-recognise (returning a
"foreign language" placeholder). Language travels *with* the audio - the
"inherit Language from the input Speech Qualifier" that ASR expects.

The typed path does not use ASR: the `LanguageSelector` carries from/to to TTT
directly.

Real-world limbs (microphone capture, avatar rendering) come from
`UAs/Lib/UaKit` (`AvatarUaHost`).

## 4. Files this app needs (build closure)
- **App:** `MPAIApps/HCIApps/MatApp/*`
- **AIF:** `AIF/V3.0/src/{AIF.Controller, AIF.Store, AIF.SharedStorage, AIF.GlobalStorage}`
- **UA library / North API:** `UAs/Lib/UaKit`, `MW/HciApi` (`NorthApi`)
- **AIMs:** `AIMs/Core`; leaves `MMC/V2.5/ASR`, `MMC/V2.5/TTT`, `PAF/V1.6/PSD`,
  `MMC/V2.5/TTS`, `PAF/V1.6/GFD`; audio devices `CAE3/V1.0/AOA(.Windows)`,
  `MMC/V2.5/SOD(.Windows)`
- **L3s:** `1MMC-MAT-V2.5-I01.json` + `1MMC-ASR-V2.5-I01.json` +
  `1MMC-TTT-V2.5-I01.json` + the RSR-leaf AMDs
- **Orchestration:** `UAs/Orchestration/HCI-MAT.orch`
- **Schemas:** the JSON schemas reachable from MAT's data types (incl. OSD-SEL)
- **Settings:** `AIMs/aim-settings.json` - `MMC-ASR-V2.5`
  (`ExecutablePath` = whisper-cli, `ModelPath` = **multilingual** ggml model),
  `MMC-TTT-V2.5` (M2M100 model), `MMC-TTS-V2.5` (Piper voices per language).

## 5. Build & run
```
D:\BI\MPAIApps\HCIApps\MatApp\MatAppBuild.bat     # produces MatApp.exe
D:\BI\MPAIApps\HCIApps\MatApp\MatApp.exe
```
MAT needs **no** Ollama. It **does** need a **multilingual** Whisper model (an
English-only `*.en` model cannot decode other input languages), the M2M100
translation model, and a Piper voice for each output language.

## Models & Prerequisites

The application code is in this package; the model files are **not** (they are large
and separately licensed). Obtain each model below, place it under `Models\`, or set
the corresponding key in `AIMs\aim-settings.json`.

> **Verification:** verify each downloaded file with
> `Get-FileHash <file> -Algorithm SHA256` and record the value in your deployment
> notes.

| Model | Settings key | File | Notes |
|---|---|---|---|
| Whisper (multilingual) | `MMC-ASR-V2.5.ModelPath` | `ggml-small.bin` | **Multilingual** build - NOT `*.en`. From `ggerganov/whisper.cpp`. |
| Whisper CLI | `MMC-ASR-V2.5.ExecutablePath` | `whisper-cli.exe` | whisper.cpp Windows release. |
| Translation (M2M100) | `MMC-TTT-V2.5` model keys | M2M100 (ONNX) | Facebook M2M100 multilingual translation. |
| Piper voices | `MMC-TTS-V2.5` `Voice:<lang>` | `<lang>_*.onnx` (+ `.json`) | One voice per **output** language you enable. |

Install Piper voices under `Models\Piper\voices\<voice>\`; `PiperExecutable` is
the Piper Windows release (`piper.exe`).