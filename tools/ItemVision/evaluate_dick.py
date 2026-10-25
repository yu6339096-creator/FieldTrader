"""Recognition QA only: real model inference, never a contact navigation test."""
from pathlib import Path
import json,hashlib,os,sys,time
from train import TASK,YOLO,torch
from PIL import Image,ImageDraw,ImageFont
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/FieldTrader/Market'))
from item_vision import predict_items,iou
OUT=TASK/'outputs/item-yolo-v3-dick'
HEAD=(OUT/'model/items22-head-best.pt').exists()
DATA=OUT/('dataset-focus' if HEAD else 'dataset')
FOCUS=['helmet_o3','armor_defender_m4','rig_926_advanced','armor_htac_a8','armor_htac_a9']
def ground(record):
 w,h=record['width'],record['height'];result=[]
 for b in record['boxes']:
  x1,y1,x2,y2=b['xyxy'];result.append({'Label':b['class'],'Bounds':dict(X=x1/w,Y=y1/h,Width=(x2-x1)/w,Height=(y2-y1)/h)})
 return result
def table(rows,names,threshold):
 stats={n:dict(gt=0,tp=0,fp=0,fn=0) for n in names}
 for row in rows:
  gt=row['GroundTruth'];used=set()
  for g in gt:stats[g['Label']]['gt']+=1
  for p in sorted([p for p in row['Predictions'] if p['Confidence']>=threshold],key=lambda p:p['Confidence'],reverse=True):
   overlap,index=max(((iou(p['Bounds'],g['Bounds']),i) for i,g in enumerate(gt) if i not in used and p['Label']==g['Label']),default=(0,-1))
   if overlap>=.5:stats[p['Label']]['tp']+=1;used.add(index)
   else:stats[p['Label']]['fp']+=1
  for i,g in enumerate(gt):
   if i not in used:stats[g['Label']]['fn']+=1
 for m in stats.values():m.update(status='DevelopmentHoldout' if m['gt'] else 'NoHoldout',precision=m['tp']/(m['tp']+m['fp']) if m['tp']+m['fp'] else None,recall=m['tp']/m['gt'] if m['gt'] else None)
 return stats
def unique_count(rows,classes):
 count=0
 for row in rows:
  for gt in row['GroundTruth']:
   if gt['Label'] not in classes:continue
   correct=any(p['Label']==gt['Label'] and p['Confidence']>=.5 and iou(p['Bounds'],gt['Bounds'])>=.5 for p in row['Predictions'])
   conflict=any(p['Label']!=gt['Label'] and p['Confidence']>=.5 and iou(p['Bounds'],gt['Bounds'])>=.5 for p in row['Predictions'])
   count+=correct and not conflict
 return count
def infer(model,record,save=None):
 im=Image.open(DATA/'images'/record['split']/(record['id']+'.png')).convert('RGB');p,passes,ms=predict_items(model,im,True)
 row={'Image':record['id'],'Split':record['split'],'Parents':record['parents'],'GroundTruth':ground(record),'Predictions':p,'Passes':passes,'Milliseconds':ms}
 if save:
  im.thumbnail((1500,1000));d=ImageDraw.Draw(im)
  for color,items in [('#ffc940',row['GroundTruth']),('#45ff80',[t for t in p if t['Confidence']>=.5])]:
   for t in items:
    b=t['Bounds'];rect=(b['X']*im.width,b['Y']*im.height,(b['X']+b['Width'])*im.width,(b['Y']+b['Height'])*im.height);d.rectangle(rect,outline=color,width=2);d.text((rect[0],rect[1]),t['Label']+(f" {t['Confidence']:.3f}" if 'Confidence' in t else ' GT'),fill=color)
  im.save(save)
 return row
def main():
 a=json.loads((OUT/('annotations-focus.json' if HEAD else 'annotations.json')).read_text(encoding='utf8'));names=[c['name'] for c in a['classes']]
 held=[r for r in a['records'] if r['split']=='val'];focus=[r for r in a['records'] if r['id'].startswith('dick-card-')];scenes=[r for r in a['records'] if r['id'] in ['D02','D03','D04']]
 candidates=[]
 for candidate in (['head-best','head-last'] if HEAD else ['best','last']):
  path=OUT/f'model/items22-{candidate}.pt';model=YOLO(os.path.relpath(path));assert [model.names[i] for i in range(22)]==names
  dev=[infer(model,r) for r in held];cards=[infer(model,r) for r in focus];dm=table(dev,names,.5);cm=table(cards,names,.5)
  source_rows=[infer(model,r) for r in scenes];sm=table(source_rows,names,.5)
  score=sum(cm[n]['tp'] for n in FOCUS);candidates.append({'Candidate':candidate,'Sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'TrainingFocusCardTP':score,'TrainingMandatorySceneTP':sum(sm[n]['tp'] for n in FOCUS[:2]),'TrainingFocusSceneTP':sum(sm[n]['tp'] for n in FOCUS),'TrainingFocusFP':sum(sm[n]['fp']+cm[n]['fp'] for n in FOCUS),'DevelopmentTP':sum(v['tp'] for v in dm.values()),'DevelopmentFP':sum(v['fp'] for v in dm.values()),'DevelopmentTables':dm,'TrainingCardTables':cm,'TrainingSceneTables':sm})
  candidates[-1].update(TrainingUniqueFocus=unique_count(cards+source_rows,FOCUS),TrainingUniqueMandatoryScene=unique_count(source_rows,FOCUS[:2]))
 if HEAD:
  selected=max(candidates,key=lambda c:(c['TrainingUniqueMandatoryScene'],c['TrainingUniqueFocus'],-c['TrainingFocusFP'],c['DevelopmentTP']-c['DevelopmentFP']))
 else:
  best,last=candidates;selected=last if last['TrainingFocusCardTP']>best['TrainingFocusCardTP'] and last['DevelopmentTP']>=best['DevelopmentTP']-1 and last['DevelopmentFP']<=best['DevelopmentFP'] else best
 comparison={'Candidates':candidates,'Selected':selected['Candidate'],'Rule':'Head stage: prioritize unique mandatory-item training-scene matches, then unique focus card+scene matches (no competing class at IoU>=0.5 and conf>=0.5), fewer focus FP, then development TP-FP. New classes have no holdout: focus selection is explicitly resubstitution, not a generalization claim.' if HEAD else 'Prefer last only for better training-card focus detection without more development FP or more than one lost development TP; otherwise validation-selected best. Card performance is resubstitution.'}
 (OUT/'candidate-comparison.json').write_text(json.dumps(comparison,indent=2),encoding='utf8')
 chosen=OUT/f"model/items22-{selected['Candidate']}.pt";model=YOLO(os.path.relpath(chosen));review=OUT/'recognition-review';review.mkdir(exist_ok=True)
 rows=[infer(model,r,review/(r['id']+'.jpg')) for r in held];cards=[infer(model,r,review/(r['id']+'.jpg')) for r in focus];scene_rows=[infer(model,r,review/(r['id']+'.jpg')) for r in scenes]
 negatives=[infer(model,r) for r in a['records'] if r['id'].startswith('dick-negative-') or r['id']=='D01']
 (OUT/'recognition-evaluation.json').write_text(json.dumps({'Scope':'RecognitionOnly','ContactRouteTestsExecuted':False,'ModelSha256':selected['Sha256'],'Thresholds':{str(t):table(rows,names,t) for t in [.25,.5,.8,.95]},'DevelopmentRows':rows,'TrainingCards':cards,'TrainingScenes':scene_rows,'TrainingNegatives':negatives,'NewClassesIndependentHoldout':False,'Policy':'Actual YOLO full+goods+inventory ROI; per-class NMS0.5; TP requires matching class and IoU0.5; no input or navigation'},ensure_ascii=False,indent=2),encoding='utf8')
 m=json.loads((OUT/'model-manifest.json').read_text(encoding='utf8'));m.update(item_model=str(chosen),item_sha256=selected['Sha256'],selected_candidate=selected['Candidate'],status='Recognition-evaluated experimental model; no contact route testing or runtime activation')
 (OUT/'model-manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf8');print(json.dumps({'selected':selected['Candidate'],'candidate_summary':[{k:v for k,v in c.items() if not k.endswith('Tables')} for c in candidates]}))
if __name__=='__main__':main()
