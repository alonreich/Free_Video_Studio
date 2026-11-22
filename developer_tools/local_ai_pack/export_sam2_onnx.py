import argparse
import os

import onnx
import torch
from onnxruntime.transformers.float16 import convert_float_to_float16
from sam2.build_sam import build_sam2

CONFIG = "configs/sam2.1/sam2.1_hiera_s.yaml"
ENCODER = "sam2_small_encoder.onnx"
DECODER = "sam2_small_decoder.onnx"


class Encoder(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, image):
        out = self.model.forward_image(image)
        _, feats, _, _ = self.model._prepare_backbone_features(out)
        if self.model.directly_add_no_mem_embed:
            feats[-1] = feats[-1] + self.model.no_mem_embed
        sizes = [(256, 256), (128, 128), (64, 64)]
        maps = [f.permute(1, 2, 0).reshape(1, -1, *s) for f, s in zip(feats[::-1], sizes[::-1])][::-1]
        return maps[2], maps[0], maps[1]


class Decoder(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, image_embed, high_res_feats_0, high_res_feats_1, point_coords, point_labels, mask_input, has_mask_input):
        pe = self.model.sam_prompt_encoder
        sparse = pe._embed_points(point_coords, point_labels, pad=True)
        has = has_mask_input.reshape(1, 1, 1, 1)
        dense = has * pe.mask_downscaling(mask_input) + (1 - has) * pe.no_mask_embed.weight.reshape(1, -1, 1, 1)
        masks, iou, _, obj = self.model.sam_mask_decoder.predict_masks(
            image_embeddings=image_embed,
            image_pe=pe.get_dense_pe(),
            sparse_prompt_embeddings=sparse,
            dense_prompt_embeddings=dense,
            repeat_image=False,
            high_res_features=[high_res_feats_0, high_res_feats_1],
        )
        return masks, iou, obj


def to_fp16(path):
    model = onnx.load(path)
    onnx.save(convert_float_to_float16(model, keep_io_types=True, op_block_list=["Cast", "Resize", "Range"]), path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--fp32", action="store_true", help="keep float32 weights (debugging)")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)

    model = build_sam2(CONFIG, a.checkpoint, device="cpu")
    model.eval()

    enc_path = os.path.join(a.out, ENCODER)
    with torch.no_grad():
        image = torch.randn(1, 3, 1024, 1024)
        torch.onnx.export(Encoder(model), (image,), enc_path, opset_version=17, dynamo=False,
                          input_names=["image"], output_names=["image_embed", "high_res_feats_0", "high_res_feats_1"])
        embed, h0, h1 = Encoder(model)(image)

        dec_path = os.path.join(a.out, DECODER)
        coords = torch.tensor([[[100.0, 100.0], [400.0, 500.0], [250.0, 300.0]]])
        labels = torch.tensor([[2.0, 3.0, 1.0]])
        mask = torch.zeros(1, 1, 256, 256)
        has = torch.zeros(1)
        torch.onnx.export(Decoder(model), (embed, h0, h1, coords, labels, mask, has), dec_path, opset_version=17, dynamo=False,
                          input_names=["image_embed", "high_res_feats_0", "high_res_feats_1", "point_coords",
                                       "point_labels", "mask_input", "has_mask_input"],
                          output_names=["masks", "iou_predictions", "object_scores"],
                          dynamic_axes={"point_coords": {1: "points"}, "point_labels": {1: "points"}})

    if not a.fp32:
        to_fp16(enc_path)
        to_fp16(dec_path)
    for p in (enc_path, dec_path):
        print(f"{p}: {os.path.getsize(p) / 1048576:.1f} MB")


if __name__ == "__main__":
    main()
