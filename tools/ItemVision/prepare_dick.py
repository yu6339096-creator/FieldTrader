"""Training-only increment: source screenshots, cards, artwork, hard negatives and rehearsal."""
from pathlib import Path
import json,shutil,hashlib,random,collections
from PIL import Image,ImageDraw,ImageFont,ImageEnhance
TASK=Path(r"C:/Users/yu'ch'yi/Documents/Codex/2026-10-24/referenced-chatgpt-conversation-this-is-an-2")
OLD=TASK/'outputs/item-yolo-v2';OUT=TASK/'outputs/item-yolo-v3-dick';DATA=OUT/'dataset'
def main():
 a=json.loads((OLD/'annotations.json').read_text(encoding='utf8'));classes=a['classes']+[
  {'id':19,'name':'rig_926_advanced','display':'926高级野战胸挂'},
  {'id':20,'name':'armor_htac_a8','display':'H-Tac A8弹挂甲'},
  {'id':21,'name':'armor_htac_a9','display':'H-Tac A9弹挂甲'}]
 names=[c['name'] for c in classes];records=list(a['records']);sources=list(a['sources']);seeds=[];seed_sources=[]
 for split in ['train','val']:
  for kind in ['images','labels']:
   folder=DATA/kind/split;folder.mkdir(parents=True,exist_ok=True)
   for p in (OLD/'dataset'/kind/split).iterdir():
    if p.is_file() and p.suffix in ['.png','.txt']:shutil.copy2(p,folder/p.name)
 (OUT/'originals').mkdir(exist_ok=True);(OUT/'annotation-review').mkdir(exist_ok=True)
 ids=['a0def456-f529-4ac2-9567-3d7193ae8c19','3e31fa22-8284-4d7f-bffe-1980978c81b4','5f41230c-dbf2-4d05-a8f3-4b2fbaa7b735','12f1edc6-4d65-426b-8816-5c98e619069c']
 images={}
 for i,uid in enumerate(ids,1):
  p=OUT/'originals'/f'D{i:02}.png';shutil.copy2(Path(r"C:/Users/yu'ch'yi/AppData/Local/Temp")/f'codex-clipboard-{uid}.png',p)
  images[f'D{i:02}']=Image.open(p).convert('RGB');sources.append({'id':f'D{i:02}','path':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()})
 def save(key,im,boxes,parents,synthetic=False):
  w,h=im.size;lines=[];valid=[]
  for cls,(x1,y1,x2,y2) in boxes:
   assert 0<=x1<x2<=w and 0<=y1<y2<=h,(key,cls,(x1,y1,x2,y2),im.size)
   lines.append(f'{names.index(cls)} {(x1+x2)/2/w:.7f} {(y1+y2)/2/h:.7f} {(x2-x1)/w:.7f} {(y2-y1)/h:.7f}');valid.append({'class':cls,'xyxy':[x1,y1,x2,y2]})
  im.save(DATA/'images/train'/(key+'.png'));(DATA/'labels/train'/(key+'.txt')).write_text('\n'.join(lines)+('\n' if lines else ''),encoding='utf8')
  records.append({'id':key,'split':'train','parents':parents,'synthetic':synthetic,'width':w,'height':h,'boxes':valid})
  preview=im.copy();preview.thumbnail((1200,900));d=ImageDraw.Draw(preview)
  for cls,b in boxes:
   q=(b[0]*preview.width/w,b[1]*preview.height/h,b[2]*preview.width/w,b[3]*preview.height/h);d.rectangle(q,outline='#ffcf40',width=2);d.text((q[0],q[1]),cls,fill='#ffcf40')
  preview.save(OUT/'annotation-review'/(key+'.jpg'))
 def scaled(src,b):
  w,h=images[src].size;return tuple(v*(w/2048 if i%2==0 else h/1152) for i,v in enumerate(b))
 specs=[('D02','helmet_sh12',(360,254,485,382),(315,188,529,463)),
        ('D03','helmet_o3',(802,834,928,970),(755,766,970,1049)),
        ('D04','armor_defender_m4',(145,531,260,705),(96,479,312,760)),
        ('D04','rig_926_advanced',(144,260,260,414),(96,198,312,478)),
        ('D04','armor_htac_a8',(580,270,703,398),(535,198,749,478)),
        ('D04','armor_htac_a9',(804,257,924,415),(755,198,970,478))]
 for src in images:save(src,images[src],[(cls,scaled(src,b)) for s,cls,b,card in specs if s==src],[src])
 for i,(src,cls,b,card) in enumerate(specs):
  art=scaled(src,b);crop=tuple(round(v) for v in scaled(src,card));tile=images[src].crop(crop)
  save(f'dick-card-{i:02}',tile,[(cls,(art[0]-crop[0],art[1]-crop[1],art[2]-crop[0],art[3]-crop[1]))],[src])
  c=(max(0,int(art[0])-10),max(0,int(art[1])-10),min(images[src].width,int(art[2])+10),min(images[src].height,int(art[3])+10));tile=images[src].crop(c);box=(art[0]-c[0],art[1]-c[1],art[2]-c[0],art[3]-c[1])
  save(f'dick-seed-{i:02}',tile,[(cls,box)],[src]);seeds.append((tile,cls,box,src));seed_sources.append({'id':f'dick-seed-{i:02}','parent':src,'source_artwork_xyxy':art,'source_crop_xyxy':c})
 for i,(src,b) in enumerate([('D03',(140,560,271,694)),('D03',(580,835,710,969)),('D02',(799,261,921,380)),('D04',(355,264,484,402))]):save(f'dick-negative-{i:02}',images[src].crop(tuple(round(v) for v in scaled(src,b))),[],[src])
 rng=random.Random(20261025)
 # Exact source descendants stay in train; realistic artwork sizes, no flips or imagined views.
 for i in range(12):
  canvas=Image.new('RGB',(640,640),(20,25,24));boxes=[];parents=[]
  for j in range(4):
   tile,cls,b,parent=seeds[(i*4+j)%len(seeds)];scale=rng.randint(110,210)/max(tile.size);size=(round(tile.width*scale),round(tile.height*scale));patch=tile.resize(size,Image.Resampling.LANCZOS)
   patch=ImageEnhance.Brightness(patch).enhance(rng.uniform(.75,1.15));x=(j%2)*320+rng.randrange(15,90);y=(j//2)*320+rng.randrange(15,90);canvas.paste(patch,(x,y));boxes.append((cls,(x+b[0]*scale,y+b[1]*scale,x+b[2]*scale,y+b[3]*scale)));parents.append(parent)
  save(f'dick-composition-{i:02}',canvas,boxes,sorted(set(parents)),True)
 train_parents={p for r in records if r['split']=='train' for p in r['parents']};val_parents={p for r in records if r['split']=='val' for p in r['parents']};assert not train_parents&val_parents
 counts={s:{n:sum(b['class']==n for r in records if r['split']==s for b in r['boxes']) for n in names} for s in ['train','val']}
 manifest={'classes':classes,'records':records,'sources':sources,'new_seed_sources':seed_sources,'counts':counts,'train_parents':sorted(train_parents),'val_parents':sorted(val_parents),'split_unit':'Original screenshot; all descendants remain in same split','limitations':a['limitations']+['New 926/A8/A9 share a single source D04; no independent holdout for new classes','D01 is a contact-screen negative, not an exchange or refresh screenshot','No contact route, input or exchange testing in this phase']}
 (OUT/'annotations.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf8')
 (DATA/'data.yaml').write_text('path: '+DATA.as_posix()+'\ntrain: images/train\nval: images/val\nnames:\n'+''.join(f'  {i}: {n}\n' for i,n in enumerate(names)),encoding='utf8')
 summary={'classes':len(names),'train_images':sum(r['split']=='train' for r in records),'validation_images':sum(r['split']=='val' for r in records),'new_records':len(records)-len(a['records']),'counts':counts,'limitations':manifest['limitations']}
 (OUT/'dataset-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf8');print(json.dumps({k:v for k,v in summary.items() if k not in ['counts','limitations']}))
if __name__=='__main__':main()
