# MPAI Store Service

Actor A of MPAI-MAS V1.0, *Architecture and Operation*: a repository of approved
L3s, with a REST API over HTTPS. Implementers submit L3s; the Controllers of MAS
Services obtain them (actions 8 and 9).

## Running it

```
dotnet run --project MPAIApps\StoreService\StoreService.csproj -- --Urls https://localhost:5020 --Root D:\MPAI\Store --Packages D:\MPAI\Packages
```

| Option | Meaning | Default |
|---|---|---|
| `--Urls` | where it listens | `https://localhost:5020` |
| `--Root` | where it keeps its L3s | `<local application data>\MPAI\Store` |
| `--Packages` | the folder holding all packages; `file:` URIs are inspected only inside it | none: `file:` packages are not inspected |
| `--Schemas` | the published schemas, for the L2s | the `schemas` folder above the program |

## What happens to a submitted L3

- **Validated against its L2**, the standard-level instance of the AIM it implements
  (`1MMC-AMQ-V2.5-I01` against `MMC-AMQ-V2.5`, found in `schemas/*/V*/AIMs`):
  Ports and Sub-AIMs that differ are **signalled**, not refused.
- **Its package looked for** at the `ImplementationURI` of each `Implementations`
  entry - present, with its `BinaryName`.dll: **signalled**, not refused. Whether the
  package is legitimate is checked later, with fingerprints.
- **Refused** if a Sub-AIM's L3 is in neither the composite's package (which carries
  the L3 of each AIM it bundles) nor the Store - or if the L3 has no `Identifier.AIMName`.

A published L3 is never overwritten: submitting it again publishes version n+1.

## The API

| Route | |
|---|---|
| `POST /MPAI/Store/L3` | submit an L3: `201` published, with `findings`; `422` refused, with `missing` |
| `GET /MPAI/Store/L3[?name=...]` | approved L3s, latest versions; `name` may be the standard name (`MMC-TIQ-V2.5`) |
| `GET /MPAI/Store/L3/{id}[?version=n]` | one L3; header `MPAI-Store-Version` |
| `GET /MPAI/Store/L3/{id}/versions` | its versions |
| `GET /MPAI/Store/L3/{id}/findings[?version=n]` | what the Store found in it |

## Submitting many

`Submit-L3s.ps1` submits a set of L3s in an order that lets each composite find its
Sub-AIMs, naming in each submitted copy where its package is:

```
powershell -ExecutionPolicy Bypass -File MPAIApps\StoreService\Submit-L3s.ps1 -Folder D:\BI\AIMs\AMDs -Ids 1MMC-TIQ-V2.5-I01,1MMC-ASR-V2.5-I01 -Packages D:\MPAI\Packages
```
