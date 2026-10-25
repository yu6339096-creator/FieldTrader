from pathlib import Path
import os,sys,json,time,hashlib,shutil
sys.stdout.reconfigure(encoding='utf-8')
TASK=Path(r"C:\Users\yu'ch'yi\Documents\Codex\2026-10-24\referenced-chatgpt-conversation-this-is-an-2")
CFG=json.loads((TASK.parent/'referenced-chatgpt-conversation-this-is-an'/'work'/'vision.local.json').read_text(encoding='utf-8-sig'))
sys.path[:0]=CFG['DependencyDirectories'];OUT=TASK/'outputs'/'item-yolo-v2'
for key,value in {'YOLO_CONFIG_DIR':str(TASK/'work'/'item-yolo-cache'),'MPLCONFIGDIR':str(TASK/'work'/'item-mpl'),'YOLO_AUTOINSTALL':'false','YOLO_OFFLINE':'true','OMP_NUM_THREADS':'8','MKL_NUM_THREADS':'8'}.items():os.environ[key]=value
import torch
torch.set_num_threads(8);torch.set_num_interop_threads(1)
from ultralytics import YOLO
from ultralytics.utils import downloads
_download_asset=downloads.attempt_download_asset
def local_or_download(file,*args,**kwargs):
 if Path(file).is_file():return str(file)
 return _download_asset(file,*args,**kwargs)
downloads.attempt_download_asset=local_or_download
def main():
 source=Path(CFG['YoloModel']);before=hashlib.sha256(source.read_bytes()).hexdigest();assert before==CFG['YoloSha256']
 model=YOLO(os.path.relpath(source));started=time.perf_counter();history=[]
 def progress(trainer):
  entry={'epoch':trainer.epoch+1,'seconds':round(time.perf_counter()-started,2),'metrics':{k:float(v) for k,v in trainer.metrics.items()}}
  history.append(entry);(OUT/'training-progress.json').write_text(json.dumps(history,indent=2),encoding='utf8');print('ITEM_PROGRESS '+json.dumps(entry),flush=True)
 model.add_callback('on_fit_epoch_end',progress)
 model.train(data=str(OUT/'dataset'/'data.yaml'),project=str(OUT/'runs'),name='items19',exist_ok=False,
  epochs=30,patience=10,imgsz=640,batch=4,device='cpu',workers=0,optimizer='AdamW',lr0=.002,lrf=.05,cos_lr=True,warmup_epochs=2,
  freeze=10,rect=False,mosaic=0,mixup=0,cutmix=0,copy_paste=0,fliplr=0,flipud=0,degrees=0,perspective=0,shear=0,
  translate=.025,scale=.15,hsv_h=0,hsv_s=.12,hsv_v=.15,plots=False,save=True,save_period=10,seed=20261024,deterministic=True,amp=False,cache=False,verbose=False)
 target=OUT/'model';target.mkdir(exist_ok=True);shutil.copy2(Path(model.trainer.save_dir)/'weights'/'best.pt',target/'items-best.pt')
 after=hashlib.sha256(source.read_bytes()).hexdigest();assert before==after
 meta={'source_model':str(source),'source_sha256':before,'original_preserved':before==after,'item_model':str(target/'items-best.pt'),'item_sha256':hashlib.sha256((target/'items-best.pt').read_bytes()).hexdigest(),'seconds':time.perf_counter()-started,'epochs_completed':min(len(history),model.trainer.epochs),'validation_callback_count':len(history),'classes':model.names,'device':'cpu','navigation_approved':False,'status':'Trained; holdout and regression evaluation required'}
 (OUT/'model-manifest.json').write_text(json.dumps(meta,ensure_ascii=False,indent=2),encoding='utf8')
if __name__=='__main__':main()
