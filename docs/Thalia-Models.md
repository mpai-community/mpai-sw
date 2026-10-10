# Thalia - Models

Thalia needs a set of AI models and programs that are **not distributed with the software** (the exception is the BLIP picture
model, which is in `Models/BLIP/onnx/` of this repository). Obtain each from the address given here, place it under
`Models/` at the path named in `AIMs/aim-settings.json`, and check it with

```
Get-FileHash <file> -Algorithm SHA256
```

A different hash means a different file. A different version or export of the same model may work, but has not been tested with Thalia.

**How each source was checked.** Where an address is marked **verified**, the SHA-256 published by (or computed from) that address is
identical to the one in the table, so the file is the same. Where it says **source model only**, the address is the origin of the model
but the ONNX file Thalia uses is a conversion that nobody has published: you will have to convert it yourself, or ask MPAI.
Licences differ and some forbid commercial use: **read each licence before you use or pass on a file.**

## Summary

| Model | Used for | Apps | Where to download | Status |
|---|---|---|---|---|
| Whisper small (whisper.cpp) | Speech recognition | MAD, AMQ, MAT, MPD | <https://huggingface.co/ggerganov/whisper.cpp> (`ggml-small.bin`) and <https://github.com/ggml-org/whisper.cpp/releases> (program) | model **verified**; program version not identified |
| Piper and 8 voices | Speech synthesis | all | <https://github.com/rhasspy/piper/releases/tag/2023.11.14-2> (program), <https://huggingface.co/rhasspy/piper-voices> (voices) | voices **verified**; program: release as named |
| M2M100 418M | Translation | MAT | <https://huggingface.co/Xenova/m2m100_418M> | **verified** (one small file differs, see below) |
| BLIP VQA base | Questions about a picture | AMQ | ONNX files: in this repository; vocabulary: <https://huggingface.co/Salesforce/blip-vqa-base> | ONNX are MPAI's own export; vocabulary **verified** |
| wav2vec2 emotion | Emotion in the voice | MPD | <https://huggingface.co/audeering/wav2vec2-large-robust-12-ft-emotion-msp-dim> | **source model only** |
| HSEmotion `enet_b0_8_va_mtl` | Emotion in the face | MPD | <https://github.com/HSE-asavchenko/face-emotion-recognition/tree/main/models/affectnet_emotions/onnx> | **verified** |
| SCRFD 10G | Face detection | MAC, ACR | <https://github.com/deepinsight/insightface/releases/tag/v0.7> (`buffalo_l.zip`, `antelopev2.zip`) | **verified** |
| ArcFace R100 (`glintr100`) | Face identity | MAC, ACR | <https://github.com/deepinsight/insightface/releases/tag/v0.7> (`antelopev2.zip`) | **a variant**: not the same file |
| ECAPA-TDNN | Voice identity | MAC, ACR | <https://huggingface.co/speechbrain/spkrec-ecapa-voxceleb> | **source model only** |
| Llama 3.2 3B | Dialogue | MAD, MPD | <https://ollama.com/library/llama3.2> | `ollama pull llama3.2:3b` |

Total: the files below are about 7.6 GB, plus the Whisper and Piper program folders and the language model that Ollama keeps.

## Speech recognition - Whisper (whisper.cpp)

Used by all the Apps. Two parts.

- **The program.** whisper.cpp, <https://github.com/ggml-org/whisper.cpp/releases>. Windows: the `whisper-bin-x64.zip` of a release up to `v1.9.2`
  (later releases publish other builds); the whole `bin` folder is needed, as `Models\Whisper\bin`. Linux: build it with `MAS/Service/linux/build-whisper.sh`.
  The release that the hashes below belong to has not been identified, so a different one has a different hash.
- **The model.** `ggml-small.bin`, <https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin> - **verified**.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `Whisper\bin\whisper-cli.exe` | 479.2 KB | `800A0FD754AFA75E109C7248286AD735670FB6B23D92CA5D12604647EF638A65` |
| `Whisper\bin\whisper-server.exe` | | `9EB6EE297215F07BA77A6D588A6A2715F2235F665528529C377B775BBAB3CD2D` |
| `Whisper\models\ggml-small.bin` | 487.6 MB | `1BE3A9B2063867B937E64E2EC7483364A79917E157FA98C5D94B5C1FFFEA987B` |

## Speech synthesis - Piper

Used by all the Apps.

- **The program.** <https://github.com/rhasspy/piper/releases/tag/2023.11.14-2>: `piper_windows_amd64.zip` (Windows) or `piper_linux_x86_64.tar.gz` (Linux).
  The whole `piper` folder is needed, as `Models\Piper\piper_windows_amd64\piper`.
- **The voices.** <https://huggingface.co/rhasspy/piper-voices>. For each, the `.onnx` and its `.onnx.json` from the same folder. The eight .onnx files and seven of the eight .onnx.json files are **verified**; the Japanese .onnx.json is not identical to the published one (the reason is not known), and the published file is expected to work but has not been tested.
  The Japanese voice is published as `ja_JP-hi_fi_captain-medium`; Thalia stores it as `ja_JA-...`, which is the name the settings use.

| File under `Models\` | Size | SHA-256 | Download |
|---|---|---|---|
| `Piper\piper_windows_amd64\piper\piper.exe` | 510.0 KB | `96F3DA3811151580073E40BB4DD20EB0FB8115F5F5F76E2FB54282B3EDFA5C1F` | the release above |
| `Piper\voices\de_DE-eva_k-x_low\de_DE-eva_k-x_low.onnx` | 20.6 MB | `E88CF290FBFB768BF111330D2E8A46E376B0D85E3423A28BFEBBC863A260DAD8` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/de/de_DE/eva_k/x_low/de_DE-eva_k-x_low.onnx> |
| `Piper\voices\de_DE-eva_k-x_low\de_DE-eva_k-x_low.onnx.json` | | `EF14B3DCB279AB4B18422A7A132877BEE7A148821BD91152FB7AE9C4B3D79625` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/de/de_DE/eva_k/x_low/de_DE-eva_k-x_low.onnx.json> |
| `Piper\voices\en_US-amy-medium\en_US-amy-medium.onnx` | 63.2 MB | `B3A6E47B57B8C7FBE6A0CE2518161A50F59A9CDD8A50835C02CB02BDD6206C18` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx> |
| `Piper\voices\en_US-amy-medium\en_US-amy-medium.onnx.json` | | `95A23EB4D42909D38DF73BB9AC7F45F597DBFCDE2D1BF9526FDEAF5466977D77` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx.json> |
| `Piper\voices\es_ES-davefx-medium\es_ES-davefx-medium.onnx` | 63.2 MB | `6658B03B1A6C316EE4C265A9896ABC1393353C2D9E1BCA7D66C2C442E222A917` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/es/es_ES/davefx/medium/es_ES-davefx-medium.onnx> |
| `Piper\voices\es_ES-davefx-medium\es_ES-davefx-medium.onnx.json` | | `0E0DDA87C732F6F38771FF274A6380D9252F327DCA77AA2963D5FBDF9EC54842` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/es/es_ES/davefx/medium/es_ES-davefx-medium.onnx.json> |
| `Piper\voices\fr_FR-siwis-medium\fr_FR-siwis-medium.onnx` | 63.2 MB | `641D1AB097DA2B81128C076810EDB052B385DECC8BE3381814802A64A73BAF99` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/fr/fr_FR/siwis/medium/fr_FR-siwis-medium.onnx> |
| `Piper\voices\fr_FR-siwis-medium\fr_FR-siwis-medium.onnx.json` | | `39479916C2DB192B5AC9764DADDD0C744D83E023AD890C6976C0633AE4DF8959` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/fr/fr_FR/siwis/medium/fr_FR-siwis-medium.onnx.json> |
| `Piper\voices\it_IT-paola-medium\it_IT-paola-medium.onnx` | 63.5 MB | `6FC918B5A0EA6137382833DDDFA567BFFBE6A5060C02043C87192EE59C04210C` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/it/it_IT/paola/medium/it_IT-paola-medium.onnx> |
| `Piper\voices\it_IT-paola-medium\it_IT-paola-medium.onnx.json` | | `AEA19C0A7FCE29FBC359B93F10E7902854401E4C95AE2EA328AE516B15D296CF` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/it/it_IT/paola/medium/it_IT-paola-medium.onnx.json> |
| `Piper\voices\ja_JA-hi_fi_captain-medium\ja_JA-hi_fi_captain-medium.onnx` | 76.8 MB | `5EAFA1610FC7A0FF2E7FDE9CBE0972D876266E23D8DB331727EB2466F19460EB` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/ja/ja_JP/hi_fi_captain/medium/ja_JP-hi_fi_captain-medium.onnx> |
| `Piper\voices\ja_JA-hi_fi_captain-medium\ja_JA-hi_fi_captain-medium.onnx.json` | | `542EB0B6389CD89CA02AE662700E1DEC49EBF1B612821C4ED2E1D1CA4E0D257C` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/ja/ja_JP/hi_fi_captain/medium/ja_JP-hi_fi_captain-medium.onnx.json> |
| `Piper\voices\pt_BR-cadu-medium\pt_BR-cadu-medium.onnx` | 63.0 MB | `765F0809A6EA9035D4A6D0D008DBF8876E68B2DD32029312672FA8F405BDB535` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/pt/pt_BR/cadu/medium/pt_BR-cadu-medium.onnx> |
| `Piper\voices\pt_BR-cadu-medium\pt_BR-cadu-medium.onnx.json` | | `5FE03AA3D4901880554905B12075713CD552598C8A350455A1EC73F8B4E6BE19` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/pt/pt_BR/cadu/medium/pt_BR-cadu-medium.onnx.json> |
| `Piper\voices\zh_CN-huayan-medium\zh_CN-huayan-medium.onnx` | 63.2 MB | `9929917BF8CABB26FD528EA44D3A6699C11E87317A14765312420BE230BE0F3D` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/zh/zh_CN/huayan/medium/zh_CN-huayan-medium.onnx> |
| `Piper\voices\zh_CN-huayan-medium\zh_CN-huayan-medium.onnx.json` | | `D521DC45504A8CCC99E325822B35946DD701840BFB07E3DBB31A40929ED6A82B` | <https://huggingface.co/rhasspy/piper-voices/resolve/main/zh/zh_CN/huayan/medium/zh_CN-huayan-medium.onnx.json> |

## Translation - M2M100 (418M), ONNX

Used by MAT. Source: Meta `facebook/m2m100_418M`, in the ONNX form published by <https://huggingface.co/Xenova/m2m100_418M> (folder `onnx`). It is stored under `Whisper\M2M100\` for historical reasons; it is not part of Whisper.

| File under `Models\` | Size | SHA-256 | Download |
|---|---|---|---|
| `Whisper\M2M100\decoder_model.onnx` | 1,335.6 MB | `0523BCDAB1AADD68FB8A16CA8FF7637547D8A352F6149F7223869D1BD0122DBB` | <https://huggingface.co/Xenova/m2m100_418M/resolve/main/onnx/decoder_model.onnx> |
| `Whisper\M2M100\decoder_model_merged_quantized.onnx` | 344.1 MB | `007654BCABB6CEA6FD3BDE34CE933137B431330B3755781145D7B6906270B45A` | <https://huggingface.co/Xenova/m2m100_418M/resolve/main/onnx/decoder_model_merged_quantized.onnx> |
| `Whisper\M2M100\decoder_with_past_model.onnx` | 1,234.8 MB | `BDE055E103A0C12F2528147CCE7C8C5374A867D6281D870137018A4FAF842E6B` | <https://huggingface.co/Xenova/m2m100_418M/resolve/main/onnx/decoder_with_past_model.onnx> |
| `Whisper\M2M100\encoder_model.onnx` | 1,133.7 MB | `2AAF1612C2738DEC7DE0A12D3F5EBB1168A38B735A33DB5BE7719DD669750544` | <https://huggingface.co/Xenova/m2m100_418M/resolve/main/onnx/encoder_model.onnx> |
| `Whisper\M2M100\sentencepiece.bpe.model` | 2.4 MB | `D8F7C76ED2A5E0822BE39F0A4F95A55EB19C78F4593CE609E2EDBC2AEA4D380A` | <https://huggingface.co/Xenova/m2m100_418M/resolve/main/sentencepiece.bpe.model> |
| `Whisper\M2M100\special_tokens_map.json` | 1.1 KB | `C1A4F86C3874D279AE1B2A05162858DB5DD6C61665D84223ED886CBCFF08FDA6` | <https://huggingface.co/Xenova/m2m100_418M/resolve/main/special_tokens_map.json> |
| `Whisper\M2M100\vocab.json` | 3.7 MB | `B6E77E474AEEA8F441363ACA7614317C06381F3EACFE10FB9856D5081D1074CC` | <https://huggingface.co/Xenova/m2m100_418M/resolve/main/vocab.json> |

Every file is **verified** except `special_tokens_map.json`: the file now published has a different hash from the one Thalia was tested with (it is a small file of special-token names; the model works from either, but this has not been tested).

## Picture question answering - BLIP VQA base, ONNX

Used by AMQ. Source: Salesforce `Salesforce/blip-vqa-base`, <https://huggingface.co/Salesforce/blip-vqa-base>. MPAI exported it to ONNX; the three ONNX files and the weights file are **in this repository**, in `Models/BLIP/onnx/`.
The vocabulary comes from the source model: <https://huggingface.co/Salesforce/blip-vqa-base/resolve/main/vocab.txt> - **verified**. `blip_vision_model.onnx` keeps its weights in `blip_vision_model.onnx.data`, which must sit beside it.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `BLIP\blip-vqa-base\vocab.txt` | 231.5 KB | `07ECED375CEC144D27C900241F3E339478DEC958F92FDDBC551F295C992038A3` |
| `BLIP\onnx\blip_decoder_context_dynamic.onnx` | 645.7 MB | `554B87CD9892D34CBF9029F2AA4B9EE8D4513AC561EB78EC64D17646F0191991` |
| `BLIP\onnx\blip_text_encoder_wrapper.onnx` | 549.4 MB | `BA2BACC981297823B2ADE32D49ECCA847DB4A39CC6637EB716EAD45DD5E2C530` |
| `BLIP\onnx\blip_vision_model.onnx` | 740.4 KB | `161F6D21D7380C22568F4E41A4F764410CF157BE9FAC75E3BB064EA4DEBB82DC` |
| `BLIP\onnx\blip_vision_model.onnx.data` | 344.5 MB | `1B099A7F3F04A41127DD6CD54CBBFBDEF832518F15A4A6C2CD9AEB4ABA4A5C80` |

## Emotion in the voice - wav2vec2

Used by MPD. Source: audeering, <https://huggingface.co/audeering/wav2vec2-large-robust-12-ft-emotion-msp-dim> (PyTorch and safetensors weights; licence **CC BY-NC-SA 4.0**, non-commercial).
**Source model only:** that page publishes no ONNX file. The `model.onnx` Thalia uses is a conversion of it that has not been published; convert the model with a standard PyTorch-to-ONNX export, or ask MPAI.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `w2v2-emotion\model.onnx` | 661.4 MB | `9B6E449686C0DB86F5C607B8C9FA1D87468C27198A1F0A20280C4E258239763D` |

## Emotion in the face - HSEmotion

Used by MPD. Source: <https://github.com/HSE-asavchenko/face-emotion-recognition>; the file is <https://github.com/HSE-asavchenko/face-emotion-recognition/raw/main/models/affectnet_emotions/onnx/enet_b0_8_va_mtl.onnx> - **verified**.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `hsemotion_enet_b0_8_va_mtl.onnx` | 16.0 MB | `C43E056AD388D4A8DC911832B8291435B2AF537F967E5870EBD731574EC7E812` |

## Face detection and identity - SCRFD and ArcFace (InsightFace)

Used by MAC and ACR (access control and registration). Source: InsightFace, <https://github.com/deepinsight/insightface>; the models are in the release <https://github.com/deepinsight/insightface/releases/tag/v0.7>.
InsightFace's pretrained models are for **non-commercial research**: read their licence.

- **`scrfd_10g_bnkps.onnx`** is the same file as `det_10g.onnx` in `buffalo_l.zip` and as `scrfd_10g_bnkps.onnx` in `antelopev2.zip` - **verified** by SHA-256 against the copies published at <https://huggingface.co/immich-app/buffalo_l> and <https://huggingface.co/immich-app/antelopev2>.
- **`glintr100.onnx` is a variant.** The one in `antelopev2.zip` has 260,665,334 bytes; the one Thalia was tested with has 260,694,151 bytes and another hash. The stock file very probably works, but this has not been tested.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `scrfd_10g_bnkps.onnx` | 16.1 MB | `5838F7FE053675B1C7A08B633DF49E7AF5495CEE0493C7DCF6697200B85B5B91` |
| `glintr100.onnx` | 248.6 MB | `A7933EA5330113B01C9B60351D8F4C33003F145D8470AC5F0E52EE2EFFE25C60` |

## Voice identity - ECAPA-TDNN

Used by MAC and ACR. Source: SpeechBrain, <https://huggingface.co/speechbrain/spkrec-ecapa-voxceleb> (PyTorch checkpoints).
**Source model only:** no published ONNX file has the same hash as the one Thalia uses, which is a conversion of that model. Convert the model, or ask MPAI.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `ecapa-tdnn.onnx` | 79.4 MB | `38FDFC7D2BC9E2925349BAAB9639FCAF4B4C755BE83EE7D616B6E3FBC9D5EAB3` |

## Dialogue - Llama 3.2 3B, served by Ollama

Used by MAD and MPD. Not a file under `Models\`: install Ollama (<https://ollama.com>) and run `ollama pull llama3.2:3b` (<https://ollama.com/library/llama3.2>).
Another Ollama model can be named in `AIMs/aim-settings.json` (`OllamaModel`).

## Before you redistribute any of these

The addresses above were checked on 2026-10-10: the hash of each file listed as **verified** matches the published one. Check the licence of each model before you use it commercially or pass it on:
several are research or non-commercial licences (wav2vec2 emotion: CC BY-NC-SA 4.0; InsightFace models: non-commercial research), and each Piper voice has its own licence.
