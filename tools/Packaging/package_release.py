"""Build the current Windows installer using a supplied complete runtime payload."""
from pathlib import Path
import argparse,hashlib,json,os,shutil,subprocess

def copy_tree(source,destination,ignore):
 # Bundled Python contains deeply nested paths. Retain them on Windows instead
 # of failing midway through an otherwise complete installer payload.
 def native(path):
  value=str(path.resolve())
  return '\\\\?\\'+value if os.name=='nt' and not value.startswith('\\\\?\\') else value
 shutil.copytree(native(source),native(destination),dirs_exist_ok=True,ignore=ignore)

def main():
 parser=argparse.ArgumentParser(description=__doc__)
 parser.add_argument('--payload-dir',type=Path,required=True,help='Complete runtime resources, e.g. an installed FieldTrader directory')
 parser.add_argument('--inno-compiler',type=Path,required=True)
 parser.add_argument('--dotnet',default='dotnet')
 parser.add_argument('--version',default='0.8.17')
 parser.add_argument('--output',type=Path)
 args=parser.parse_args();repo=Path(__file__).resolve().parents[2]
 output=(args.output or repo/'artifacts'/'release'/args.version).resolve();output.mkdir(parents=True,exist_ok=True)
 payload=output/'payload'
 if payload==args.payload_dir.resolve():raise SystemExit('Output payload must differ from the runtime source')
 for required in ['Runtime/dotnet.exe','VisionRuntime/python/python.exe']:
  if not (args.payload_dir/required).is_file():raise SystemExit('Incomplete payload: '+required)
 subprocess.run([args.dotnet,'build',str(repo/'src/FieldTrader/FieldTrader.csproj'),'-c','Release','-p:UseAppHost=false','-p:Version='+args.version],check=True)
 ignore=shutil.ignore_patterns('*.pdb','startup-error.txt','__pycache__','*.pyc')
 copy_tree(args.payload_dir,payload,ignore)
 copy_tree(repo/'src/FieldTrader/bin/Release/net8.0-windows',payload,ignore)
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
 template=(repo/'installer/FieldTrader-vision.iss').read_text(encoding='utf-8-sig').replace('0.8.6',args.version).replace('Readonly-Setup','ContactExchange-Setup')
 template=template.replace('进入“单笔自动买入/兑换”，填写预算、勾选启用并开始，4秒内切回游戏目标详情页。','进入“自动买入/联络人兑换”，选择联络人串联、勾选商品并填写整轮预算，启用后4秒内切回游戏。')
 template=template.replace('每轮最多处理一笔；','串联模式每轮按勾选清单逐份兑换；').replace('支持从当前详情页执行真实单笔买入、补齐和兑换。','支持联络人串联兑换、补齐，以及原有详情页单笔买入。')
 template=template.replace('MessagesFile: "ChineseSimplified.isl"','MessagesFile: "'+str(repo/'installer/ChineseSimplified.isl')+'"')
 script=output/'FieldTrader.iss';script.write_text(template,encoding='utf-8-sig')
 subprocess.run([str(args.inno_compiler),'/Q','/DPayloadDir='+str(payload),'/DPackageOutput='+str(output),str(script)],check=True)
 print(str(output))

if __name__=='__main__':main()
