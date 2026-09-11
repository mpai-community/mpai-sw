# MAD - User Guidelines

**MAD** is the **Multimodal Anonymous Dialogue** application. You hold a spoken
conversation with a Speaking Avatar. You are **anonymous** - MAD does not try to
identify you - and the avatar keeps a neutral manner.

## What it is for
MAD answers *"let me have a conversation."* You speak; the app transcribes your
speech, a local language model composes a reply, and the avatar speaks it back,
remembering the conversation so far. Useful as a hands-free conversational
assistant demonstration on the MPAI-AIF platform.

## Before you start
- A **microphone** must be connected and available.
- **Ollama** must be running with the configured model (start it with the
  provided `start-ollama.bat`). Without Ollama, the avatar cannot reply.

## How to use it
1. Launch **MadApp**. The Speaking Avatar appears.
2. Press **Start**. The avatar says *"Welcome to the HCI Multimodal Dialogue
   Service."*
3. When it is quiet, **speak**. Pause when you finish - MAD detects the end of
   your turn automatically.
4. The avatar **replies**, and remembers what was said as the conversation goes
   on.
5. Continue speaking and listening for as many turns as you like.
6. Press **Stop** to end. The avatar says *"Thank you for using the HCI
   Multimodal Dialogue Service."*

## Notes & troubleshooting
- **The avatar does not reply:** check that **Ollama is running** (the
  `start-ollama.bat` window should be open) and that the model is loaded.
- **A reply takes a few seconds:** the local model runs on your machine; some
  delay per turn is normal.
- **It does not hear you:** speak clearly and pause at the end; make sure the
  microphone is the default input and not muted.
