"""Second optimization stage. Retains the first round and trains all layers."""
from pathlib import Path
import json,time,hashlib,shutil
from train import TASK,OUT,CFG,YOLO

def main():
 archive=OUT/'round1';archive.mkdir(exist_ok=True)
 for name in ['evaluation.json','evaluate.log','model-manifest.json','vision-items.local.json']:
  shutil.copy2(OUT/name,archive/name)
 shutil.copy2(OUT/'model/items-best.pt',archive/'items-best.pt')
 model=YOLO(str(archive/'items-best.pt'));started=time.perf_counter();history=[]
 def progress(trainer):
  row={'epoch':trainer.epoch+1,'seconds':round(time.perf_counter()-started,2),'metrics':{k:float(v) for k,v in trainer.metrics.items()}}
  history.append(row);(OUT/'refine-progress.json').write_text(json.dumps(history,indent=2),encoding='utf8');print('ITEM_REFINE '+json.dumps(row),flush=True)
 model.add_callback('on_fit_epoch_end',progress)
 model.train(data=str(OUT/'dataset/data.yaml'),project=str(OUT/'runs'),name='items19-refine',exist_ok=False,
  epochs=40,patience=15,imgsz=640,batch=4,device='cpu',workers=0,optimizer='AdamW',lr0=.001,lrf=.05,cos_lr=True,warmup_epochs=2,
  freeze=0,mosaic=0,mixup=0,cutmix=0,copy_paste=0,fliplr=0,flipud=0,degrees=0,perspective=0,shear=0,
  translate=.025,scale=.15,hsv_h=0,hsv_s=.12,hsv_v=.15,plots=False,save=True,seed=20261024,deterministic=True,amp=False,cache=False,verbose=False)
 target=OUT/'model/items-best.pt';shutil.copy2(Path(model.trainer.save_dir)/'weights/best.pt',target)
 meta=json.loads((archive/'model-manifest.json').read_text(encoding='utf8'))
 assert hashlib.sha256(Path(meta['source_model']).read_bytes()).hexdigest()==meta['source_sha256']
 meta.update(item_sha256=hashlib.sha256(target.read_bytes()).hexdigest(),refine_epochs_completed=min(len(history),model.trainer.epochs),refine_validation_callback_count=len(history),refine_seconds=time.perf_counter()-started,refine_base_sha256=hashlib.sha256((archive/'items-best.pt').read_bytes()).hexdigest(),status='Refined all layers; evaluation pending')
 (OUT/'model-manifest.json').write_text(json.dumps(meta,ensure_ascii=False,indent=2),encoding='utf8')
if __name__=='__main__':main()
