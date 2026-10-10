"""Export the 3D-Speaker ECAPA-TDNN speaker model to ONNX, as Thalia's Apps MAC and ACR use it.

The model   iic/speech_ecapa-tdnn_sv_zh-cn_3dspeaker_16k   (Apache-2.0)
            https://www.modelscope.cn/models/iic/speech_ecapa-tdnn_sv_zh-cn_3dspeaker_16k
            file ecapa-tdnn.ckpt (83,310,835 bytes), 80-dimensional filterbank in, 192-dimensional embedding out.
The code    https://github.com/modelscope/3D-Speaker   (Apache-2.0)

    git clone --depth 1 https://github.com/modelscope/3D-Speaker.git
    pip install torch onnx onnxruntime numpy
    python export_ecapa_tdnn.py 3D-Speaker ecapa-tdnn.ckpt ecapa-tdnn.onnx

Place the result at Models/ecapa-tdnn.onnx.

The model takes log-Mel filterbank features of shape (1, frames, 80). Thalia's AIM gives it 360 frames (3.6 s), so the
export fixes that length; give another number as a fourth argument, or 0 for a dynamic length.

This is the recipe of 3D-Speaker's own export (speakerlab/bin/export_speaker_embedding_onnx.py): opset 11, constant folding,
input `feature`, output `embedding`. Its result is close to the file Thalia was tested with (cosine similarity 0.998 to 0.9999
on test inputs), not identical: check it with your own speakers.
"""
import sys

import torch

clone, checkpoint, out = sys.argv[1:4]
frames = int(sys.argv[4]) if len(sys.argv) > 4 else 360
sys.path.insert(0, clone)
from speakerlab.models.ecapa_tdnn.ECAPA_TDNN import ECAPA_TDNN  # noqa: E402

model = ECAPA_TDNN(input_size=80, lin_neurons=192, channels=[1024, 1024, 1024, 1024, 3072])
model.load_state_dict(torch.load(checkpoint, map_location="cpu"))
model.eval()
dummy = torch.randn(1, frames or 345, 80)
torch.onnx.export(
    model, dummy, out,
    export_params=True, opset_version=11, do_constant_folding=True,
    input_names=["feature"], output_names=["embedding"],
    dynamic_axes=None if frames else {"feature": {0: "batch_size", 1: "frame_num"}, "embedding": {0: "batch_size"}},
)
print("written", out)
