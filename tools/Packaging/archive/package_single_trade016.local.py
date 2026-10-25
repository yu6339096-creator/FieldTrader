from pathlib import Path
import json,shutil,subprocess,hashlib,os

root=Path(__file__).resolve().parents[1]
repo=root.parent/'referenced-chatgpt-conversation-this-is-an'/'work'/'FieldTrader'
payload=root/'work'/'installer-payload'
out=root/'outputs'/'release-0.8.16';out.mkdir(exist_ok=True)
source=repo/'src'/'FieldTrader'/'bin'/'Release'/'net8.0-windows'
shutil.copytree(source,payload,dirs_exist_ok=True,ignore=shutil.ignore_patterns('*.pdb','startup-error.txt'))

# The installed entry point requests normal Windows UAC; its runtime children inherit its token.
launcher=payload/'FieldTraderLauncher.exe'
compiler_csc=Path(os.environ.get('WINDIR','C:/Windows'))/'Microsoft.NET'/'Framework64'/'v4.0.30319'/'csc.exe'
manifest_file=repo/'src'/'FieldTrader'/'app.manifest'
compiled=subprocess.run([str(compiler_csc),'/nologo','/target:winexe','/platform:x64','/win32manifest:'+str(manifest_file),'/out:'+str(launcher),'/reference:System.Windows.Forms.dll',str(repo/'tools'/'InstalledLauncher.cs')],capture_output=True,text=True)
assert compiled.returncode==0, compiled.stdout+compiled.stderr
launcher_bytes=launcher.read_bytes()
assert b'level="requireAdministrator"' in launcher_bytes and b'uiAccess="false"' in launcher_bytes

(payload/'startup-error.txt').unlink(missing_ok=True)
# The isolated interpreter must resolve our bundled sibling item_vision.py module.
(payload/'VisionRuntime'/'python'/'python312._pth').write_text('python312.zip\n.\nDLLs\nLib\nLib/site-packages\n../../Market\nimport site\n',encoding='utf-8')
cfg=json.loads((payload/'market-vision.local.json').read_text(encoding='utf-8-sig'))
items=json.loads((root/'work'/'dick-vision.json').read_text(encoding='utf-8-sig'))
model=Path(items['ItemYoloModel']);assert hashlib.sha256(model.read_bytes()).hexdigest()==items['ItemYoloSha256']
shutil.copy2(model,payload/'VisionModels'/'items22-selected.pt')
for key in ['ItemYoloSha256','ItemClasses'] :cfg[key]=items[key]
cfg['ItemYoloModel']='VisionModels/items22-selected.pt'
cfg['ItemNavigationClasses']=[] # This release enables recognition, not unvalidated game input.
(payload/'market-vision.local.json').write_text(json.dumps(cfg,ensure_ascii=False,indent=2),encoding='utf-8')
notes='战地行商 0.8.16 · 默认管理员启动 · 每轮自动重置 · OCR 75%与真实系统鼠标\n\n本版删除跨轮未决拦截。默认从启动器及安装快捷方式请求管理员权限，启动时请在系统UAC中点击“是”。界面会显示“管理员模式：已启用”。每轮结束、急停或异常后均重置运行状态；下一次主动开始直接重新截图和核价，旧日志不会拦截。历史结果仍原样保留，不改写为成功。\n\n本版统一修改商品名称、页面文字、材料数量、补齐报价、确认弹窗、结果核对的OCR接受门槛为75%，不修改模型的原始置信度。YOLO保留辅助判断。\n真实执行使用Windows SendInput进行绝对坐标移动、到位核对、按下和松开。每一步记录到native-input-events.jsonl。没有额外的驱动或输入限制绕过。\n\n使用方法：\n1. 关闭旧版，安装本版到原目录。\n2. 游戏中打开拉里100D战地医疗包的兑换详情，或市场目标商品购买详情。\n3. 程序中选择操作、填写商品和预算、勾选启用并开始，4秒内返回游戏。\n程序会显示识别、重新核价、鼠标输入和结果核对的当前步骤。\n\n当前入口每轮只交易一笔；100D兑换数量为1，市场数量1至5。不包含出售、自动刷新或大厅巡回批量交易。\n不需要用户逐笔确认。游戏出现匹配目标的集齐道具弹窗，程序自动点击确定。\n全局急停按界面显示的F8或备用组合键；关闭自动开关会停止后续操作。价格变化、商品不符、输入受阻或结果未知会结束本轮，不在本轮自动重试；再次主动开始就是全新一轮。\n只有核对到成功证据才入账，报价不会冒充实际扣款。\n\n验证：35项交易流程检查、81项导航检查；用户失败记录中的33022补齐价回归通过；普通Windows桌面测试窗口实际收到1次按下、1次松开、1次点击。\n本版本尚未完成真实游戏内自动兑换成功验收。\n'
(payload/'市场只读采集实测说明.txt').write_text(notes,encoding='utf-8-sig')
(out/'安装与功能范围.txt').write_text(notes,encoding='utf-8-sig')
iss=(repo/'installer'/'FieldTrader-vision.iss').read_text(encoding='utf-8-sig')
iss=iss.replace('0.8.6','0.8.16').replace('市场只读采集测试版','识别与利润建议测试版').replace('FieldTrader-0.8.16-Readonly-Setup','FieldTrader-0.8.16-SingleTrade-Setup')
iss=iss.replace('MessagesFile: "ChineseSimplified.isl"','MessagesFile: "'+str(repo/'installer'/'ChineseSimplified.isl')+'"')
iss=iss.replace('14类YOLO11m原权重和OCR运行组件','YOLO11m界面模型、22类实验物品模型和OCR运行组件')
iss=iss.replace('8%仅为未验证临时估算','缺少已核验费用时净利润显示未知')
iss=iss.replace('战地行商 · 市场只读采集','战地行商 · 市场识别与利润建议')
script=out/'FieldTrader-0.8.16.iss';script.write_text(iss,encoding='utf-8-sig')
compiler=root.parents[1]/'2026-09-22'/'referenced-chatgpt-conversation-this-is-an'/'work'/'installer'/'inno'/'ISCC.exe'
with (out/'installer-build.log').open('w',encoding='utf-8') as log:
 result=subprocess.run([str(compiler),'/DPayloadDir='+str(payload),'/DPackageOutput='+str(out),str(script)],stdout=log,stderr=subprocess.STDOUT)
assert result.returncode==0, 'See installer-build.log'
exe=out/'FieldTrader-0.8.16-SingleTrade-Setup.exe'
manifest={'Version':'0.8.16','InstallerBytes':exe.stat().st_size,'InstallerSha256':hashlib.sha256(exe.read_bytes()).hexdigest(),'AppDllSha256':hashlib.sha256((payload/'FieldTrader.dll').read_bytes()).hexdigest(),'UiModelSha256':cfg['YoloSha256'],'ItemModelSha256':cfg['ItemYoloSha256'],'BundledRuntime':True,'GameTradeAutomationEnabled':True,'GameMouseInputEnabled':True,'UserRequestedUnattendedTradesDelivered':False,'RealGameTest':'One manually verified exchange; not automatic execution'}
(out/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(manifest),flush=True)







manifest.update({'OcrMinimumConfidence':0.75,'MouseTransport':'Windows SendInput absolute virtual desktop move, verified position, down/up','NativeDesktopCheck':'Passed: owned test window received exactly one button down, up and click; GameInputs=0'})
(out/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')

manifest.update({'PerRunReset':True,'HistoricalJournalBlocksNewRuns':False})
(out/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')

manifest.update({'DefaultRunAsAdministrator':True,'LauncherManifest':'requireAdministrator; uiAccess=false','LauncherSha256':hashlib.sha256(launcher.read_bytes()).hexdigest()})
(out/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
