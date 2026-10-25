"""22-class training increment only. No capture, navigation, refresh or transactions."""
from pathlib import Path
import sys,json,time,hashlib,shutil
from train import TASK,CFG,YOLO,torch
OUT=TASK/'outputs/item-yolo-v3-dick';SOURCE=TASK/'outputs/item-yolo-v2/model/items-best.pt'
def main():
 before=hashlib.sha256(SOURCE.read_bytes()).hexdigest();ui=hashlib.sha256(Path(CFG['YoloModel']).read_bytes()).hexdigest();assert ui==CFG['YoloSha256']
 model=YOLO(str(SOURCE));old_names=[model.names[i] for i in range(19)]
 old_head={k:v.detach().cpu().clone() for k,v in model.model.state_dict().items() if '.cv3.' in k and v.ndim and v.shape[0]==19}
 assert len(old_head)==6
 history=[];started=time.perf_counter()
 def verify_transfer(trainer):
  assert [trainer.model.names[i] for i in range(19)]==old_names
  state=trainer.model.state_dict();checks={k:torch.equal(state[k][:19].detach().cpu(),v) for k,v in old_head.items()}
  audit={'Passed':all(checks.values()),'OriginalClassesRetained':19,'TargetClasses':22,'ExactHeadTensorChecks':checks,'Method':'Installed Ultralytics cls_remap by exact class name; checked before first optimizer step'}
  (OUT/'class-transfer-audit.json').write_text(json.dumps(audit,indent=2),encoding='utf8');assert audit['Passed'],audit
 def progress(trainer):
  final=trainer.epoch>=trainer.epochs
  row={'epoch':min(trainer.epoch+1,trainer.epochs),'stage':'final_best_validation' if final else 'training','seconds':round(time.perf_counter()-started,2),'metrics':{k:float(v) for k,v in trainer.metrics.items()}}
  history.append(row);(OUT/'training-progress.json').write_text(json.dumps(history,indent=2),encoding='utf8');print('DICK_ITEM_TRAIN '+json.dumps(row),flush=True)
 model.add_callback('on_pretrain_routine_end',verify_transfer);model.add_callback('on_fit_epoch_end',progress)
 model.train(data=str(OUT/'dataset/data.yaml'),project=str(OUT/'runs'),name='items22',exist_ok=False,
  epochs=24,patience=0,imgsz=640,batch=4,device='cpu',workers=0,optimizer='AdamW',lr0=.001,lrf=.05,cos_lr=True,warmup_epochs=2,
  freeze=10,cls_remap=True,cls_pw=.5,mosaic=0,mixup=0,cutmix=0,copy_paste=0,fliplr=0,flipud=0,degrees=0,perspective=0,shear=0,
  translate=.025,scale=.15,hsv_h=0,hsv_s=.10,hsv_v=.15,plots=False,save=True,seed=20261025,deterministic=True,amp=False,cache=False,verbose=False)
 target=OUT/'model';target.mkdir(exist_ok=True);shutil.copy2(Path(model.trainer.save_dir)/'weights/best.pt',target/'items22-best.pt');shutil.copy2(Path(model.trainer.save_dir)/'weights/last.pt',target/'items22-last.pt')
 assert hashlib.sha256(SOURCE.read_bytes()).hexdigest()==before and hashlib.sha256(Path(CFG['YoloModel']).read_bytes()).hexdigest()==ui
 m={'source_model':str(SOURCE),'source_sha256':before,'original_item_model_preserved':True,'ui_model_sha256':ui,'ui_model_preserved':True,'classes':model.names,'epochs_completed':sum(r['stage']=='training' for r in history),'seconds':time.perf_counter()-started,'device':'cpu','item_model':str(target/'items22-best.pt'),'item_sha256':hashlib.sha256((target/'items22-best.pt').read_bytes()).hexdigest(),'status':'Trained; recognition evaluation pending','scope':'TrainingOnly','ContactRouteTestsExecuted':False,'RefreshActionsExecuted':0,'GameInputsExecuted':0}
 (OUT/'model-manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf8')
if __name__=='__main__':main()
