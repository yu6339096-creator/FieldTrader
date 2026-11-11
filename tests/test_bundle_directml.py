import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools/Packaging'))
import bundle_directml as b

class BundleTests(unittest.TestCase):
    def fake_install(self, command, **kwargs):
        self.command = command
        stage = Path(command[command.index('--target')+1])
        (stage/'DirectML.dll').write_bytes(b'dml')
        (stage/'onnxruntime_pybind11_state.pyd').write_bytes(b'ort')
        (stage/'LICENSE').write_text('license')

    def test_offline_bundle_manifest_and_replacement(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(b.subprocess,'run',side_effect=self.fake_install):
            target=Path(temp)/b.RELATIVE;target.mkdir(parents=True);(target/'stale').touch()
            b.bundle(temp,Path(temp)/'wheels')
            self.assertIn('--no-index',self.command)
            self.assertIn('--no-deps',self.command)
            self.assertFalse((target/'stale').exists())
            manifest=json.loads((target/'bundle-manifest.json').read_text())
            self.assertIn('LICENSE',manifest['Files'])
            self.assertEqual(len(manifest['Files']['DirectML.dll']),64)

    def test_missing_dll_fails_without_replacing_existing(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(b.subprocess,'run'):
            target=Path(temp)/b.RELATIVE;target.mkdir(parents=True);(target/'old').touch()
            with self.assertRaisesRegex(RuntimeError,'missing native'):
                b.bundle(temp)
            self.assertTrue((target/'old').exists())

if __name__=='__main__':unittest.main()
