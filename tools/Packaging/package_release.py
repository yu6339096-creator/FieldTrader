"""Build the current Windows installer using a supplied complete runtime payload."""
from pathlib import Path
import argparse,hashlib,json,os,shutil,subprocess

def main():
 parser=argparse.ArgumentParser(description=__doc__)
 parser.add_argument('--payload-dir',type=Path,required=True,help='Complete runtime resources, e.g. an installed FieldTrader directory')
 parser.add_argument('--inno-compiler',type=Path,required=True)
 parser.add_argument('--dotnet',default='dotnet')
 parser.add_argument('--version',default='0.8.16')
 parser.add_argument('--output',type=Path)
 args=parser.parse_args();repo=Path(__file__).resolve().parents[2]
 output=(args.output or repo/'artifacts'/'release'/args.version).resolve();output.mkdir(parents=True,exist_ok=True)
 payload=output/'payload'
 if payload==args.payload_dir.resolve():raise SystemExit('Output payload must differ from the runtime source')
 for required in ['Runtime/dotnet.exe','VisionRuntime/python/python.exe']:
  if not (args.payload_dir/required).is_file():raise SystemExit('Incomplete payload: '+required)
 subprocess.run([args.dotnet,'build',str(repo/'src/FieldTrader/FieldTrader.csproj'),'-c','Release','-p:UseAppHost=false','-p:Version='+args.version],check=True)
 ignore=shutil.ignore_patterns('*.pdb','startup-error.txt','__pycache__','*.pyc')
 shutil.copytree(args.payload_dir,payload,dirs_exist_ok=True,ignore=ignore)
 shutil.copytree(repo/'src/FieldTrader/bin/Release/net8.0-windows',payload,dirs_exist_ok=True,ignore=ignore)
 compiler=Path(os.environ.get('WINDIR','C:/Windows'))/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
 subprocess.run([str(compiler),'/nologo','/target:winexe','/platform:x64','/win32manifest:'+str(repo/'src/FieldTrader/app.manifest'),'/out:'+str(payload/'FieldTraderLauncher.exe'),'/reference:System.Windows.Forms.dll',str(repo/'tools/InstalledLauncher.cs')],check=True)
 cfg=json.loads((repo/'config/market-vision.bundle.json').read_text(encoding='utf-8'))
 models=payload/'VisionModels';models.mkdir(exist_ok=True)
 shutil.copy2(repo/'models/ui14/best.pt',models/'best.pt')
 shutil.copy2(repo/'models/items22/model/items22-selected.pt',models/'items22-selected.pt')
 for source in (repo/'models/ocr-v4').iterdir():
  if source.is_file():shutil.copy2(source,models/source.name)
 assert hashlib.sha256((models/'best.pt').read_bytes()).hexdigest()==cfg['YoloSha256']
 assert hashlib.sha256((models/'items22-selected.pt').read_bytes()).hexdigest()==cfg['ItemYoloSha256']
 (payload/'market-vision.local.json').write_text(json.dumps(cfg,ensure_ascii=False,indent=2),encoding='utf-8')
 (payload/'VisionRuntime/python/python312._pth').write_text('python312.zip\n.\nDLLs\nLib\nLib/site-packages\n../../Market\nimport site\n',encoding='utf-8')
 template=(repo/'installer/FieldTrader-vision.iss').read_text(encoding='utf-8-sig').replace('0.8.6',args.version).replace('Readonly-Setup','SingleTrade-Setup')
 template=template.replace('MessagesFile: "ChineseSimplified.isl"','MessagesFile: "'+str(repo/'installer/ChineseSimplified.isl')+'"')
 script=output/'FieldTrader.iss';script.write_text(template,encoding='utf-8-sig')
 subprocess.run([str(args.inno_compiler),'/DPayloadDir='+str(payload),'/DPackageOutput='+str(output),str(script)],check=True)
 print(str(output))

if __name__=='__main__':main()
