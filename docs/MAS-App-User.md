# MAS-App - User Guide

MAS-App is a **Service** offering four AI applications - MAD, AMQ, MAT and MPD -
and two **clients** to use them with: one for the Windows desktop, one for a web
browser. This guide explains how to install it, start it and use it.

For what the software is, see [MPAI Software](MPAI-Software.md). For how it is
built, see the [Developer Guide](MAS-App-Developer.md).

---

## 1. What you need

- **Windows 10 or 11**, 64-bit, with **16 GB of memory** or more. A recent GPU
  helps but is not required.
- **.NET 10 SDK** - <https://dotnet.microsoft.com/download>.
- **Ollama**, which runs the language model used by MAD and MPD -
  <https://ollama.com>. After installing it, run once:
  ```
  ollama pull llama3.2:3b
  ```
- **A microphone and speakers.** For MPD, also **a webcam**.
- **For the browser client:** Microsoft Edge or Google Chrome.
- **The AI models**, placed under `Models\` in the folder where you put the
  software (section 2).

## 2. The models

The models are not in the repository. Obtain each from its source - listed, with
its size and SHA-256, in [MAS-App - Models](MAS-App-Models.md) - and place it under
`Models\` at the path the settings file (`AIMs\aim-settings.json`) names:

| Used for | Files under `Models\` | Needed by |
|---|---|---|
| Speech recognition (Whisper) | `Whisper\bin\whisper-cli.exe`, `Whisper\models\ggml-small.bin` | all four Apps |
| Speech synthesis (Piper) | `Piper\piper_windows_amd64\piper\piper.exe`, and the voices under `Piper\voices\` (one `.onnx` and its `.onnx.json` per language) | all four Apps |
| Picture question answering (BLIP) | `BLIP\onnx\blip_vision_model.onnx` with `blip_vision_model.onnx.data`, `blip_text_encoder_wrapper.onnx`, `blip_decoder_context_dynamic.onnx`, `BLIP\blip-vqa-base\vocab.txt` | AMQ |
| Translation (M2M100) | the files under `Whisper\M2M100\` named in `AIMs\aim-settings.json` | MAT |
| Emotion in the voice (wav2vec2) | `w2v2-emotion\model.onnx` | MPD |
| Emotion in the face (HSEmotion) | `hsemotion_enet_b0_8_va_mtl.onnx` | MPD |

If a model is missing or misplaced, the Service reports an error while it loads the models; check the path against `AIMs\aim-settings.json`.

## 3. Once, before the first start

**Trust the development certificate**, so that the clients can reach the
Service over HTTPS on this machine:
```
dotnet dev-certs https --trust
```

**Write the Service's configuration.** Create a file, for example
`MPAIApps\MmcApps\AmqServer\mas-server-MAS.json`, replacing `D:\\MPAI` with the
folder where you put the software:
```json
{
  "ListenUrl":    "https://localhost:5005/",
  "AppDirectory": "D:\\MPAI\\Apps",
  "Apps":         [ "MAD", "AMQ", "MAT", "MPD" ],
  "AmdDirectory": "D:\\MPAI\\AIMs\\AMDs",
  "SettingsPath": "D:\\MPAI\\AIMs\\aim-settings.json"
}
```

## 4. Starting it

Use one PowerShell window for each program. In the commands below, replace
`D:\MPAI` with your folder.

**Window 1 - the Service.** Start it first, and wait: loading the models takes a
minute or two.
```
dotnet run --project D:\MPAI\MPAIApps\MmcApps\AmqServer\src\AmqServer.csproj -- D:\MPAI\MPAIApps\MmcApps\AmqServer\mas-server-MAS.json
```
It is ready when it prints `[MAS] Listening on https://localhost:5005/`. Above
that it lists the Modules it loaded, the four Apps, and the Data Types it
carries.

Then start either client, or both.

**Window 2 - the browser client.**
```
dotnet run --project D:\MPAI\MPAIApps\RcaWeb\Host\RcaWeb.Host.csproj -- --Service https://localhost:5005/ --Urls https://localhost:5010
```
Open `https://localhost:5010` in Edge or Chrome and press **Start**. The browser
asks to use the microphone - allow it.

**Window 3 - the desktop client.**
```
dotnet run --project D:\MPAI\MPAIApps\RcaApp\src\RcaApp.csproj
```
It opens its own window with the avatar.

To stop a program, press **Ctrl+C** in its window.

## 5. Using it

The avatar welcomes you and shows the Apps. Choose one. **Stop** ends the App
you are in and brings the list back; with no App running, it ends the session.

**Speaking or typing.** Whenever the avatar waits for you, you may either speak,
or type in the text box and press **Enter**. As soon as you type the first
character, she stops listening, so take your time. Typed text must be in the
language's own script (for Japanese, kana and kanji - not romaji).

**MAD - conversation.** Talk about anything. She remembers what was said in this
conversation, and forgets it when you stop the App.

**AMQ - questions about a picture.** She asks whether you want to ask about a
picture; answer yes or no. Then press **Choose a picture...** (in the browser)
or pick a file (on the desktop), and ask your question. She answers aloud and
shows the answer. Answer no to finish.

**MAT - translation.** Choose the input and the output language, then speak or
type a sentence. She shows the translation and says it in the other language.

**MPD - affective dialogue.** Allow the camera. She reads what you say, how you
say it and your face, and answers with an expression of her own. Tell her
something happy, and she smiles.

## 6. Privacy

- What you say, type and show is processed and not stored. A conversation's
  memory is kept by your client for that conversation only; the Service keeps
  nothing between turns, and two people using it at once never share a memory.
- Nothing is written to disk unless whoever runs the software switches
  diagnostics on with the environment variable `MPAI_DIAG=1`; diagnostics are
  then written under the system's temporary folder, in `mpai-diag`.
- The Service window shows, as it happens, what the speech recognition heard.

## 7. When something goes wrong

| What you see | What to do |
|---|---|
| A build fails with "The file is locked by ... (number)" | The program is still running from before. Press Ctrl+C in its window, or run `Stop-Process -Id <number>`, and start it again. |
| The browser says "Something went wrong", or a client cannot reach the Service | The Service is not running, or still loading. Wait for `[MAS] Listening` in window 1, then reload the page (Ctrl+F5). |
| The browser page stays at "Loading..." | Reload with Ctrl+F5. If it persists, open the browser's console (F12, Console) and note the red lines. |
| She answers something unrelated to what you said | The microphone picked up noise, or the start of your sentence. Speak after she has finished, or type instead. |
| No sound in the browser | Press Start first: a browser plays sound only after a click. |

## 8. Known limitations

- **Small models.** The language model (`llama3.2:3b`) and the picture model are
  small. Answers can be vague, and in MPD she may avoid personal questions such
  as "What is my name?". A larger Ollama model can be set in `AIMs\aim-settings.json`
  (`OllamaModel`).
- **One Service, one machine.** The Service is meant to run on the machine it
  serves. It can be reached from elsewhere (for example through a tunnel), but
  it then has no access control of its own for the browser client: anyone who
  knows its address can use it.
