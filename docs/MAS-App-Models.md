# MAS-App - Models

The AI models MAS-App uses are not distributed with it. This lists each file:
where it goes under `Models\`, its size and its SHA-256, and where it comes from.
The paths are those named in `AIMs\aim-settings.json`.

In all: 33 files, 7.2 GB, plus the Whisper and Piper program
folders and the language model served by Ollama.

To check a file after obtaining it, in PowerShell:

```
Get-FileHash <file> -Algorithm SHA256
```

A different hash means a different file: a different version or export of the
same model may work, but has not been tested with MAS-App.

## Speech recognition - Whisper (whisper.cpp)

Used by all four Apps. The program: the Windows release of whisper.cpp, <https://github.com/ggerganov/whisper.cpp/releases> - the whole `bin` folder is needed (38 files, 21.2 MB). The model: `ggml-small.bin` from Hugging Face `ggerganov/whisper.cpp`.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `Whisper\bin\whisper-cli.exe` | 479.2 KB | `800A0FD754AFA75E109C7248286AD735670FB6B23D92CA5D12604647EF638A65` |
| `Whisper\models\ggml-small.bin` | 487.6 MB | `1BE3A9B2063867B937E64E2EC7483364A79917E157FA98C5D94B5C1FFFEA987B` |

## Speech synthesis - Piper

Used by all four Apps. The program: `piper_windows_amd64.zip` from <https://github.com/rhasspy/piper/releases> - the whole `piper` folder is needed (368 files, 38.9 MB). The voices: Hugging Face `rhasspy/piper-voices`, each `.onnx` with its `.onnx.json`. The Japanese voice is published there as `ja_JP-hi_fi_captain-medium`; it is stored here under the name `ja_JA-...`, which the settings use.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `Piper\piper_windows_amd64\piper\piper.exe` | 510.0 KB | `96F3DA3811151580073E40BB4DD20EB0FB8115F5F5F76E2FB54282B3EDFA5C1F` |
| `Piper\voices\de_DE-eva_k-x_low\de_DE-eva_k-x_low.onnx` | 20.6 MB | `E88CF290FBFB768BF111330D2E8A46E376B0D85E3423A28BFEBBC863A260DAD8` |
| `Piper\voices\de_DE-eva_k-x_low\de_DE-eva_k-x_low.onnx.json` | 4.2 KB | `EF14B3DCB279AB4B18422A7A132877BEE7A148821BD91152FB7AE9C4B3D79625` |
| `Piper\voices\en_US-amy-medium\en_US-amy-medium.onnx` | 63.2 MB | `B3A6E47B57B8C7FBE6A0CE2518161A50F59A9CDD8A50835C02CB02BDD6206C18` |
| `Piper\voices\en_US-amy-medium\en_US-amy-medium.onnx.json` | 4.9 KB | `95A23EB4D42909D38DF73BB9AC7F45F597DBFCDE2D1BF9526FDEAF5466977D77` |
| `Piper\voices\es_ES-mls_10246-low\es_ES-mls_10246-low.onnx` | 63.1 MB | `3F9D76D2778778942297AAB052AA7B2E67248D3F43614C889EE82901A230E197` |
| `Piper\voices\es_ES-mls_10246-low\es_ES-mls_10246-low.onnx.json` | 4.2 KB | `A865944D7F6972AF347263B0B8152466DABBA3B78B8FE5A6985F7D3B19FD06A1` |
| `Piper\voices\fr_FR-siwis-medium\fr_FR-siwis-medium.onnx` | 63.2 MB | `641D1AB097DA2B81128C076810EDB052B385DECC8BE3381814802A64A73BAF99` |
| `Piper\voices\fr_FR-siwis-medium\fr_FR-siwis-medium.onnx.json` | 4.9 KB | `39479916C2DB192B5AC9764DADDD0C744D83E023AD890C6976C0633AE4DF8959` |
| `Piper\voices\it_IT-paola-medium\it_IT-paola-medium.onnx` | 63.5 MB | `6FC918B5A0EA6137382833DDDFA567BFFBE6A5060C02043C87192EE59C04210C` |
| `Piper\voices\it_IT-paola-medium\it_IT-paola-medium.onnx.json` | 7.1 KB | `AEA19C0A7FCE29FBC359B93F10E7902854401E4C95AE2EA328AE516B15D296CF` |
| `Piper\voices\ja_JA-hi_fi_captain-medium\ja_JA-hi_fi_captain-medium.onnx` | 76.8 MB | `5EAFA1610FC7A0FF2E7FDE9CBE0972D876266E23D8DB331727EB2466F19460EB` |
| `Piper\voices\ja_JA-hi_fi_captain-medium\ja_JA-hi_fi_captain-medium.onnx.json` | 5.3 KB | `542EB0B6389CD89CA02AE662700E1DEC49EBF1B612821C4ED2E1D1CA4E0D257C` |
| `Piper\voices\pt_BR-cadu-medium\pt_BR-cadu-medium.onnx` | 63.0 MB | `765F0809A6EA9035D4A6D0D008DBF8876E68B2DD32029312672FA8F405BDB535` |
| `Piper\voices\pt_BR-cadu-medium\pt_BR-cadu-medium.onnx.json` | 5.0 KB | `5FE03AA3D4901880554905B12075713CD552598C8A350455A1EC73F8B4E6BE19` |
| `Piper\voices\zh_CN-huayan-medium\zh_CN-huayan-medium.onnx` | 63.2 MB | `9929917BF8CABB26FD528EA44D3A6699C11E87317A14765312420BE230BE0F3D` |
| `Piper\voices\zh_CN-huayan-medium\zh_CN-huayan-medium.onnx.json` | 4.8 KB | `D521DC45504A8CCC99E325822B35946DD701840BFB07E3DBB31A40929ED6A82B` |

## Translation - M2M100 (418M), ONNX

Used by MAT. Source: Meta `facebook/m2m100_418M`, as exported to ONNX for transformers.js - most probably Hugging Face `Xenova/m2m100_418M`. Stored under `Whisper\M2M100\` for historical reasons; it is not part of Whisper.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `Whisper\M2M100\decoder_model.onnx` | 1,335.6 MB | `0523BCDAB1AADD68FB8A16CA8FF7637547D8A352F6149F7223869D1BD0122DBB` |
| `Whisper\M2M100\decoder_model_merged_quantized.onnx` | 344.1 MB | `007654BCABB6CEA6FD3BDE34CE933137B431330B3755781145D7B6906270B45A` |
| `Whisper\M2M100\decoder_with_past_model.onnx` | 1,234.8 MB | `BDE055E103A0C12F2528147CCE7C8C5374A867D6281D870137018A4FAF842E6B` |
| `Whisper\M2M100\encoder_model.onnx` | 1,133.7 MB | `2AAF1612C2738DEC7DE0A12D3F5EBB1168A38B735A33DB5BE7719DD669750544` |
| `Whisper\M2M100\sentencepiece.bpe.model` | 2.4 MB | `D8F7C76ED2A5E0822BE39F0A4F95A55EB19C78F4593CE609E2EDBC2AEA4D380A` |
| `Whisper\M2M100\special_tokens_map.json` | 1.1 KB | `C1A4F86C3874D279AE1B2A05162858DB5DD6C61665D84223ED886CBCFF08FDA6` |
| `Whisper\M2M100\vocab.json` | 3.7 MB | `B6E77E474AEEA8F441363ACA7614317C06381F3EACFE10FB9856D5081D1074CC` |

## Picture question answering - BLIP VQA base, ONNX

Used by AMQ. Source: Salesforce `Salesforce/blip-vqa-base` (Hugging Face), exported to ONNX. `blip_vision_model.onnx` keeps its weights in `blip_vision_model.onnx.data`, which must sit beside it.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `BLIP\blip-vqa-base\vocab.txt` | 231.5 KB | `07ECED375CEC144D27C900241F3E339478DEC958F92FDDBC551F295C992038A3` |
| `BLIP\onnx\blip_decoder_context_dynamic.onnx` | 645.7 MB | `554B87CD9892D34CBF9029F2AA4B9EE8D4513AC561EB78EC64D17646F0191991` |
| `BLIP\onnx\blip_text_encoder_wrapper.onnx` | 549.4 MB | `BA2BACC981297823B2ADE32D49ECCA847DB4A39CC6637EB716EAD45DD5E2C530` |
| `BLIP\onnx\blip_vision_model.onnx` | 740.4 KB | `161F6D21D7380C22568F4E41A4F764410CF157BE9FAC75E3BB064EA4DEBB82DC` |
| `BLIP\onnx\blip_vision_model.onnx.data` | 344.5 MB | `1B099A7F3F04A41127DD6CD54CBBFBDEF832518F15A4A6C2CD9AEB4ABA4A5C80` |

## Emotion in the voice - wav2vec2, ONNX

Used by MPD. Source: audeering `audeering/wav2vec2-large-robust-12-ft-emotion-msp-dim` (Hugging Face), exported to ONNX.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `w2v2-emotion\model.onnx` | 661.4 MB | `9B6E449686C0DB86F5C607B8C9FA1D87468C27198A1F0A20280C4E258239763D` |

## Emotion in the face - HSEmotion, ONNX

Used by MPD. Source: HSEmotion `enet_b0_8_va_mtl`, <https://github.com/HSE-asavchenko/face-emotion-recognition>.

| File under `Models\` | Size | SHA-256 |
|---|---|---|
| `hsemotion_enet_b0_8_va_mtl.onnx` | 16.0 MB | `C43E056AD388D4A8DC911832B8291435B2AF537F967E5870EBD731574EC7E812` |

## Dialogue - Llama 3.2 3B, served by Ollama

Used by MAD and MPD. Not a file under `Models\`: install Ollama (<https://ollama.com>)
and run `ollama pull llama3.2:3b`. Another Ollama model can be named in
`AIMs\aim-settings.json` (`OllamaModel`).

## Before redistributing any of these

The sources above were established from each file's name and identity; confirm
each against its origin, and check its licence, before citing it or passing a
file on. Several are research or non-commercial licences, and the Piper voices
each have their own.
