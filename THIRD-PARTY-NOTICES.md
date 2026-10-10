# Third-party notices

The software in this repository is under the BSD 3-Clause License of `LICENSE`. The
following files, made by others, are included under their own licences.

| Files | What | Licence |
|---|---|---|
| `MMM/Viewer/personae/Persona1.glb`, `Persona2.glb` | The Personae of the MMM viewer: Microsoft Rocketbox avatars Female_Adult_01 and Male_Adult_01, converted to glTF (`MMM/Viewer/personae/README.md`) | MIT, Copyright (c) 2020 Microsoft - `MMM/Viewer/personae/LICENSE-Rocketbox.md` |
| `UserAgent/Assets/cav-avatar.glb` | Thalia's avatar, made with Ready Player Me | Creative Commons Attribution-NonCommercial-ShareAlike 4.0 (CC BY-NC-SA 4.0), https://creativecommons.org/licenses/by-nc-sa/4.0/ |

**Thalia's avatar is used for demonstration.** CC BY-NC-SA 4.0 allows non-commercial use,
with attribution to Ready Player Me, and adaptations under the same licence; other uses
are restricted. The avatar page says so under the avatar. Whoever uses this software
otherwise must replace `cav-avatar.glb` by an avatar they may use: any glTF avatar with
the 52 ARKit blendshapes works with the avatar page - for example a Microsoft Rocketbox
avatar (MIT), whose ARKit blendshapes are named `AK_nn_<Name>` and need only that
mapping.

`UserAgent/Assets/studio.hdr`, the studio lighting of the avatar page, is an image
whose origin is not recorded; it can be replaced by any equirectangular HDR, such as
the CC0 studio HDRIs of Poly Haven.

**Obtained separately, not in the repository.** The spatial renderer of the User Agent
(`UserAgent/SpatialAudio`) calls Steam Audio 4.8.1 by Valve Corporation, under the
Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0), whose native library
is put in `Models/SteamAudio` (`docs/models-provenance.md`). OCR uses RapidOcrNet
(Apache-2.0, a NuGet package) with PaddleOCR's PP-OCRv5 models (Apache-2.0).
