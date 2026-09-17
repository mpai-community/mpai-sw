# Models — Provenance (best-estimate)

**Status of this document:** No download URLs were recorded in `aim-settings.json` or in the source code
(both were searched and returned nothing). The sources below are **best-estimate canonical origins**
inferred from each model's filename and identity — they are strong hints, **not confirmed provenance**.
**Verify each source (repo, mirror, version, licence) before citing or redistributing.** Model files are
NOT committed to the repositories; obtain them from the sources below and place them where the settings/guides expect.

Where a filename could plausibly come from more than one place, all candidates are listed.

Legend: ⚠ = filename spelling differs from upstream (see notes). ✔ = high confidence in the source. ~ = medium.

---

## 1. Face / speaker / body — identity & pose (used by the HCI-family apps)

| File | Size | Model | Best-estimate source(s) | Conf. |
|---|---|---|---|---|
| `scrfd_10g_bnkps.onnx` | 16 MB | SCRFD face detector | InsightFace model zoo — github.com/deepinsight/insightface (detection/scrfd); part of the `buffalo_l` pack. Mirrors on HuggingFace (search "scrfd_10g_bnkps"). | ✔ |
| `glintr100.onnx` | 249 MB | ArcFace R100 (Glint360K) face embedding | InsightFace — github.com/deepinsight/insightface; the `buffalo_l` recognition model (`glintr100`/`w600k_r50` family). HuggingFace mirrors of `buffalo_l`. | ✔ |
| `ecapa-tdnn.onnx` | 79 MB | ECAPA-TDNN speaker embedding | Source model: SpeechBrain `speechbrain/spkrec-ecapa-voxceleb` (HuggingFace) → ONNX conversion. Alternatives shipping ECAPA ONNX: WeSpeaker (github.com/wenet-e2e/wespeaker), NVIDIA NeMo, or community ONNX exports on HuggingFace. | ~ |
| `pose_landmarks_detector_full.onnx` | 12 MB | MediaPipe Pose landmarks | Google MediaPipe (github.com/google-ai-edge/mediapipe) — originally `.tflite`, ONNX via conversion. Community ONNX: PINTO0309/PINTO_model_zoo. | ~ |

## 2. Audio / speech affect & events

| File | Size | Model | Best-estimate source(s) | Conf. |
|---|---|---|---|---|
| `yamnet.onnx` (+ `yamnet_class_map.csv`) | 15 MB | YAMNet audio-event classifier | Google — TensorFlow Hub (tfhub.dev/google/yamnet/1) / github.com/tensorflow/models (research/audioset/yamnet). CSV = `yamnet_class_map.csv` from that repo. ONNX via tf2onnx, or community ONNX (HuggingFace / PINTO_model_zoo). | ✔ |
| `w2v2-emotion/model.onnx` | 631 MB | wav2vec2 dimensional speech emotion (arousal/valence/dominance) | audeering `audeering/wav2vec2-large-robust-12-ft-emotion-msp-dim` (HuggingFace) → ONNX conversion. | ✔ |
| `hsemotion_enet_b0_8_va_mtl.onnx` | 15 MB | HSEmotion face affect (EfficientNet-B0, 8-emotion + valence/arousal multitask) | HSE-asavchenko — github.com/HSE-asavchenko/face-emotion-recognition (weights `enet_b0_8_va_mtl`); `hsemotion` on PyPI; HuggingFace mirrors under the author. | ✔ |

## 3. Speech recognition (Whisper, whisper.cpp GGML)

| File | Size | Model | Best-estimate source(s) | Conf. |
|---|---|---|---|---|
| `ggml-small.bin` | 465 MB | Whisper small (multilingual) | whisper.cpp GGML — HuggingFace `ggerganov/whisper.cpp` (file `ggml-small.bin`); or via github.com/ggerganov/whisper.cpp `download-ggml-model.sh`. | ✔ |
| `ggml-base.bin` | 141 MB | Whisper base (multilingual) | whisper.cpp GGML — HuggingFace `ggerganov/whisper.cpp` (`ggml-base.bin`). | ✔ |
| `ggml-base.en.bin` | 141 MB | Whisper base (English-only) | whisper.cpp GGML — HuggingFace `ggerganov/whisper.cpp` (`ggml-base.en.bin`). | ✔ |

## 4. Translation (M2M100 ONNX) — stored under `Whisper\M2M100\` but it is the translation model

| File(s) | Size | Model | Best-estimate source(s) | Conf. |
|---|---|---|---|---|
| `encoder_model.onnx`, `decoder_model.onnx`, `decoder_model_merged_quantized.onnx`, `encoder_model_quantized.onnx`, `decoder_model_quantized.onnx`, `decoder_with_past_model.onnx`, `decoder_with_past_model_quantized.onnx` | 0.27–1.3 GB each | Meta M2M100 (418M) multilingual translation, ONNX | The `encoder/decoder/*_quantized/*_with_past` naming is the **HuggingFace Optimum / transformers.js** export convention. Strongest match: **`Xenova/m2m100_418M`** (transformers.js ONNX). Alternative: self-exported via `optimum-cli export onnx --model facebook/m2m100_418M`. Base model: `facebook/m2m100_418M`. | ✔ (naming), ~ (exact repo) |

## 5. Text-to-speech (Piper voices) — ALL from one place

**Source (all voices): rhasspy/piper — HuggingFace `rhasspy/piper-voices`** (github.com/rhasspy/piper).
Path pattern: `huggingface.co/rhasspy/piper-voices/tree/main/<lang>/<region>/<name>/<quality>/` → `<voice>.onnx` + `<voice>.onnx.json`.

| File | Size | Upstream voice id | Note |
|---|---|---|---|
| `de_DE-thorsten-medium.onnx` | 60 MB | de_DE/thorsten/medium | |
| `de_DE-eva_k-x_low.onnx` | 20 MB | de_DE/eva_k/x_low | |
| `en_GB-alan-medium.onnx` | 60 MB | en_GB/alan/medium | |
| `en_US-amy-medium.onnx` | 60 MB | en_US/amy/medium | |
| `en_US-lessiac-medium.onnx` | 60 MB | en_US/**lessac**/medium | ⚠ local spelling "lessiac" → upstream **lessac** |
| `es_ES-davefx-medium.onnx` | 60 MB | es_ES/davefx/medium | |
| `es_ES-mls_10246-low.onnx` | 60 MB | es_ES/mls_10246/low | |
| `fr_FR-siwis-medium.onnx` | 60 MB | fr_FR/siwis/medium | |
| `fr_FR-mls-medium.onnx` | 73 MB | fr_FR/mls/medium (or gilles) | verify exact name |
| `it_IT-paola-medium.onnx` | 61 MB | it_IT/paola/medium | |
| `it_IT-riccardo-x_low.onnx` | 27 MB | it_IT/riccardo/x_low | |
| `ja_JA-hi_fi_captain-medium.onnx` | 73 MB | **ja_JP**/hi_fi_captain/medium | ⚠ local "ja_JA" → upstream language code **ja_JP** |
| `pt_BR-cadu-medium.onnx` | 60 MB | pt_BR/cadu/medium | |
| `zh_CN-huayan-medium.onnx` | 60 MB | zh_CN/huayan/medium | |

Each voice also needs its companion `.onnx.json` config from the same folder.

## 6. LLM for dialogue (EDP) — not a file above; served by Ollama

| Model | Source | Command |
|---|---|---|
| `llama3.2:3b` | Ollama registry — ollama.com/library/llama3.2 (underlying: Meta Llama 3.2 3B) | `ollama pull llama3.2:3b` |

## 7. Vision-language / OCR — present in D:\AI, NOT used by the six HCI-family apps

| File(s) | Model | Best-estimate source(s) | Conf. |
|---|---|---|---|
| `blip-vqa-base/pytorch_model.bin` (1.47 GB) | Salesforce BLIP VQA base | HuggingFace `Salesforce/blip-vqa-base`. | ✔ |
| `blip_decoder_context*.onnx`, `blip_text_encoder_wrapper.onnx`, `blip_vision_model.onnx`, `blip_vqa.onnx`, `vision_onnx.bin`, `context_onnx.bin`, `pixel_values_csharp.bin` | BLIP components exported to ONNX | Self/community ONNX exports of `Salesforce/blip-vqa-base`. | ~ |
| `ch_PP-OCRv5_server_det.onnx`, `ch_PP-OCRv5_mobile_det.onnx`, `ch_ppocr_mobile_v2.0_cls_infer.onnx`, `latin_PP-OCRv5_rec_mobile_infer.onnx` | PaddleOCR PP-OCRv5 / PP-OCR mobile (detect / classify / recognise) | PaddleOCR — github.com/PaddlePaddle/PaddleOCR (the `*_infer` Paddle inference models; ONNX via paddle2onnx). Some mirrored on HuggingFace `PaddlePaddle/*`. | ~ |

---

## Notes to act on
1. **Piper spelling fixes** if you script re-downloads: `lessiac`→`lessac`; `ja_JA`→`ja_JP`. Verify `fr_FR-mls-medium` exact voice name against the repo.
2. **M2M100** files live under a `Whisper\` folder but are the **translation** model — the `encoder/decoder/*_quantized/*_with_past` naming most closely matches **`Xenova/m2m100_418M`** (transformers.js). Confirm before citing.
3. Several ONNX files are **conversions** of upstream PyTorch/TF models (ECAPA, YAMNet, w2v2-emotion, BLIP, M2M100). The "source" is the original model; the ONNX may be a community export or a self-export — record which you actually used for reproducibility.
4. **Provenance is unverified.** This table is derived from filenames only; no URLs were found in the codebase. Before publishing licence/attribution notes, confirm each origin and check its licence (InsightFace non-commercial terms, Piper voice licences, Llama licence, PaddleOCR/Apache, etc.).
