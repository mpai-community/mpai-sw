# MPD - Multimodal Personal Status-based Dialogue - User Guide

MPD lets you hold a spoken conversation with a 3-D **Speaking Avatar** that not
only understands **what** you say but senses **how you feel** - from your voice
and your face - and replies accordingly. Its reply is spoken with matching
expression: the avatar's face and voice carry the emotion, not just the words.

---

## What you need

- A **Windows PC** with the **.NET 10** runtime.
- A **microphone**, **speakers**, and a **webcam** (the webcam lets MPD read
  facial expression; without it, MPD still works from voice and words).
- The models in place (installed once - see the Developer guide): a multilingual
  Whisper model (speech-to-text), a local LLM served by **Ollama** (the dialogue),
  the speech-emotion and face-emotion models, and a Piper voice.

## Running MPD

1. Start **MpdApp.exe** (or run `MpdAppBuild.bat` once to produce it).
2. Press **Start**. The avatar welcomes you and loads the models (a few seconds).
   The button greys while loading.
3. When loading finishes the avatar says the service is available, and MPD begins
   listening automatically - there is nothing to press.

## Using MPD

- Just **speak**, then pause. The avatar replies, expressing how it chooses to
  present itself (its face and voice reflect its response).
- When you have spoken, a **Stop** button becomes available; press it to end the
  session.
- Speak clearly and let the webcam see your face for the fullest reading of how
  you feel.

## Privacy

MPD is **anonymous** - there is no identity and nothing is recorded. Your speech
and image are processed only to understand and reply to the moment.

## If something goes wrong

| You see / hear | Likely cause | What to do |
| --- | --- | --- |
| The avatar does not reply | The local LLM (Ollama) is not running | Start Ollama and its model, then try again. |
| The voice is flat | The reply's emotion was neutral, or the emotion models are missing | Neutral replies are spoken plainly; check the emotion models are installed. |
| "startup failed..." | The app cannot find its files | Run it from inside the installed folder; do not move `MpdApp.exe` out on its own. |
