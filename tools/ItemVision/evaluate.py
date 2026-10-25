from pathlib import Path
import sys,os,json,hashlib,time,collections
sys.stdout.reconfigure(encoding='utf-8')
TASK=Path(r"C:\Users\yu'ch'yi\Documents\Codex\2026-10-24\referenced-chatgpt-conversation-this-is-an-2")
OUT=TASK/'outputs'/'item-yolo-v2';REPO=TASK.parent/'referenced-chatgpt-conversation-this-is-an'/'work'/'FieldTrader'
cfg=json.loads((REPO.parent/'vision.local.json').read_text(encoding='utf-8-sig'));sys.path[:0]=cfg['DependencyDirectories'];sys.path.insert(0,str(REPO/'src'/'FieldTrader'/'Market'))
os.environ['YOLO_CONFIG_DIR']=str(TASK/'work'/'item-yolo-cache');os.environ['YOLO_AUTOINSTALL']='false'
import torch
torch.set_num_threads(2);torch.set_num_interop_threads(1)
from ultralytics import YOLO
from PIL import Image,ImageDraw
from item_vision import predict_items,iou

def main():
 manifest=json.loads((OUT/'annotations.json').read_text(encoding='utf8'));names=[c['name'] for c in manifest['classes']]
 model=YOLO(os.path.relpath(OUT/'model'/'items-best.pt'));assert [model.names[i] for i in range(len(model.names))]==names
 (OUT/'evaluation').mkdir(exist_ok=True);rows=[]
 for record in manifest['records']:
  if record['split']!='val':continue
  im=Image.open(OUT/'dataset'/'images'/'val'/(record['id']+'.png')).convert('RGB');w,h=im.size
  pred,passes,ms=predict_items(model,im,True)
  gt=[{'Label':b['class'],'Bounds':{'X':b['xyxy'][0]/w,'Y':b['xyxy'][1]/h,'Width':(b['xyxy'][2]-b['xyxy'][0])/w,'Height':(b['xyxy'][3]-b['xyxy'][1])/h}} for b in record['boxes']]
  rows.append({'Image':record['id'],'Parents':record['parents'],'GroundTruth':gt,'Predictions':pred,'Passes':passes,'Milliseconds':ms})
  canvas=im.copy();canvas.thumbnail((1600,1000));d=ImageDraw.Draw(canvas)
  for color,objects in [('#ffcc33',gt),('#43ff78',[p for p in pred if p['Confidence']>=.5])]:
   for p in objects:
    b=p['Bounds'];r=(b['X']*canvas.width,b['Y']*canvas.height,(b['X']+b['Width'])*canvas.width,(b['Y']+b['Height'])*canvas.height)
    d.rectangle(r,outline=color,width=2);d.text((r[0],r[1]),p['Label']+(f" {p['Confidence']:.3f}" if 'Confidence' in p else ' GT'),fill=color)
  canvas.save(OUT/'evaluation'/(record['id']+'.jpg'))
 tables={}
 for threshold in [.25,.5,.8,.9,.95]:
  metrics={n:{'gt':0,'tp':0,'fp':0,'fn':0} for n in names}
  for row in rows:
   for gt in row['GroundTruth']:metrics[gt['Label']]['gt']+=1
   used=set()
   for p in sorted([p for p in row['Predictions'] if p['Confidence']>=threshold],key=lambda p:p['Confidence'],reverse=True):
    matches=[(iou(p['Bounds'],g['Bounds']),i) for i,g in enumerate(row['GroundTruth']) if i not in used and g['Label']==p['Label']]
    overlap,index=max(matches,default=(0,-1))
    if overlap>=.5:metrics[p['Label']]['tp']+=1;used.add(index)
    else:metrics[p['Label']]['fp']+=1
   for i,g in enumerate(row['GroundTruth']):
    if i not in used:metrics[g['Label']]['fn']+=1
  for m in metrics.values():m.update(precision=m['tp']/(m['tp']+m['fp']) if m['tp']+m['fp'] else None,recall=m['tp']/m['gt'] if m['gt'] else None,status='DevelopmentHoldout' if m['gt'] else 'NoHoldout')
  tables[str(threshold)]=metrics
 approved=[n for n,m in tables['0.95'].items() if m['gt']>0 and m['tp']==m['gt'] and m['fp']==0]
 # Diagnostic resubstitution audit of the four warehouse examples; never counted as holdout.
 stock=Image.open(OUT/'originals'/'N16.png').convert('RGB');stock_pred,stock_passes,stock_ms=predict_items(model,stock,False)
 stock_canvas=stock.resize((stock.width*4,stock.height*4));stock_draw=ImageDraw.Draw(stock_canvas)
 for p in stock_pred:
  b=p['Bounds'];r=(b['X']*stock_canvas.width,b['Y']*stock_canvas.height,(b['X']+b['Width'])*stock_canvas.width,(b['Y']+b['Height'])*stock_canvas.height)
  stock_draw.rectangle(r,outline='#43ff78',width=2);stock_draw.text((r[0],r[1]),f"{p['Label']} {p['Confidence']:.3f}",fill='#43ff78')
 stock_canvas.save(OUT/'warehouse-training-audit.jpg')
 seed_audit=[]
 for record in manifest['records']:
  if not record['id'].startswith('seed-'):continue
  im=Image.open(OUT/'dataset'/'images'/'train'/(record['id']+'.png')).convert('RGB');p,_,_=predict_items(model,im,False)
  cls=record['boxes'][0]['class'];seed_audit.append({'Image':record['id'],'Class':cls,'CorrectClassBestConfidence':max([t['Confidence'] for t in p if t['Label']==cls],default=0),'Predictions':p})
 sha=hashlib.sha256((OUT/'model'/'items-best.pt').read_bytes()).hexdigest()
 result={'ModelSha256':sha,'Policy':'Actual YOLO11m19; full+goods+inventory ROI; normalized original coordinates; per-class NMS 0.5; TP IoU0.5','Thresholds':tables,'LocalNavigationClasses':approved,'Scope':'LocalTestOnly','IndependentGameSessionsTested':False,'Rows':rows,'TrainingSeedAudit':seed_audit,'WarehouseTrainingViewAudit':{'Source':'N16 already used in training; not independent validation','Predictions':stock_pred,'Passes':stock_passes,'Milliseconds':stock_ms}}
 (OUT/'evaluation.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf8')
 cfg.update(ItemYoloModel=str(OUT/'model'/'items-best.pt'),ItemYoloSha256=sha,ItemClasses=names,ItemNavigationClasses=approved,ItemRois=True,CacheDirectory=str(TASK/'work'/'item-vision-cache'))
 (OUT/'vision-items.local.json').write_text(json.dumps(cfg,ensure_ascii=False,indent=2),encoding='utf8')
 model_manifest=json.loads((OUT/'model-manifest.json').read_text(encoding='utf8'));model_manifest.update(local_navigation_classes=approved,status='Development-evaluated; game navigation not authorized; no-holdout classes remain experimental')
 (OUT/'model-manifest.json').write_text(json.dumps(model_manifest,ensure_ascii=False,indent=2),encoding='utf8')
 print(json.dumps({'local_navigation_classes':approved,'heldout_095':tables['0.95'],'warehouse_training_predictions':stock_pred},ensure_ascii=False,indent=2))
if __name__=='__main__':main()
