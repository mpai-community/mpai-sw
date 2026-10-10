# docs/conversion

Scripts that turn the published form of a model into the ONNX file Thalia uses. Two models are not published as ONNX
by their authors; see [Thalia - Models](../Thalia-Models.md) for all the others.

| Script | Model | Status |
|---|---|---|
| `export_ecapa_tdnn.py` | 3D-Speaker ECAPA-TDNN (Apache-2.0) | Run and checked: its result is close to the file Thalia was tested with, not identical. |
| `export_blip_vqa_to_onnx.py` | Salesforce BLIP VQA base (BSD-3-Clause) | The recipe that made the files in `Models/BLIP/onnx`. Not re-run with the latest libraries. |

The wav2vec2 emotion model needs no conversion: audeering publishes the ONNX file Thalia uses (<https://zenodo.org/records/6221127>).
