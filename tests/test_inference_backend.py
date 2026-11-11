import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src/FieldTrader/Market'))
from inference_backend import FallbackEngine, decode_yolo, gpu_session
import numpy as np

class BackendTests(unittest.TestCase):
    def test_gpu_success(self):
        cpu = Mock()
        engine = FallbackEngine(lambda: SimpleNamespace(predict=lambda: 7), cpu)
        self.assertEqual(engine.predict(), 7)
        self.assertEqual(engine.provider, 'DirectML')
        cpu.assert_not_called()

    def test_initialization_failure(self):
        engine = FallbackEngine(Mock(side_effect=RuntimeError('missing provider')), lambda: SimpleNamespace(predict=lambda: 2))
        self.assertEqual(engine.predict(), 2)
        self.assertIn('missing provider', engine.status())

    def test_inference_failure_is_sticky(self):
        gpu = Mock(); gpu.predict.side_effect = RuntimeError('device removed')
        cpu = Mock(); cpu.predict.return_value = 3
        engine = FallbackEngine(lambda: gpu, lambda: cpu)
        self.assertEqual(engine.predict(), 3)
        self.assertEqual(engine.predict(), 3)
        self.assertEqual(gpu.predict.call_count, 1)
        self.assertEqual(cpu.predict.call_count, 2)
        self.assertIn('device removed', engine.status())

    def test_cpu_errors_propagate(self):
        engine = FallbackEngine(Mock(), lambda: SimpleNamespace(predict=Mock(side_effect=ValueError('bad frame'))), False)
        with self.assertRaisesRegex(ValueError, 'bad frame'): engine.predict()

    def test_cpu_only_skips_gpu(self):
        gpu = Mock()
        FallbackEngine(gpu, Mock(), False)
        gpu.assert_not_called()

    def test_ocr_recognition_fallback(self):
        gpu = SimpleNamespace(text_rec=Mock(side_effect=RuntimeError('GPU')))
        cpu = SimpleNamespace(text_rec=lambda image: (image, .1))
        engine = FallbackEngine(lambda: gpu, lambda: cpu)
        self.assertEqual(engine.call('text_rec', 'image'), ('image', .1))

    def test_nms_classes_and_coordinate_restore(self):
        output = np.array([[[320,320,320],[320,320,320],[200,200,200],[100,100,100],[.9,.8,.1],[.1,.1,.85]]])
        result = decode_yolo(output, 2, 1280, 720, .5, 0, 140).boxes
        self.assertEqual(len(result.conf), 2)
        self.assertEqual(set(result.cls), {0,1})
        np.testing.assert_allclose(result.xyxy[0], [440,260,840,460])

    def test_empty_predictions(self):
        self.assertEqual(len(decode_yolo(np.zeros((1,6,10)),2,640,640,1,0,0).boxes.conf),0)

    def test_invalid_output_rejected(self):
        for data in (np.zeros((1,7,10)), np.full((1,6,10),np.nan)):
            with self.assertRaises(ValueError): decode_yolo(data,2,640,640,1,0,0)

    def test_directml_session_settings(self):
        session = Mock(); session.get_providers.return_value=['DmlExecutionProvider','CPUExecutionProvider']
        ort = SimpleNamespace(get_available_providers=lambda:['DmlExecutionProvider'], SessionOptions=SimpleNamespace,
            ExecutionMode=SimpleNamespace(ORT_SEQUENTIAL=0),InferenceSession=Mock(return_value=session))
        with patch.dict(sys.modules, onnxruntime=ort), patch('inference_backend.os.name','nt'):
            gpu_session('model.onnx', 1)
        args = ort.InferenceSession.call_args.kwargs
        self.assertFalse(args['sess_options'].enable_mem_pattern)
        self.assertEqual(args['providers'][0][1]['device_id'],1)
        session.disable_fallback.assert_called_once()

if __name__ == '__main__': unittest.main()
