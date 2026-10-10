# Third-party notices

The software in this repository is under the BSD 3-Clause License of `LICENSE`. The
following files, made by others, are included under their own licences.

| Files | What | Licence |
|---|---|---|
| `UserAgent/Assets/cav-avatar.glb` | Thalia's avatar, made with Ready Player Me | Creative Commons Attribution-NonCommercial-ShareAlike 4.0 (CC BY-NC-SA 4.0), https://creativecommons.org/licenses/by-nc-sa/4.0/ |
| `Models/BLIP/onnx/*` | The ONNX export of Salesforce's BLIP VQA base (https://huggingface.co/Salesforce/blip-vqa-base), which AMQ uses. Converted by MPAI; the model is unchanged. | BSD 3-Clause License, Copyright (c) 2022, Salesforce.com, Inc. All rights reserved. The licence text is in https://github.com/salesforce/BLIP/blob/main/LICENSE.txt and is kept in `docs/licences/BLIP-LICENSE.txt`. |

**Thalia's avatar is used for demonstration.** The face is a Ready Player Me avatar. CC BY-NC-SA 4.0 allows
non-commercial use, with attribution to Ready Player Me, and adaptations under the same
licence; other uses are restricted. The avatar page says so on the screen whenever the face is
shown ("Avatar by Ready Player Me (CC BY-NC-SA 4.0), used for demonstration. Other uses are
subject to restrictions."). Whoever uses this software otherwise must replace `cav-avatar.glb` by an
avatar they may use: any glTF avatar with the 52 ARKit blendshapes works with the avatar page - for
example a Microsoft Rocketbox avatar (MIT), whose ARKit blendshapes are named `AK_nn_<Name>` and need
only that mapping - and change or remove the notice in `UserAgent/Assets/cav-webview.html`.

`UserAgent/Assets/studio.hdr`, the studio lighting of the avatar page, is an image
whose origin is not recorded; it can be replaced by any equirectangular HDR, such as
the CC0 studio HDRIs of Poly Haven.

**Obtained separately, not in the repository.** The AI models, programs and voices that Thalia
uses, each under its own licence, are listed with where to download them in
`docs/Thalia-Models.md`. Several are for non-commercial use only.
