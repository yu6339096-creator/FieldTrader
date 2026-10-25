"""Build auditable item-artwork labels; originals are immutable, augmentations train-only."""
from pathlib import Path
import json,hashlib,shutil,random,sys
from PIL import Image,ImageDraw,ImageEnhance
sys.stdout.reconfigure(encoding='utf-8')
TASK=Path(r"C:\Users\yu'ch'yi\Documents\Codex\2026-10-24\referenced-chatgpt-conversation-this-is-an-2")
OUT=TASK/'outputs'/'item-yolo-v2';DATA=OUT/'dataset'
NAMES=['helmet_sh12','helmet_o3','armor_defender_m4','ammo_545_bs','ammo_556_m995','ammo_58_dvc12','ammo_76239_ap','ammo_76251_m62','ammo_939_7n12','ammo_338_ap','med_100d','med_sto','med_tmk','surgery_sto','painkiller_special','grenade_mkii','grenade_gho2','quantity_max','ammo_round']
DISPLAY=['SH12军用头盔','03重型战术头盔','防卫者M4重装弹挂甲','5.45×39毫米 BS子弹','5.56×45毫米 M995子弹','5.8×42毫米 DVC12子弹','7.62×39毫米 AP子弹','7.62×51毫米 M62子弹','9×39毫米 7N12子弹','.338 AP','100D战地医疗包','STO急救套装','TMK野战医疗包','STO战地手术套装','特效止痛药','MK II破片手榴弹','GHO 2进攻手榴弹','MAX','散装弹头（弹种须OCR确认）']
IDS=['30795a5e-0029-4abf-80c0-473ebae7d088','d771527d-829f-4de4-98c5-11612ace928a','94b11f5a-8abf-40ac-a181-dc6b1166c79b','c3bf2f90-fab5-495a-8ae9-2fb768bda0c2','03b10a75-bc57-4ff7-b731-9c82164cc997','a4fdf9f4-746f-45d2-a1c3-996c2f073966','1b5051bf-f246-402f-a66b-f46bbd522e00','a12a4f1f-e91d-4282-8d9c-824f7c7ad811','40196a6b-9601-4571-9ed2-783a1cbb3328','c8e5db23-0476-4c93-999f-f762f459cb8c','65ccb2fa-25bf-4575-b28f-5495d4ceaaeb','ec1acf57-c471-4b6e-8c5b-b7670e43dd9b','08b9b9e2-9f27-4f07-96f8-a6195ea56c30','d34a83e9-0616-4cd7-85cb-daf257473b9a','747a9268-962b-4ef7-ad4f-ade0afc05727','c76f6432-4fac-43bc-b825-74893683f4a7']
for split in ['train','val']:
 for kind in ['images','labels']:(DATA/kind/split).mkdir(parents=True,exist_ok=True)
(OUT/'originals').mkdir(exist_ok=True);(OUT/'annotation-review').mkdir(exist_ok=True)
for i,uuid in enumerate(IDS,1):shutil.copy2(Path(r"C:\Users\yu'ch'yi\AppData\Local\Temp")/f'codex-clipboard-{uuid}.png',OUT/'originals'/f'N{i:02}.png')
sources={f'N{i:02}':OUT/'originals'/f'N{i:02}.png' for i in range(1,17)}
for n in [4,6,8,10,11,13,14,15,16,17,18]:sources[f'S{n:02}']=TASK/'work'/'larry-images'/f'S{n:02}.png'
for name in ['b06_09','b11_04','b03_05','b10_07']:sources[name]=TASK/'work'/'visual-navigation-images'/f'{name}.png'
records=[];seeds=[]
def image(name):return Image.open(sources[name]).convert('RGB')
def boxes_display(name,boxes):
 im=image(name);w,h=im.size
 return [(cls,(x1*w/2048,y1*h/1152,x2*w/2048,y2*h/1152)) for cls,(x1,y1,x2,y2) in boxes]
def save(name,im,boxes,split,parents,synthetic=False):
 im.save(DATA/'images'/split/(name+'.png'))
 w,h=im.size;labels=[];valid=[]
 for cls,(x1,y1,x2,y2) in boxes:
  x1=max(0,x1);y1=max(0,y1);x2=min(w,x2);y2=min(h,y2)
  assert x2>x1 and y2>y1,(name,cls,(x1,y1,x2,y2))
  labels.append(f'{NAMES.index(cls)} {(x1+x2)/2/w:.7f} {(y1+y2)/2/h:.7f} {(x2-x1)/w:.7f} {(y2-y1)/h:.7f}')
  valid.append({'class':cls,'xyxy':[x1,y1,x2,y2]})
 (DATA/'labels'/split/(name+'.txt')).write_text('\n'.join(labels)+'\n',encoding='utf8')
 records.append({'id':name,'split':split,'parents':parents,'synthetic':synthetic,'width':w,'height':h,'boxes':valid})
 preview=im.copy();preview.thumbnail((1100,800));d=ImageDraw.Draw(preview);sx=preview.width/w;sy=preview.height/h
 for cls,(x1,y1,x2,y2) in boxes:d.rectangle((x1*sx,y1*sy,x2*sx,y2*sy),outline='#ffcc32',width=2);d.text((x1*sx,y1*sy),cls,fill='#ffcc32')
 preview.save(OUT/'annotation-review'/(name+'.jpg'))
def scene(name,boxes,split,display=True):
 im=image(name);boxes=boxes_display(name,boxes) if display else boxes
 save(name,im,boxes,split,[name]);return boxes
def seed(name,cls,b,display=False):
 im=image(name)
 if display:
  w,h=im.size;b=(b[0]*w/2048,b[1]*h/1152,b[2]*w/2048,b[3]*h/1152)
 x1,y1,x2,y2=b;pad=max(4,int((x2-x1)*.08));crop=(max(0,int(x1)-pad),max(0,int(y1)-pad),min(im.width,int(x2)+pad),min(im.height,int(y2)+pad))
 tile=im.crop(crop);box=(x1-crop[0],y1-crop[1],x2-crop[0],y2-crop[1]);seeds.append((tile,cls,box,name))

# Actual artwork regions: exclude names, prices, durability bars and favorite icons.
for n,cls,b in [(1,'helmet_sh12',(48,77,218,264)),(2,'helmet_o3',(62,70,210,246)),(3,'armor_defender_m4',(73,65,206,270)),(4,'ammo_545_bs',(83,111,186,214)),(7,'ammo_556_m995',(84,112,185,207)),(10,'ammo_76239_ap',(79,116,190,212)),(12,'ammo_76251_m62',(79,110,190,207)),(14,'ammo_939_7n12',(82,116,188,213))]:seed(f'N{n:02}',cls,b)
for cls,b in [('ammo_58_dvc12',(7,23,82,100)),('ammo_556_m995',(91,24,167,100)),('ammo_76239_ap',(177,23,252,100)),('ammo_338_ap',(264,22,340,101))]:seed('N16',cls,b)
seed('N05','quantity_max',(1,1,72,41))
shop=[('med_100d',(812,283,914,354)),('med_sto',(145,555,252,650)),('med_tmk',(368,542,470,658))]
scene('S04',shop,'train')
for cls,b in shop:seed('S04',cls,b,True)
scene('S11',[('surgery_sto',(810,824,920,932))],'train');seed('S11','surgery_sto',(810,824,920,932),True)
seed('S14','painkiller_special',(100,100,167,218))
old=[('grenade_mkii',(839,550,904,650)),('grenade_gho2',(603,558,673,647)),('med_100d',(1274,622,1391,678)),('med_tmk',(1414,624,1507,744)),('surgery_sto',(1548,631,1669,737)),('painkiller_special',(1485,306,1519,351))]
scene('b06_09',old,'train')
for cls,b in old:seed('b06_09',cls,b,True)
# Training modal ammunition: learn a generic round, never guess caliber from similar brass artwork.
for name,roundbox,maxbox in [('N06',(707,348,806,483),(1545,743,1605,777)),('N11',(364,164,478,323),(1410,653,1483,695))]:
 im=image(name)
 if name=='N06':
  bb=boxes_display(name,[('ammo_round',roundbox),('quantity_max',maxbox),('ammo_545_bs',(1618,276,1682,364))]);scene(name,bb,'train',False)
 else:bb=[('ammo_round',roundbox),('quantity_max',maxbox)];scene(name,bb,'train',False)
 for cls,b in bb:seed(name,cls,b)

# Validation sources and all their descendants are held out from training.
scene('S16',[('grenade_mkii',(839,550,904,650)),('grenade_gho2',(603,558,673,647))],'val')
for name,cls,b in [('S06','med_100d',(311,141,584,317)),('S08','med_sto',(309,135,565,327)),('S10','med_tmk',(557,195,704,366)),('S13','surgery_sto',(310,133,562,330)),('S15','painkiller_special',(370,123,502,326))]:
 im=image(name);boxes=boxes_display(name,[(cls,b)]) if name=='S10' else [(cls,b)]
 # Isolate the left detail panel; unrelated dark background cards are outside this ROI.
 cut=im.crop((0,0,int(im.width*.47),int(im.height*.50))) if name!='S10' else im.crop((int(im.width*.137),int(im.height*.086),int(im.width*.477),int(im.height*.362)))
 x0=0 if name!='S10' else int(im.width*.137);y0=0 if name!='S10' else int(im.height*.086)
 save(name+'-detail',cut,[(c,(a-x0,b-y0,d-x0,e-y0)) for c,(a,b,d,e) in boxes],'val',[name])
for name in ['N08','N09','N13','N15']:
 im=image(name);w,h=im.size
 # Detail + controls crop excludes same-item boxed background reference cards.
 crop=(int(.204*w),int(.195*h),int(.797*w),int(.81*h));cut=im.crop(crop)
 raw=boxes_display(name,[('ammo_round',(710,348,805,481)),('quantity_max',(1545,743,1605,777))])
 save(name+'-modal',cut,[(c,(a-crop[0],b-crop[1],d-crop[0],e-crop[1])) for c,(a,b,d,e) in raw],'val',[name])
# Different legacy frames supply helmet/armor/MKII validation, not train copies.
im=image('b03_05');w,h=im.size;cut=im.crop((int(530*w/2048),int(190*h/1152),int(720*w/2048),int(375*h/1152)))
save('old-o3-detail',cut,[('helmet_o3',(28*w/2048,15*h/1152,175*w/2048,173*h/1152))],'val',['b03_05'])
im=image('b10_07');w,h=im.size;cut=im.crop((int(122*w/2048),int(323*h/1152),int(259*w/2048),int(454*h/1152)))
save('old-sh12-listed',cut,[('helmet_sh12',(12*w/2048,16*h/1152,126*w/2048,124*h/1152))],'val',['b10_07'])
im=image('b11_04');w,h=im.size;cut=im.crop((int(1198*w/2048),int(148*h/1152),int(1598*w/2048),int(352*h/1152)))
save('old-m4-inventory',cut,[('armor_defender_m4',(27*w/2048,0,167*w/2048,190*h/1152)),('armor_defender_m4',(230*w/2048,0,365*w/2048,190*h/1152))],'val',['b11_04'])

# Every synthetic training composition has explicit parent provenance. None is validation.
rng=random.Random(20261024)
for i in range(48):
 canvas=Image.new('RGB',(640,640),(rng.randrange(7,35),rng.randrange(9,36),rng.randrange(10,38)));boxes=[];parents=[]
 selected=rng.sample(seeds,4)
 for j,(tile,cls,b,parent) in enumerate(selected):
  maxw=rng.randint(95,220);maxh=rng.randint(100,230);scale=min(maxw/tile.width,maxh/tile.height)
  patch=tile.resize((max(2,round(tile.width*scale)),max(2,round(tile.height*scale))),Image.Resampling.LANCZOS)
  patch=ImageEnhance.Brightness(patch).enhance(rng.uniform(.55,1.25));patch=ImageEnhance.Contrast(patch).enhance(rng.uniform(.85,1.1))
  x=(j%2)*320+rng.randrange(10,max(11,300-patch.width));y=(j//2)*320+rng.randrange(10,max(11,300-patch.height));canvas.paste(patch,(x,y))
  boxes.append((cls,(x+b[0]*scale,y+b[1]*scale,x+b[2]*scale,y+b[3]*scale)));parents.append(parent)
 save(f'composition-{i:03}',canvas,boxes,'train',sorted(set(parents)),True)
for i,(tile,cls,b,parent) in enumerate(seeds):save(f'seed-{i:03}',tile,[(cls,b)],'train',[parent])
train_parents={p for r in records if r['split']=='train' for p in r['parents']};val_parents={p for r in records if r['split']=='val' for p in r['parents']}
assert not train_parents&val_parents,train_parents&val_parents
assert set(c for r in records if r['split']=='train' for c in [b['class'] for b in r['boxes']])==set(NAMES)
yaml='path: '+DATA.as_posix()+'\ntrain: images/train\nval: images/val\nnames:\n'+''.join(f'  {i}: {name}\n' for i,name in enumerate(NAMES))
(DATA/'data.yaml').write_text(yaml,encoding='utf8')
counts={s:{c:sum(b['class']==c for r in records if r['split']==s for b in r['boxes']) for c in NAMES} for s in ['train','val']}
manifest={'classes':[{'id':i,'name':n,'display':DISPLAY[i]} for i,n in enumerate(NAMES)],'records':records,'counts':counts,'train_parents':sorted(train_parents),'val_parents':sorted(val_parents),'split_unit':'source screenshot; no descendant/augmentation crosses split; related same-session assets remain development-only','limitations':['Only one original warehouse view for DVC12/.338 AP: train-only, no independent validation for these classes','Boxed ammo identity is specific; loose round is generic and requires OCR caliber confirmation','Single-seed classes are experimental; synthetic compositions are not independent evidence'],'sources':[{'id':k,'path':str(v),'sha256':hashlib.sha256(v.read_bytes()).hexdigest()} for k,v in sources.items()]}
(OUT/'annotations.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf8')
(OUT/'dataset-summary.json').write_text(json.dumps({'train_images':sum(r['split']=='train' for r in records),'val_images':sum(r['split']=='val' for r in records),'counts':counts,'limitations':manifest['limitations']},ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps({'train':sum(r['split']=='train' for r in records),'val':sum(r['split']=='val' for r in records),'seeds':len(seeds),'classes':len(NAMES)},ensure_ascii=False))
