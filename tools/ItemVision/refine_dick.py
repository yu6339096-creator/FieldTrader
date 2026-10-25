"""Focus on actual goods-ROI scales; train detection head only, preserving prior stage."""
from pathlib import Path
import json,time,shutil,hashlib
from PIL import Image,ImageEnhance
from train import TASK,YOLO
OUT=TASK/'outputs/item-yolo-v3-dick'
def main():
 archive=OUT/'round1';archive.mkdir(exist_ok=True)
 for name in ['annotations.json','dataset-summary.json','model-manifest.json','candidate-comparison.json','recognition-evaluation.json','focus-training-audit.json']:shutil.copy2(OUT/name,archive/name)
 a=json.loads((OUT/'annotations.json').read_text(encoding='utf8'));names=[c['name'] for c in a['classes']];data=OUT/'dataset-focus'
 for split in ['train','val']:
  for kind in ['images','labels']:
   (data/kind/split).mkdir(parents=True,exist_ok=True)
   for p in (OUT/'dataset'/kind/split).iterdir():
    if p.suffix in ['.png','.txt']:shutil.copy2(p,data/kind/split/p.name)
 records=list(a['records'])
 def save(key,im,boxes,parents):
  w,h=im.size;im.save(data/'images/train'/(key+'.png'));lines=[]
  for b in boxes:
   x1,y1,x2,y2=b['xyxy'];assert 0<=x1<x2<=w and 0<=y1<y2<=h
   lines.append(f'{names.index(b["class"])} {(x1+x2)/2/w:.7f} {(y1+y2)/2/h:.7f} {(x2-x1)/w:.7f} {(y2-y1)/h:.7f}')
  (data/'labels/train'/(key+'.txt')).write_text('\n'.join(lines)+'\n',encoding='utf8');records.append(dict(id=key,split='train',parents=parents,synthetic=True,width=w,height=h,boxes=boxes))
 for r in a['records']:
  if r['id'] in ['D02','D03','D04']:
   im=Image.open(OUT/'dataset/images/train'/(r['id']+'.png')).convert('RGB');w,h=im.size;crop=(int(.04*w),int(.16*h),int(.50*w),int(.915*h));tile=im.crop(crop)
   boxes=[{'class':b['class'],'xyxy':[b['xyxy'][0]-crop[0],b['xyxy'][1]-crop[1],b['xyxy'][2]-crop[0],b['xyxy'][3]-crop[1]]} for b in r['boxes']]
   for i,brightness in enumerate([.85,1,1.15]):save(f'focus-roi-{r["id"]}-{i}',ImageEnhance.Brightness(tile).enhance(brightness),boxes,r['parents'])
  if r['id'].startswith('dick-card-'):
   im=Image.open(OUT/'dataset/images/train'/(r['id']+'.png')).convert('RGB')
   for i,brightness in enumerate([.85,1.1]):save(f'focus-{r["id"]}-{i}',ImageEnhance.Brightness(im).enhance(brightness),r['boxes'],r['parents'])
 a['records']=records;a['counts']={s:{n:sum(b['class']==n for r in records if r['split']==s for b in r['boxes']) for n in names} for s in ['train','val']}
 a['limitations'].append('Additional focus-ROI/card brightness descendants remain training-only; no new independent views')
 (OUT/'annotations-focus.json').write_text(json.dumps(a,ensure_ascii=False,indent=2),encoding='utf8')
 (data/'data.yaml').write_text('path: '+data.as_posix()+'\ntrain: images/train\nval: images/val\nnames:\n'+''.join(f'  {i}: {n}\n' for i,n in enumerate(names)),encoding='utf8')
 source=OUT/'model/items22-last.pt';before=hashlib.sha256(source.read_bytes()).hexdigest();model=YOLO(str(source));history=[];started=time.perf_counter()
 def progress(trainer):
  row={'epoch':min(trainer.epoch+1,trainer.epochs),'stage':'final_best_validation' if trainer.epoch>=trainer.epochs else 'training','seconds':round(time.perf_counter()-started,2),'metrics':{k:float(v) for k,v in trainer.metrics.items()}};history.append(row)
  (OUT/'head-progress.json').write_text(json.dumps(history,indent=2),encoding='utf8');print('HEAD_FOCUS '+json.dumps(row),flush=True)
 model.add_callback('on_fit_epoch_end',progress)
 model.train(data=str(data/'data.yaml'),project=str(OUT/'runs'),name='items22-head',exist_ok=False,
  epochs=20,patience=0,imgsz=640,batch=4,device='cpu',workers=0,optimizer='AdamW',lr0=.001,lrf=.05,cos_lr=True,warmup_epochs=1,
  freeze=23,cls_pw=.5,mosaic=0,mixup=0,cutmix=0,copy_paste=0,fliplr=0,flipud=0,degrees=0,perspective=0,shear=0,
  translate=.02,scale=.10,hsv_h=0,hsv_s=.10,hsv_v=.10,plots=False,save=True,seed=20261025,deterministic=True,amp=False,cache=False,verbose=False)
 assert hashlib.sha256(source.read_bytes()).hexdigest()==before
 for kind in ['best','last']:shutil.copy2(Path(model.trainer.save_dir)/'weights'/(kind+'.pt'),OUT/'model'/f'items22-head-{kind}.pt')
 m=json.loads((OUT/'model-manifest.json').read_text(encoding='utf8'));m.update(head_epochs_completed=sum(r['stage']=='training' for r in history),head_seconds=time.perf_counter()-started,head_source_sha256=before,head_training_images=sum(r['split']=='train' for r in records),head_freeze_layers=23,status='Head refinement complete; recognition evaluation pending')
 (OUT/'model-manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf8')
if __name__=='__main__':main()
