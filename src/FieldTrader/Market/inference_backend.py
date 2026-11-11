"""DirectML-first adapters. CPU fallback is sticky for the life of this worker."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
from types import SimpleNamespace

DML = 'DmlExecutionProvider'

class FallbackEngine:
    def __init__(self, gpu_factory, cpu_factory, prefer_gpu=True):
        self.cpu_factory = cpu_factory
        self.reason = ''
        self.provider = 'CPU'
        self.engine = None
        if prefer_gpu:
            try:
                self.engine = gpu_factory()
                self.provider = 'DirectML'
            except Exception as exc:
                self.reason = f'GPU initialization failed: {exc}'
        if self.engine is None:
            self.engine = cpu_factory()

    def call(self, method, *args, **kwargs):
        try:
            return getattr(self.engine, method)(*args, **kwargs)
        except Exception as exc:
            if self.provider == 'CPU':
                raise
            self.reason = f'GPU inference failed: {exc}'
            self.provider = 'CPU'
            self.engine = None  # release device resources before rebuilding
            self.engine = self.cpu_factory()
            return getattr(self.engine, method)(*args, **kwargs)

    def predict(self, *args, **kwargs):
        return self.call('predict', *args, **kwargs)

    def status(self):
        return self.provider + (f' | {self.reason}' if self.reason else '')


def gpu_session(path, device_id=0):
    import onnxruntime as ort
    if os.name != 'nt' or DML not in ort.get_available_providers():
        raise RuntimeError('Windows onnxruntime-directml is unavailable')
    options = ort.SessionOptions()
    options.enable_mem_pattern = False
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    session = ort.InferenceSession(str(path), sess_options=options,
        providers=[(DML, {'device_id': int(device_id)}), 'CPUExecutionProvider'])
    if session.get_providers()[0] != DML:
        raise RuntimeError('ONNX Runtime did not activate DirectML')
    session.disable_fallback()  # our wrapper records and owns any fallback
    return session


def prepare_yolo(source, cache):
    """Export verified local weights into a content-addressed writable cache, never the install dir."""
    from ultralytics import YOLO
    source = Path(source)
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    folder = Path(cache) / ('yolo-onnx17-640-' + digest)
    folder.mkdir(parents=True, exist_ok=True)
    result, manifest = folder / 'model.onnx', folder / 'model.json'
    if result.exists() and manifest.exists():
        expected = json.loads(manifest.read_text())
        if expected.get('SourceSha256') == digest and expected.get('OnnxSha256') == hashlib.sha256(result.read_bytes()).hexdigest():
            return result
    # Export is offline. Missing onnx/export dependencies fail back to the original CPU model.
    os.environ['YOLO_AUTOINSTALL'] = 'false'
    with tempfile.TemporaryDirectory(dir=folder) as staging:
        local = Path(staging) / 'model.pt'
        shutil.copyfile(source, local)
        YOLO(str(local)).export(format='onnx', imgsz=640, batch=1, dynamic=False,
            simplify=False, opset=17, device='cpu', half=False, nms=False)
        exported = local.with_suffix('.onnx')
        if not exported.exists():
            raise RuntimeError('ONNX export did not create model.onnx')
        metadata = Path(staging) / 'model.json'
        metadata.write_text(json.dumps({'SourceSha256': digest,
            'OnnxSha256': hashlib.sha256(exported.read_bytes()).hexdigest()}))
        os.replace(exported, result)
        os.replace(metadata, manifest)
        return result


def decode_yolo(output, classes, width, height, scale, left, top, confidence=.25, iou_limit=.7):
    import numpy as np
    data = np.asarray(output)
    if data.ndim != 3 or data.shape[0] != 1 or data.shape[1] != 4 + classes:
        raise ValueError(f'Unsupported YOLO11 ONNX output shape: {data.shape}')
    rows = data[0].T
    if not np.isfinite(rows).all():
        raise ValueError('Non-finite YOLO output')
    labels = rows[:, 4:].argmax(axis=1)
    scores = rows[np.arange(len(rows)), 4 + labels]
    mask = scores > confidence
    rows, labels, scores = rows[mask], labels[mask], scores[mask]
    boxes = np.column_stack((rows[:, 0]-rows[:, 2]/2, rows[:, 1]-rows[:, 3]/2,
                             rows[:, 0]+rows[:, 2]/2, rows[:, 1]+rows[:, 3]/2))
    # Class-aware NMS, as in the original Ultralytics detect policy.
    order = scores.argsort()[::-1][:30000]
    keep = []
    while len(order) and len(keep) < 300:
        idx = order[0]; keep.append(idx); rest = order[1:]
        lo = np.maximum(boxes[idx, :2], boxes[rest, :2])
        hi = np.minimum(boxes[idx, 2:], boxes[rest, 2:])
        overlap = np.maximum(hi-lo, 0).prod(axis=1)
        area = np.maximum(boxes[:, 2:]-boxes[:, :2], 0).prod(axis=1)
        iou = overlap / np.maximum(area[idx]+area[rest]-overlap, 1e-9)
        order = rest[(iou <= iou_limit) | (labels[rest] != labels[idx])]
    boxes = boxes[keep]
    boxes[:, [0, 2]] = np.clip((boxes[:, [0, 2]]-left)/scale, 0, width)
    boxes[:, [1, 3]] = np.clip((boxes[:, [1, 3]]-top)/scale, 0, height)
    return SimpleNamespace(boxes=SimpleNamespace(xyxy=boxes, conf=scores[keep], cls=labels[keep]))


class OnnxYolo:
    def __init__(self, path, names, device_id=0):
        self.names = names
        self.session = gpu_session(path, device_id)
        self.input_name = self.session.get_inputs()[0].name
        if self.session.get_inputs()[0].shape != [1, 3, 640, 640]:
            raise ValueError('Expected static FP32 YOLO 640 input')

    def predict(self, pixels, imgsz=640, conf=.25, **kwargs):
        import numpy as np
        import cv2
        if imgsz != 640:
            raise ValueError('Only 640 input is supported')
        image = np.asarray(pixels.convert('RGB'))
        height, width = image.shape[:2]
        scale = min(640/width, 640/height)
        w, h = round(width*scale), round(height*scale)
        left, top = round((640-w)/2-.1), round((640-h)/2-.1)
        canvas = np.full((640, 640, 3), 114, dtype=np.uint8)
        canvas[top:top+h, left:left+w] = cv2.resize(image, (w, h))
        tensor = np.ascontiguousarray(canvas.transpose(2, 0, 1)[None], dtype=np.float32)/255
        output = self.session.run(None, {self.input_name: tensor})[0]
        return [decode_yolo(output, len(self.names), width, height, scale, left, top, conf)]


def make_yolo(model, source, cache, prefer_gpu, device_id):
    def gpu():
        import onnxruntime as ort
        if os.name != 'nt' or DML not in ort.get_available_providers():
            raise RuntimeError('DirectML provider unavailable; using original PyTorch CPU weights')
        return OnnxYolo(prepare_yolo(source, cache), model.names, device_id)
    wrapper = FallbackEngine(gpu, lambda: model, prefer_gpu)
    wrapper.names = model.names
    return wrapper


def make_ocr(factory, cfg, prefer_gpu, device_id):
    def gpu():
        ocr = factory()  # preserve existing PP-OCR preprocessing/postprocessing and vocabulary
        # rapidocr-onnxruntime 1.4.x session layout. Unsupported versions explicitly fall back.
        parts = [(ocr.text_det.infer, cfg['OcrDetectionModel']),
                 (ocr.text_cls.infer, cfg['OcrClassificationModel']),
                 (ocr.text_rec.session, cfg['OcrRecognitionModel'])]
        for engine, path in parts:
            engine.session = gpu_session(path, device_id)
        return ocr
    return FallbackEngine(gpu, factory, prefer_gpu)
