# MPAI Software

Reference software for the standards of MPAI - Moving Picture, Audio and Data
Coding by Artificial Intelligence (<https://mpai.community>). Contact:
secretariat@mpai.community. Licence: BSD-3-Clause (see `LICENSE`).

This repository holds one application, **MAS-App**: a Service that offers four
AI applications over the network, and two clients - one for the desktop, one
for the browser - through which a person uses them. Every application presents
a 3-D Speaking Avatar that listens, looks, answers and shows expression.

| App | Name | What it does |
|---|---|---|
| **MAD** | Multimodal Conversation | A spoken or typed conversation with the avatar. |
| **AMQ** | Multimodal Question Answering | Show a picture, ask about it, and the avatar answers. |
| **MAT** | Multimodal Translation | Speak or type in one language; the avatar says it in another. |
| **MPD** | Multimodal Affective Dialogue | A conversation in which the avatar reads your words, voice and face and answers with feeling. |

- **To install and use it:** [MAS-App User Guide](MAS-App-User.md)
- **To build on it:** [MAS-App Developer Guide](MAS-App-Developer.md)
- **The AI models it needs:** [MAS-App - Models](MAS-App-Models.md)

## The standards it implements

- **MPAI-AIF** - AI Framework: <https://mpai.community/standards/mpai-aif/>
- **MPAI-MAS** - MPAI as a Service: <https://mpai.community/standards/mpai-mas/>
- **MPAI-MMC** - Multimodal Conversation: <https://mpai.community/standards/mpai-mmc/>
- **MPAI-PAF** - Portable Avatar Format: <https://mpai.community/standards/mpai-paf/>
- **MPAI-OSD** - Object and Scene Description: <https://mpai.community/standards/mpai-osd/>

## The ideas in one page

**AIM (AI Module).** A unit of processing with typed Ports. A Port is addressed
by its **Data Type** and **Port Number**, never by its name.

**Module.** A composite AIM: a graph of AIMs, described by an **L3** file (JSON,
in `AIMs/AMDs/`) listing its Sub-AIMs, its boundary Ports and its Topology. Each
App runs over one Module: MAD over `1MMC-MAD-V2.5-I01`, and so on.

**Controller.** Builds a Module's graph from its L3 and runs it. It exposes only
the Module's boundary.

**User Agent.** Everything on the person's side: it captures speech, text,
pictures and the face, renders the avatar, and runs the App's **workflow** - a
short text in the Workflow Description Language (WDL) saying what to acquire,
what to give the Module, what to ask back and what to present.

**MPAI-MAS.** The same arrangement with the User Agent across a network: the
**Service** holds the Controller, the Modules and the models; the **client**
(the Remote Client Application) holds the microphone, the camera, the screen
and the avatar, and no model at all. They speak MPAI-MAS over HTTPS.

An App, as a client sees it, is therefore just its workflow, a name and an
icon. The work is done by the Module on the Service.

## What is in the repository

| Folder | What it holds |
|---|---|
| `AIF/` | The Controller, the Controller API (`ControllerApi`, formerly called the North API), the wire form of each Data Type (`PortData`), the Store of L3s, and Communication (Shared and Global Storage). |
| `AIMs/` | The AIMs, their L3s (`AIMs/AMDs/`), the shared data types (`AIMs/Core/`), and the providers that build each Module's AIMs (`AIMs/Providers/`). |
| `MAS/` | MPAI-MAS: the server of its API (`Server`); the **Service** (`Service`, the program `MasService`, formerly `AmqServer`), which offers all four Apps; the Store of L3s (`Store/Service`) and its App (`Store/App`). |
| `UserAgent/` | The User Agent: the WDL reader (`Wdl`), the workflow interpreter (`Rca`), the Controller API across MPAI-MAS (`Remote`), the Physical Layer's Units - microphone, camera, loudspeaker, avatar (`PhysicalLayer`), the desktop avatar library (`UaKit`), the avatar (`Assets`), and the client's own workflow, MPAI-MAS (`Orchestration`). |
| `UserAgent/Clients/Desktop/` | The **desktop client** (Windows). |
| `UserAgent/Clients/Browser/` | The **browser client**: `Client` (runs in the browser) and `Host` (serves it). |
| `Apps/` | One folder per App: its workflow, `app.json` and icon. |
| `schemas/` | The JSON Schemas of the MPAI Data Types. |
| `docs/` | This guide, the User and Developer guides, model provenance. |

## What is not in the repository

**AI models** are not distributed here. They are obtained separately, by name,
size, SHA-256 and source, and placed under `Models/` - see
[MAS-App - Models](MAS-App-Models.md). Nor are there any credentials,
certificates, server configurations or personal data.

## Privacy, by design

- **A conversation's memory belongs to the conversation.** The workflow carries
  it from turn to turn; the Service keeps nothing between turns, and two people
  using it at once never share a memory.
- **Nothing a person says or shows is written to disk** unless diagnostics are
  switched on by the person running the software (`MPAI_DIAG=1`), and then only
  under the system's temporary folder.
