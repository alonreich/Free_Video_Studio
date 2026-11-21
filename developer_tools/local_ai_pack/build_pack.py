"""LOCALAIPACK_01 (docs/09 DIST-LOCALAIPACK) - assemble the Local AI pack and pin it in the app.

1. Run export_sam2_onnx.py into <pack folder> first.
2. python developer_tools/local_ai_pack/build_pack.py --pack <pack folder>
   - downloads the OFFICIAL, Microsoft-signed onnxruntime.dll (Microsoft.ML.OnnxRuntime.DirectML) and DirectML.dll
     (Microsoft.AI.DirectML) from nuget.org into the pack folder (the Authenticode signature is checked),
   - writes src/FreeVideoStudio.Core/LocalAi/LocalAiPackFiles.cs with every file's URL, length and SHA-256,
   - prints the `gh release upload` command for the human to run. This script never publishes anything.
3. A human uploads the files to the GitHub release named in LocalAiPackManifest.ReleaseTag, runs this script again
   with --released (Released = true) and commits LocalAiPackFiles.cs. Until then the app says the pack
   "has not been published yet" and Install stays disabled.
"""
import argparse
import hashlib
import io
import os
import subprocess
import sys
import urllib.request
import zipfile

ORT_PACKAGE = ("microsoft.ml.onnxruntime.directml", "1.22.0", "runtimes/win-x64/native/onnxruntime.dll")
DML_PACKAGE = ("microsoft.ai.directml", "1.15.4", "bin/x64-win/DirectML.dll")
MODEL_FILES = ["sam2_small_encoder.onnx", "sam2_small_decoder.onnx"]
RELEASE_TAG = "localai-v1"
BASE_URL = "https://github.com/alonreich/Free_Video_Studio/releases/download/" + RELEASE_TAG + "/"
REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT_CS = os.path.join(REPO, "src", "FreeVideoStudio.Core", "LocalAi", "LocalAiPackFiles.cs")


def fetch_from_nuget(package, version, inner, dest):
    url = f"https://api.nuget.org/v3-flatcontainer/{package}/{version}/{package}.{version}.nupkg"
    print(f"Downloading {package} {version} from nuget.org ...")
    with urllib.request.urlopen(url, timeout=300) as r:
        data = r.read()
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        with z.open(inner) as src, open(dest, "wb") as out:
            out.write(src.read())


def check_signature(path):
    ps = ["powershell", "-NoProfile", "-Command",
          f"(Get-AuthenticodeSignature -LiteralPath '{path}').Status.ToString() + '|' + "
          f"(Get-AuthenticodeSignature -LiteralPath '{path}').SignerCertificate.Subject"]
    res = subprocess.run(ps, capture_output=True, text=True)
    status = res.stdout.strip()
    print(f"  signature: {status}")
    if not status.startswith("Valid|") or "Microsoft" not in status:
        sys.exit(f"Refusing {os.path.basename(path)}: it is not validly signed by Microsoft.")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest().upper()


def write_test_folder(pack, folder):
    import shutil
    import onnx
    from onnx import TensorProto, helper
    os.makedirs(folder, exist_ok=True)
    for name in ("onnxruntime.dll", "DirectML.dll"):
        shutil.copyfile(os.path.join(pack, name), os.path.join(folder, name))
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 4])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [1, 4])
    s = helper.make_tensor_value_info("s", TensorProto.FLOAT, [])
    two = helper.make_tensor("two", TensorProto.FLOAT, [], [2.0])
    graph = helper.make_graph(
        [helper.make_node("Mul", ["x", "two"], ["y"]), helper.make_node("ReduceSum", ["x"], ["s"], keepdims=0)],
        "tiny", [x], [y, s], [two])
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 13)])
    model.ir_version = 8
    onnx.checker.check_model(model)
    onnx.save(model, os.path.join(folder, "tiny.onnx"))
    print(f"Wrote the runtime test folder {folder} (onnxruntime.dll, DirectML.dll, tiny.onnx).")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pack", required=True, help="folder holding the exported .onnx files")
    ap.add_argument("--released", action="store_true",
                    help="only after every file is uploaded to the release: lets the app offer Install")
    ap.add_argument("--test-folder", help="also write the runtime test folder (DLLs + tiny.onnx) used by the A/V gate")
    a = ap.parse_args()
    pack = os.path.abspath(a.pack)
    for name in MODEL_FILES:
        if not os.path.isfile(os.path.join(pack, name)):
            sys.exit(f"{name} is missing; run export_sam2_onnx.py first.")

    for package, version, inner in (ORT_PACKAGE, DML_PACKAGE):
        dest = os.path.join(pack, os.path.basename(inner))
        fetch_from_nuget(package, version, inner, dest)
        check_signature(dest)

    names = ["onnxruntime.dll", "DirectML.dll"] + MODEL_FILES
    if os.path.isfile(os.path.join(pack, "yolov8n.onnx")):
        names.append("yolov8n.onnx")
    rows = []
    for name in names:
        p = os.path.join(pack, name)
        rows.append(f'        new("{name}", LocalAiPackManifest.ReleaseBaseUrl + "{name}", {os.path.getsize(p)}, "{sha256(p)}"),')

    cs = ("namespace FreeVideoStudio.Core.LocalAi;\n\n"
          "internal static class LocalAiPackFiles\n{\n"
          f"    public const bool Released = {'true' if a.released else 'false'};\n\n"
          "    public static readonly LocalAiPackFile[] All =\n    [\n" + "\n".join(rows) + "\n    ];\n}\n")
    with open(OUT_CS, "w", encoding="utf-8", newline="\n") as f:
        f.write(cs)
    if a.test_folder:
        write_test_folder(pack, os.path.abspath(a.test_folder))
    total = sum(os.path.getsize(os.path.join(pack, n)) for n in names)
    print(f"\nWrote {OUT_CS} ({len(names)} files, {total / 1048576:.0f} MB).")
    print("\nA HUMAN uploads the pack (this script does not):")
    files = " ".join(f'"{os.path.join(pack, n)}"' for n in names)
    print(f"  gh release create {RELEASE_TAG} --title \"Local AI pack v1\" --notes \"Optional Local AI pack (not an app release)\" --prerelease")
    print(f"  gh release upload {RELEASE_TAG} {files}")
    if not a.released:
        print("\nThen run this script again with --released and commit LocalAiPackFiles.cs; until then Install stays disabled.")


if __name__ == "__main__":
    main()
