# MAD - Developer Guidelines (Software Architecture)

**MAD** (Multimodal Anonymous Dialogue) lets a person hold a spoken conversation
with a Speaking Avatar. There is **no identity and no affect input**: the user is
anonymous and the machine renders neutrally. This document is for developers.

## 1. MPAI-AIF in brief
- **AIM** - typed processing unit; **only data types matter at a port**.
- **Module (AIW)** - composite AIM defined by an **L3** JSON (`AIMs/AMDs/`).
- **Controller** - builds the Module from its L3 via a *provider*; runs it;
  exposes `MODULE_Start/RunAsync/Stop`.
- **User Agent (UA)** - acquires speech (VAD-gated) and delivers the avatar;
  **orchestrates** the Controller; described by a **WDL** `.orch` guidebook.
- **Local LLM** - the dialogue is produced by a local model served by **Ollama**.

## 2. The MAD Module - `MMC-MAD-V2.5`
L3: `AIMs/AMDs/1MMC-MAD-V2.5-I01.json`. Sub-AIMs (provider leaves):

| Sub-AIM | Role | Engine |
|---|---|---|
| `MMC-ASR-V2.5` | Speech -> text | Whisper (`whisper-cli` + `ggml-small.bin`) |
| `MMC-EDP-V2.5` | Dialogue | local LLM via **Ollama** (`llama3.2:3b`) |
| `PAF-RSR-V1.6` (+ `PAF-PSD`, `MMC-TTS`, `PAF-GFD`) | Response & Scene Rendering | text -> speech + avatar |

**Boundary in:** `InputSpeech` (OSD-BSO) + `InputSpeechTime` (OSD-STM) *or*
`TextObject`, and `Summary` (MMC-SUM). **Boundary out:** `MachineSpeech`
(OSD-BSO), `MachineFaceDescriptors` (PAF-FDO), `EditedSummary` (MMC-SUM).

**Conversation memory** is the running **Summary**: each turn the UA supplies the
current `Summary` and receives an `EditedSummary`, which it carries into the next
turn. This is how MAD "remembers" without any per-session server state.

**EDP input->output rule (affect gating):** EDP produces a machine **Personal
Status only if a Personal Status was provided as input**. MAD provides none, so
EDP is asked for a **plain spoken reply** and emits **no** Personal Status; the
avatar renders neutrally. (Absent inputs are not referenced in the LLM prompt.)

## 3. The User Agent
`MPAIApps/HCIApps/MadApp/src/` - namespace `HciMad`; provider `MadProvider.cs`;
UA `MainWindow.xaml.cs`, realising `UAs/Orchestration/HCI-MAD.orch`.

Flow (turn-taking loop, bounded by the **Start** and **Stop** buttons):
- **on Start:** speak a fixed **welcome** (*"Welcome to the HCI Multimodal
  Dialogue Service."*) via a one-shot RSR render, then enter the loop.
- **each turn:** wait for the user to speak; **VAD** detects end-of-utterance;
  `MODULE_Start("MMC-MAD-V2.5")` -> `RunAsync{InputSpeech, InputSpeechTime,
  Summary}` -> read `MachineSpeech`/`MachineFaceDescriptors`/`EditedSummary` ->
  the avatar speaks the reply -> `Summary = EditedSummary` -> `Stop`.
- **on Stop:** speak a fixed **closing** (*"Thank you for using the HCI
  Multimodal Dialogue Service."*) and end the loop.

Microphone capture (VAD) and avatar rendering come from `UAs/Lib/UaKit`
(`AvatarUaHost`). Any visual acquisition uses **native Windows Media Capture**.

## 4. Files this app needs (build closure)
- **App:** `MPAIApps/HCIApps/MadApp/*`
- **AIF:** `AIF/V3.0/src/{Controller, Store, SharedStorage, GlobalStorage}`
- **UA library / MW:** `UAs/Lib/UaKit`, `MW/HciApi`
- **AIMs:** `AIMs/Core`; leaves `MMC/V2.5/ASR`, `MMC/V2.5/EDP`, `PAF/V1.6/PSD`,
  `MMC/V2.5/TTS`, `PAF/V1.6/GFD`; audio devices `CAE3/V1.0/AOA(.Windows)`,
  `MMC/V2.5/SOD(.Windows)`
- **L3s:** `1MMC-MAD-V2.5-I01.json` + `1MMC-ASR-V2.5-I01.json` +
  `1MMC-EDP-V2.5-I01.json` + the RSR-leaf AMDs
- **Orchestration:** `UAs/Orchestration/HCI-MAD.orch`
- **Schemas:** the JSON schemas reachable from MAD's data types (incl. MMC-SUM)
- **Settings:** `AIMs/aim-settings.json` - `MMC-ASR-V2.5`
  (`ExecutablePath` = whisper-cli, `ModelPath` = ggml-small.bin), `MMC-EDP-V2.5`
  (`OllamaModel` = llama3.2:3b), `MMC-TTS-V2.5` (Piper voice).
- **External runtimes (fetched/installed separately):** the **Whisper** binaries
  + model, and **Ollama** with the `llama3.2:3b` model running on
  `http://127.0.0.1:11434`.

## 5. Build & run
```
D:\BI\MPAIApps\HCIApps\MadApp\MadAppBuild.bat     # produces MadApp.exe
# start Ollama first (serve + model), then:
D:\BI\MPAIApps\HCIApps\MadApp\MadApp.exe
```
MAD requires **Ollama running** with the configured model; ASR requires the
Whisper CLI + model at the configured paths.

## Models & Prerequisites

The application code is in this package; the model files are **not** (they are large
and separately licensed). Obtain each model below, place it at the indicated relative
path under `Models\`, or set the corresponding key in `AIMs\aim-settings.json`.

> **Verification:** the SHA-256 values below identify the exact model files used.
> After downloading, verify each file with `Get-FileHash <file> -Algorithm SHA256`.
> For the InsightFace and SpeechBrain models the exact download URL/version was not
> recorded; the SHA-256 is the authoritative identity - confirm your copy matches.

| Model | Settings key (fallback) | File | Size | SHA-256 | Source |
|---|---|---|---|---|---|
| Whisper CLI (whisper.cpp Windows build) | `MMC-ASR-V2.5.ExecutablePath` | `whisper-cli.exe` | 0.457 MB | `800A0FD754AFA75E109C7248286AD735670FB6B23D92CA5D12604647EF638A65` | whisper.cpp (ggerganov) Windows release |
| Whisper model | `MMC-ASR-V2.5.ModelPath` | `ggml-small.bin` | 465.01 MB | `1BE3A9B2063867B937E64E2EC7483364A79917E157FA98C5D94B5C1FFFEA987B` | Hugging Face `ggerganov/whisper.cpp` (ggml-small.bin) |
| Local LLM (dialogue) | `MMC-EDP-V2.5.OllamaModel` | `llama3.2:3b` (Ollama tag) | ~2 GB | (Ollama library) | `ollama pull llama3.2:3b` |
| Piper TTS voice | `VoiceModel` / `Voice:en` | `en_US-amy-medium.onnx` | 60.27 MB | `B3A6E47B57B8C7FBE6A0CE2518161A50F59A9CDD8A50835C02CB02BDD6206C18` | Hugging Face `rhasspy/piper-voices` (en_US-amy-medium) |
| Piper voice config | `VoiceConfig` / `VoiceConfig:en` | `en_US-amy-medium.onnx.json` | 0.005 MB | `95A23EB4D42909D38DF73BB9AC7F45F597DBFCDE2D1BF9526FDEAF5466977D77` | Hugging Face `rhasspy/piper-voices` |

MAD additionally requires **Ollama running** (`ollama serve`) with the model pulled,
reachable at `http://127.0.0.1:11434`, and the Whisper CLI + model at the configured paths.
