# MPAI Software - Developer Guide

This guide is for a person who wants to **build, change or extend** the software. To use it, read the [User Guide](User-Guide.md).
What is specific to one application is in its own guide: for Thalia, the [Technical Guide](Thalia-Technical-Guide.md)
and [Models](Thalia-Models.md).

## 1. The idea: one platform, not one silo per application

An MPAI application is made of three things, and only the last is the application's own:

1. **The infrastructure.** The AI Framework (`AIF/`): the Controller that builds a Module's graph from its L3 and runs it, the Store
   of L3s, the checks of Metadata and PortData, Shared Storage, Trust. For a service on a network, MPAI-MAS adds communication and
   storage (`MAS/`).
2. **The AIMs** (`AIMs/`): the tools. They are **common to all applications**: an AIM written once is used by every application whose
   workflow reaches it.
3. **The User Agents** (`UserAgent/`, `Apps/`): the applications a person uses. A User Agent runs a **workflow**, a text in the
   Workflow Description Language (WDL), over a Module. User Agents may be combined: a workflow can run another App.

Adding an application therefore means adding a User Agent and, only when a tool is missing, an AIM. It never means a copy of the
platform.

## 2. Folders

| Folder | Role |
|---|---|
| `AIF/` | The AI Framework. |
| `AIMs/` | AIMs by standard and version (`MMC`, `OSD`, `PAF`, `HCI`, `CAV`, `CAE3`, `CVE`), shared Data Types (`Core`), the ONNX helper (`Onnx`), the providers of Thalia's Modules (`Providers`), the L3s (`AMDs`) and the settings (`aim-settings.json`). |
| `MAS/` | The MPAI-MAS Service and its install and publish scripts. |
| `UserAgent/` | The person's side: the browser client, the workflow interpreter, the Controller API across the network, the client's own workflow and the avatar. |
| `Apps/` | One folder per App: workflow, `app.json`, icon. |
| `schemas/` | JSON Schemas of the Data Types and of the AIMs' L2 metadata. |
| `Models/`, `Access/`, `SharedStorage/` | Where models, static data and governed storage go on your machine; **not distributed**. |

The folder can be put anywhere: the software finds its root by going up to the folder that holds `AIMs` and `UserAgent`. Keep it so:
no path of a drive or folder in code or in a setting; resolve relative to that root (`MpaiPaths`).

## 3. Which AIMs serve which application

There are 56 L3s in `AIMs/AMDs/`. Of those, 17 are used by both Thalia and the CAV, 11 only by Thalia, 28 only by the CAV.

| Used by | L3s |
|---|---|
| Both | `MMC`: ASR, EDP, NLU, PDX, PMX, PSE, SIR, SPE, TTS - `OSD`: IDR - `PAF`: FIR, FPE, GBD, GFD, RSR, SAF, SAS |
| Thalia only | `MAS-APP`; `MMC`: ACR, AMQ, ESD, MAC, MAD, MAT, MPD, TIQ, TTT - `PAF`: EFD |
| CAV only | `CAV` (18, Stage 1): CAO (the CAV as one Module), HCI, ESS, BED, SAG, FED, AMS, TPD, PSP, RSP, MSP, TOA, AMI, AMM, MAS, MSA, MRA, ICA - `CAE`: AII, ASI, QCV - `CVE`: VII, VSI - `OSD`: BAS, BLS, BVS (two instances) |

Do not duplicate a shared AIM for a new application. If you need it to behave differently, add a setting or an L3 that composes it.

## 4. Build

Each project builds on its own:

```bash
dotnet build AIMs/CAV/V2.0/Ams/Mpai.Cav.Ams.csproj
dotnet build MAS/Service/src/MasService.csproj
```

All the CAV projects (`AIMs/CAV`, `AIMs/CAE3`, `AIMs/CVE`, `AIMs/OSD`) build here without anything outside this tree. The models
are obtained separately; the AIMs read their locations and thresholds from `AIMs/aim-settings.json`.

## 5. Adding things

- **An AIM.** A project under `AIMs/<standard>/<version>/<ACRONYM>/`, its L3 in `AIMs/AMDs/` (`1<STD>-<AIM>-V<v>-I<nn>.json`), its
  settings in `aim-settings.json`, and the schemas it refers to in `schemas/`. Ports are addressed by Data Type and Port Number, never
  by name.
- **A Module.** A composite L3 listing Sub-AIMs, boundary Ports and Topology, each end written as AIM + Data Type + Port Number.
- **An App.** A folder in `Apps/` with its workflow, `app.json` and icon (Thalia Technical Guide, section 7).
- **A User Agent.** A client that gets the person's input, runs a workflow and presents the result.

## 6. State of the CAV side

The CAV's AIMs are here in their Stage 1 form: environment sensing from one visual technology, path and motion planning that
follows the vehicle ahead in its lane, the actuation messages and their analysis, human-CAV interaction, and the Connected
Autonomous Operation Module that holds them together. The Autonomous Motion Subsystem of Stage 1 **never changes lane**: an
overtaking is not yet one of its decisions. There is no simulation and no Athena application in this repository yet. They will
be added as a User Agent over these AIMs, without a copy of anything above.
