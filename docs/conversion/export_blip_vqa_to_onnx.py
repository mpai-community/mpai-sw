"""Export Salesforce's BLIP VQA base (Hugging Face `Salesforce/blip-vqa-base`, BSD-3-Clause) to the three ONNX files AMQ uses.

    pip install torch transformers onnx
    python export_blip_vqa_to_onnx.py <folder of blip-vqa-base> <output folder>

Place the three files, and `blip_vision_model.onnx.data` that the first writes beside itself, in Models/BLIP/onnx/, and the
model's `vocab.txt` in Models/BLIP/blip-vqa-base/.

  blip_vision_model.onnx           the vision encoder:  pixel_values (1, 3, 384, 384) -> last_hidden_state
  blip_text_encoder_wrapper.onnx   the question encoder: input_ids, attention_mask, encoder_hidden_states; any question length
  blip_decoder_context_dynamic.onnx the answer decoder:  input_ids, encoder_hidden_states; any context length

This is the recipe MPAI used to make the files that are in Models/BLIP/onnx of this repository (the ONNX opsets and the
`dynamo=False` exporter are those of that time). It has not been re-run with the latest versions of the libraries: with a newer
torch, the exporter options may need adjusting, and the result should be compared with the files in this repository.
"""
import sys
from pathlib import Path

import torch
import torch.nn as nn
from transformers import BlipForQuestionAnswering

model_dir, output_dir = Path(sys.argv[1]), Path(sys.argv[2])
output_dir.mkdir(parents=True, exist_ok=True)
model = BlipForQuestionAnswering.from_pretrained(str(model_dir)).eval()


class EncoderWrapper(nn.Module):
    def __init__(self, encoder):
        super().__init__()
        self.encoder = encoder

    def forward(self, input_ids, attention_mask, encoder_hidden_states):
        return self.encoder(input_ids=input_ids, attention_mask=attention_mask,
                            encoder_hidden_states=encoder_hidden_states, return_dict=False, use_cache=False)[0]


class DecoderWrapper(nn.Module):
    def __init__(self, decoder):
        super().__init__()
        self.decoder = decoder

    def forward(self, input_ids, encoder_hidden_states):
        return self.decoder(input_ids=input_ids, encoder_hidden_states=encoder_hidden_states,
                            use_cache=False, return_dict=False)[0]


# 1. the vision encoder
pixel_values = torch.randn(1, 3, 384, 384)
torch.onnx.export(model.vision_model, (pixel_values,), str(output_dir / "blip_vision_model.onnx"),
                  export_params=True, opset_version=18, do_constant_folding=True,
                  input_names=["pixel_values"], output_names=["last_hidden_state"])

# 2. the question encoder: the question length is dynamic
encoder = EncoderWrapper(model.text_encoder).eval()
input_ids = torch.tensor([[2054, 2003, 2023, 2311, 1029, 102, 0]], dtype=torch.long)
attention_mask = torch.tensor([[1, 1, 1, 1, 1, 1, 0]], dtype=torch.long)
context = torch.zeros((1, 577, 768), dtype=torch.float32)
torch.onnx.export(encoder, (input_ids, attention_mask, context), str(output_dir / "blip_text_encoder_wrapper.onnx"),
                  export_params=True, opset_version=17, do_constant_folding=True,
                  input_names=["input_ids", "attention_mask", "encoder_hidden_states"], output_names=["last_hidden_state"],
                  dynamic_axes={"input_ids": {1: "sequence_length"}, "attention_mask": {1: "sequence_length"},
                                "last_hidden_state": {1: "sequence_length"}},
                  dynamo=False)

# 3. the answer decoder: the context length is dynamic
decoder = DecoderWrapper(model.text_decoder).eval()
start = torch.tensor([[30522]], dtype=torch.long)
context = torch.zeros((1, 7, 768), dtype=torch.float32)
torch.onnx.export(decoder, (start, context), str(output_dir / "blip_decoder_context_dynamic.onnx"),
                  export_params=True, opset_version=17, do_constant_folding=True,
                  input_names=["input_ids", "encoder_hidden_states"], output_names=["logits"],
                  dynamic_axes={"input_ids": {1: "sequence_length"}, "encoder_hidden_states": {1: "context_length"},
                                "logits": {1: "sequence_length"}},
                  dynamo=False)
print("written to", output_dir)
