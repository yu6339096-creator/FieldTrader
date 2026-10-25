"""Shared runtime/evaluation item policy. All boxes return to original client pixels."""
import time,contextlib,sys

def iou(a,b):
    x=max(a['X'],b['X']);y=max(a['Y'],b['Y'])
    w=max(0,min(a['X']+a['Width'],b['X']+b['Width'])-x)
    h=max(0,min(a['Y']+a['Height'],b['Y']+b['Height'])-y)
    overlap=w*h
    return overlap/(a['Width']*a['Height']+b['Width']*b['Height']-overlap) if overlap else 0

def predict_items(model,pixels,roi=True):
    width,height=pixels.size;started=time.perf_counter()
    views=[('full',(0,0,width,height))]
    if roi and width>=1000 and height>=600 and width/height>=1.5:
        views.extend([('goods',(int(.04*width),int(.16*height),int(.50*width),int(.915*height))),
                      ('inventory',(int(.58*width),int(.13*height),int(.92*width),int(.90*height)))])
    passes=[];proposals=[]
    for name,(left,top,right,bottom) in views:
        clock=time.perf_counter()
        with contextlib.redirect_stdout(sys.stderr):
            result=model.predict(pixels.crop((left,top,right,bottom)),imgsz=640,conf=.25,device='cpu',verbose=False)[0]
        items=[]
        for box,confidence,category in zip(result.boxes.xyxy.tolist(),result.boxes.conf.tolist(),result.boxes.cls.tolist()):
            x1,y1,x2,y2=box
            b={'X':max(0,left+x1)/width,'Y':max(0,top+y1)/height,
               'Width':(min(width,left+x2)-max(0,left+x1))/width,'Height':(min(height,top+y2)-max(0,top+y1))/height}
            if b['Width']>0 and b['Height']>0:items.append({'Label':model.names[int(category)],'Confidence':confidence,'Bounds':b})
        proposals.extend(items)
        passes.append({'View':name,'Crop':{'X':left/width,'Y':top/height,'Width':(right-left)/width,'Height':(bottom-top)/height},'Detections':items,'Milliseconds':(time.perf_counter()-clock)*1000})
    combined=[]
    for candidate in sorted(proposals,key=lambda t:t['Confidence'],reverse=True):
        if not any(t['Label']==candidate['Label'] and iou(t['Bounds'],candidate['Bounds'])>=.5 for t in combined):combined.append(candidate)
    return combined,passes,(time.perf_counter()-started)*1000
