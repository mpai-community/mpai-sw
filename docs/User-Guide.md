# MPAI Software - User Guide

This guide is for a person who wants to **use** what this repository offers. If you want to build, change or extend it, read the
[Developer Guide](Developer-Guide.md).

## 1. What this is

The repository holds the reference software of MPAI as one platform made of three kinds of part:

- the **AI Framework** (AIF) that runs AI Modules and, for services on a network, the communication and storage around it (MPAI-MAS);
- the **AI Modules** (AIMs): the tools, the same for every application;
- the **User Agents**: the applications a person uses, which can be combined.

## 2. What you can use today

| Application | What it is | Status | Guide |
|---|---|---|---|
| **Thalia** | The avatar of MPAI as a Service: a conversation, a question about a picture, a translation, an affective dialogue, and access by face and voice, in your web browser. | Available | [Thalia - User Guide](Thalia-User-Guide.md) |
| **Athena** | One interface through which a person drives the simulations of a Connected Autonomous Vehicle (CAV). No MAS: it runs on your own computer. | Preliminary name. The CAV's AI Modules are in this repository; the application is **not yet released** | - |

Thalia needs a computer on which you install her, the AI models (downloaded separately: [Thalia - Models](Thalia-Models.md)) and a
web browser with a microphone and a camera. The Thalia guide takes you from there to a first conversation.

## 3. What to expect of Athena

Athena will let you choose a driving simulation and watch the CAV in it. The first one planned is **highway overtaking**: the CAV
follows a slower vehicle, waits for faster ones coming up behind, pulls out, passes and returns. That is not offered here yet,
because the overtaking decision is still a prototype outside the CAV's own Modules. When it is part of the CAV it will be added,
with its own guide. Until then there is nothing for you to install or run for Athena.

## 4. Your data

Nothing a person says or shows is stored by the software unless whoever runs it switches diagnostics on. Thalia's privacy section
([User Guide, section 6](Thalia-User-Guide.md)) says exactly what applies.

## 5. Where to ask

MPAI Secretariat: secretariat@mpai.community - <https://mpai.community>.
