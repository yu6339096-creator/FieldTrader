"""Verify downloaded model/checkpoint and release bytes without loading pickle models."""
from pathlib import Path
import hashlib,json

def main():
 root=Path(__file__).resolve().parents[2]
 manifest=json.loads((root/'models/manifest.json').read_text(encoding='utf-8'))
 for entry in manifest['Files']:
  path=root/entry['path']
  if not path.is_file():raise SystemExit('Missing file: '+entry['path']+'; run git lfs pull')
  digest=hashlib.sha256()
  with path.open('rb') as stream:
   for block in iter(lambda:stream.read(1024*1024),b''):digest.update(block)
  if path.stat().st_size!=entry['bytes'] or digest.hexdigest()!=entry['sha256']:
   raise SystemExit('Hash mismatch or LFS pointer: '+entry['path']+'; run git lfs pull')
 print(json.dumps({'Files':len(manifest['Files']),'AllHashesMatch':True}))

if __name__=='__main__':main()
