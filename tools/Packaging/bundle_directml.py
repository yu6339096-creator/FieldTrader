"""Stage Windows CPython 3.12 x64 inference wheels using the build machine's pip.
Only the copied release payload is modified. The embedded Python needs no pip.
"""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile

PACKAGES = ['onnxruntime-directml==1.24.4', 'onnx==1.17.0']
RELATIVE = 'VisionRuntime/gpu-packages'


def bundle(payload, wheelhouse=None):
    payload = Path(payload).resolve()
    target = payload / RELATIVE
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=target.parent, prefix='gpu-stage-') as stage:
        command = [sys.executable, '-m', 'pip', 'install', '--disable-pip-version-check',
                   '--only-binary=:all:', '--no-deps', '--no-compile', '--platform', 'win_amd64',
                   '--python-version', '3.12', '--implementation', 'cp', '--abi', 'cp312',
                   '--target', stage]
        if wheelhouse:
            command += ['--no-index', '--find-links', str(Path(wheelhouse).resolve())]
        subprocess.run(command + PACKAGES, check=True)
        root = Path(stage)
        if not list(root.rglob('DirectML.dll')) or not list(root.rglob('onnxruntime_pybind11_state*.pyd')):
            raise RuntimeError('DirectML wheel is missing native runtime files')
        # Includes distribution metadata, license and third-party notices.
        files = {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
                 for p in sorted(root.rglob('*')) if p.is_file()}
        (root / 'bundle-manifest.json').write_text(json.dumps(
            {'Packages': PACKAGES, 'Platform': 'win_amd64', 'Python': '3.12', 'Files': files}, indent=2))
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(root, target)
    return target


def validate(payload):
    """Run with the bundled interpreter: verify actual import paths and CPU execution.
    GPU hardware availability is intentionally not required on the packaging machine.
    """
    payload = Path(payload).resolve()
    check = r'''
import json, pathlib, sys
import numpy, torch, cv2, onnx, onnxruntime as ort
from rapidocr_onnxruntime import RapidOCR
root = pathlib.Path(sys.argv[1]).resolve()
for module in (onnx, ort):
    assert pathlib.Path(module.__file__).resolve().is_relative_to(root), module.__file__
assert 'DmlExecutionProvider' in ort.get_available_providers(), ort.get_available_providers()
x = onnx.helper.make_tensor_value_info('x', onnx.TensorProto.FLOAT, [1])
y = onnx.helper.make_tensor_value_info('y', onnx.TensorProto.FLOAT, [1])
model = onnx.helper.make_model(onnx.helper.make_graph([onnx.helper.make_node('Identity',['x'],['y'])], 'smoke', [x], [y]), opset_imports=[onnx.helper.make_opsetid('',17)], ir_version=8)
session = ort.InferenceSession(model.SerializeToString(), providers=['CPUExecutionProvider'])
assert session.run(None, {'x': numpy.array([42], dtype=numpy.float32)})[0][0] == 42
print(json.dumps({'Providers':ort.get_available_providers(), 'OrtPath':ort.__file__, 'OrtVersion':ort.__version__, 'OnnxVersion':onnx.__version__, 'CpuSmokeTest':'passed'}))
'''
    completed = subprocess.run([str(payload / 'VisionRuntime/python/python.exe'), '-c', check,
                                str(payload / RELATIVE)], check=True, capture_output=True, text=True)
    (payload / 'VisionRuntime/gpu-validation.json').write_text(completed.stdout, encoding='utf-8')
