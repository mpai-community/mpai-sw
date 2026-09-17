# AMQ - User Guidelines

**AMQ** (Audio-Visual Multimodal Question Answering) shows the machine a picture,
takes a question about it, and a Speaking Avatar answers aloud.

---

## What you need

- **Windows**, with the .NET 10 runtime.
- A **microphone**, if you want to ask aloud rather than type.
- Loudspeakers or headphones, to hear the answer.
- The **models** listed in the Developer guide, placed under `Models\`. They are
  not part of the download: they are large, and separately licensed.

---

## Running it

Build once with `MPAIApps\MmcApps\AmqApp\AmqAppBuild.bat`, then start

```
MPAIApps\MmcApps\AmqApp\src\bin\Release\net10.0-windows10.0.19041.0\AmqApp.exe
```

The window says **Wait while the avatar loads**. The first start takes half a
minute or so: the speech recogniser, the image model and the voice are all being
loaded from disk. When the avatar appears and greets you, it is ready.

1. **Load an image** - choose a picture from your computer.
2. **Ask** - type your question, or press the speak control and ask aloud.
3. The avatar answers.

---

## What it can and cannot answer

Ask about what the picture *shows*.

- "What is this?"
- "What colour is the car?"
- "How many people are there?"
- "Is it raining?"

It cannot tell you **who** somebody is. That is a different application: MAC
recognises a person against a gallery of people enrolled by ACR. AMQ describes
what it sees and nothing more.

Answers are short - a word or a phrase. The model behind AMQ answers visual
questions rather than holding a conversation; for conversation, see MAD.

---

## If something is wrong

| What you see | What it usually means |
|---|---|
| The avatar never appears | A model is missing or in the wrong place. Check the paths in the Developer guide. |
| The avatar moves its mouth but says nothing | The text-to-speech voice could not be loaded. |
| The mouth moves oddly, as if spelling | `espeak-ng` is not installed. The mouth follows the letters rather than the sounds. Harmless; AMQ works. |
| Nothing is heard from the microphone | Another application is using it. Close anything showing a camera or recording sound, then start AMQ again. |
| The answer is wrong or odd | The model answers from the image alone. Try a plainer question, or a clearer picture. |

AMQ writes a log beside its executable - `amq-crash.log` - which names what
failed. Read it with:

```
Get-Content amq-crash.log -Tail 20 -Encoding UTF8
```

---

## Licence

BSD 3-Clause. See `LICENSE`.
