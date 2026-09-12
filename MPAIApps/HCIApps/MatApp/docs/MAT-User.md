# MAT - Multimodal Anonymous Translation - User Guide

MAT translates what you say (or type) from one language into another and has a
3-D **Speaking Avatar** speak the translation. You choose the input and output
languages; you can translate **typed text** (you read the translation) or
**spoken speech** (the avatar speaks it).

---

## What you need

- A **Windows PC** with the **.NET 10** runtime.
- A **microphone** (for the spoken path) and speakers.
- The translation **models** in place (installed once - see the Developer guide):
  a **multilingual** Whisper model (speech-to-text), the M2M100 translation model,
  and Piper voices for the output languages.

## Running MAT

1. Start **MatApp.exe** (or run `MatAppBuild.bat` once to produce it).
2. Press **Start**. The avatar welcomes you and loads the models (a few seconds).
3. Press **Start** again. The avatar reads the instructions; the button becomes
   **Select**.

## Using MAT

1. Press **Select** and choose the **input** language (what you will speak/type)
   and the **output** language (what you want back).
2. Then either:
   - **Type text** in the box and press **Enter** -> the translation appears in the
     panel (you read it); or
   - Press **Speak**, say a sentence, and pause -> the avatar **speaks** the
     translation and the text is also shown. Press **Stop** when you have heard it.
3. Repeat for another translation. Press **Select** again to change languages.

## Tips

- Speak clearly and pause at the end so MAT knows you have finished.
- Make sure the **input language you pick matches the language you actually
  speak** - MAT decodes your speech as the input language you selected.
- For the spoken path, the output language needs an installed voice; if you hear
  nothing, that voice may be missing (see the Developer guide).

## If something goes wrong

| You see / hear | Likely cause | What to do |
| --- | --- | --- |
| The translation reads "(speaking in foreign language)" or similar | You spoke a language other than the **input** language you selected, or the speech model is English-only | Pick the input language you are actually speaking; ensure a **multilingual** speech model is installed. |
| Text appears but the avatar is silent | The output language's voice is missing | Install the Piper voice for that language. |
| "Startup failed..." | The app cannot find its files | Run it from inside the installed folder; do not move `MatApp.exe` out on its own. |

## Privacy

MAT is **anonymous** - there is no identity and no recording kept. Speech is
processed for the translation of the moment only.