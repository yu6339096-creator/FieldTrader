"""Local, persistent inference worker. JSON lines on stdout; diagnostics on stderr.
No network, training, capture, or input injection. All models are supplied locally.
"""
import sys, os, json, time, argparse, contextlib, hashlib
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor

def main():
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stdin.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', required=True)
    args = parser.parse_args()
    cfg = json.loads(Path(args.config).read_text(encoding='utf-8-sig'))
    base=Path(args.config).resolve().parent
    def resolve(value):
        path=Path(os.path.expandvars(value))
        return str(path if path.is_absolute() else (base/path).resolve())
    for key in ['YoloModel','OcrConfig','OcrDetectionModel','OcrRecognitionModel','OcrClassificationModel','CacheDirectory']:
        cfg[key]=resolve(cfg[key])
    cfg['DependencyDirectories']=[resolve(p) for p in cfg['DependencyDirectories']]
    Path(cfg['CacheDirectory']).mkdir(parents=True,exist_ok=True)
    for directory in reversed(cfg['DependencyDirectories']):
        sys.path.insert(0, directory)
    threads = max(1, min(int(cfg.get('Threads', 2)), 4))
    os.environ['OMP_NUM_THREADS'] = str(threads)
    os.environ['MKL_NUM_THREADS'] = str(threads)
    os.environ['YOLO_CONFIG_DIR'] = cfg['CacheDirectory']
    os.environ['YOLO_AUTOINSTALL'] = 'false'
    os.environ['YOLO_OFFLINE'] = 'true'
    for key in ['YoloModel', 'OcrConfig', 'OcrDetectionModel', 'OcrRecognitionModel', 'OcrClassificationModel']:
        if not Path(cfg[key]).is_file():
            raise FileNotFoundError(key + ': ' + cfg[key])
    sha = hashlib.sha256(Path(cfg['YoloModel']).read_bytes()).hexdigest()
    if sha.lower() != cfg['YoloSha256'].lower():
        raise ValueError('YOLO model SHA256 mismatch')
    started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        import torch
        torch.set_num_threads(threads)
        torch.set_num_interop_threads(1)
        import cv2
        cv2.setNumThreads(1)
        import numpy as np
        from PIL import Image
        from ultralytics import YOLO
        from rapidocr_onnxruntime import RapidOCR
        # Relative path preserves apostrophes in the Windows user directory.
        model_started=time.perf_counter()
        model = YOLO(os.path.relpath(cfg['YoloModel']))
        model_load_ms=(time.perf_counter()-model_started)*1000
        expected = cfg['Classes']
        actual = [model.names[i] for i in range(len(model.names))]
        if actual != expected:
            raise ValueError('YOLO class mapping mismatch: ' + repr(actual))
        ocr_started=time.perf_counter()
        ocr = RapidOCR(config_path=cfg['OcrConfig'], det_model_path=cfg['OcrDetectionModel'],
            rec_model_path=cfg['OcrRecognitionModel'], cls_model_path=cfg['OcrClassificationModel'],
            intra_op_num_threads=threads, inter_op_num_threads=1)
    ocr_load_ms=(time.perf_counter()-ocr_started)*1000
    startup_ms=(time.perf_counter()-started)*1000
    print(json.dumps({'Ready': True, 'Classes': actual, 'ModelSha256': sha,
        'StartupMs': startup_ms,'Stages':{'YoloLoadMs':model_load_ms,'OcrLoadMs':ocr_load_ms,'ImportsAndInitializationMs':startup_ms-model_load_ms-ocr_load_ms}}), flush=True)

    ocr_stats={}
    frame_index=0
    def run_ocr(image):
        result,elapsed=ocr(image)
        ocr_stats['Passes']=ocr_stats.get('Passes',0)+1
        if elapsed and len(elapsed)==3:
            for key,value in zip(['OcrDetectionMs','OcrDirectionMs','OcrRecognitionMs'],elapsed):
                ocr_stats[key]=ocr_stats.get(key,0)+float(value)*1000
        return result,elapsed
    def read_rec(image):
        result,elapsed=ocr.text_rec(image)
        ocr_stats['Passes']=ocr_stats.get('Passes',0)+1
        ocr_stats['OcrRecognitionMs']=ocr_stats.get('OcrRecognitionMs',0)+float(elapsed)*1000
        return result,elapsed
    def detect(pixels, width, height):
        start = time.perf_counter()
        with contextlib.redirect_stdout(sys.stderr):
            result = model.predict(pixels, imgsz=640, conf=.25, device='cpu', verbose=False)[0]
        regions = []
        for box, confidence, category in zip(result.boxes.xyxy.tolist(), result.boxes.conf.tolist(), result.boxes.cls.tolist()):
            x1, y1, x2, y2 = box
            regions.append({'Label': actual[int(category)], 'Confidence': confidence,
                'Bounds': {'X': max(0,x1)/width, 'Y': max(0,y1)/height,
                'Width': (min(width,x2)-max(0,x1))/width, 'Height': (min(height,y2)-max(0,y1))/height}})
        return regions, (time.perf_counter()-start)*1000

    def read(pixels, width, height):
        start = time.perf_counter()
        result, _ = run_ocr(cv2.cvtColor(np.asarray(pixels), cv2.COLOR_RGB2BGR))
        texts = []
        for points, content, score in result or []:
            xs, ys = zip(*points)
            texts.append({'Label': content, 'Confidence': float(score), 'Bounds': {
                'X': max(0,min(xs))/width, 'Y': max(0,min(ys))/height,
                'Width': (min(width,max(xs))-max(0,min(xs)))/width,
                'Height': (min(height,max(ys))-max(0,min(ys)))/height}})
        return texts, (time.perf_counter()-start)*1000

    with ThreadPoolExecutor(max_workers=2) as pool:
        for line in sys.stdin:
            request = {}
            try:
                request = json.loads(line)
                start = time.perf_counter()
                frame_index+=1
                ocr_stats={}
                raw = Path(request['ScreenshotPath']).read_bytes()
                digest = hashlib.sha256(raw).hexdigest()
                if digest != request['ContentHash']:
                    raise ValueError('Screenshot changed after capture')
                import io
                with Image.open(io.BytesIO(raw)) as source:
                    pixels = source.convert('RGB')
                width, height = pixels.size
                preprocess_ms=(time.perf_counter()-start)*1000
                yolo = pool.submit(detect, pixels, width, height)
                words = pool.submit(read, pixels, width, height)
                regions, yolo_ms = yolo.result()
                texts, ocr_ms = words.result()
                all_roi_started=time.perf_counter()
                # Foreground OCR follows the proven prototype's regional crops. Also scan
                # the top notification strip independently of YOLO notification detections.
                crops=[(.31,.065,.69,.23,1.25)]
                for region in regions:
                    b=region['Bounds']
                    if region['Confidence'] < .6: continue
                    if region['Label'] == 'result_region':
                        crops.append((b['X'],b['Y'],b['X']+b['Width'],b['Y']+b['Height'],1.5))
                    elif region['Label']=='detail_panel':
                        crops.append((b['X'],b['Y'],b['X']+b['Width'],b['Y']+b['Height']*.12,2))
                for left,top,right,bottom,scale in crops:
                    x1,y1,x2,y2=int(left*width),int(top*height),int(right*width),int(bottom*height)
                    crop=cv2.resize(cv2.cvtColor(np.asarray(pixels.crop((x1,y1,x2,y2))),cv2.COLOR_RGB2BGR),None,fx=scale,fy=scale)
                    local,_=run_ocr(crop)
                    recovered=[]
                    for points,word,score in local or []:
                        xs,ys=zip(*points)
                        recovered.append({'Label':word,'Confidence':float(score),'Bounds':{'X':(x1+min(xs)/scale)/width,'Y':(y1+min(ys)/scale)/height,'Width':(max(xs)-min(xs))/scale/width,'Height':(max(ys)-min(ys))/scale/height}})
                    if recovered:
                        texts=[t for t in texts if (top==.065) or not (left<=t['Bounds']['X'] and top<=t['Bounds']['Y'] and t['Bounds']['X']+t['Bounds']['Width']<=right and t['Bounds']['Y']+t['Bounds']['Height']<=bottom)]
                        texts.extend(recovered)
                # Read small material-card counts locally; geometry remains in source pixels.
                for region in regions:
                    if region['Label'] not in ('material_card','product_card') or region['Confidence'] < .6:
                        continue
                    b=region['Bounds']
                    import re
                    inside=[t for t in texts if t['Confidence']>=.95 and b['X']<=t['Bounds']['X'] and b['Y']<=t['Bounds']['Y'] and t['Bounds']['X']+t['Bounds']['Width']<=b['X']+b['Width']+.003 and t['Bounds']['Y']+t['Bounds']['Height']<=b['Y']+b['Height']+.003]
                    is_receipt=any(r['Label']=='result_region' and r['Confidence']>=.6 for r in regions)
                    titles=[t for t in inside if t['Bounds']['Y']<b['Y']+b['Height']*.25 and any(c.isalpha() for c in t['Label'])]
                    prices=[t for t in inside if t['Bounds']['Y']>b['Y']+b['Height']*.65 and re.fullmatch(r'[0-9,]+',t['Label'].strip())]
                    counts={re.sub(r'\s+','',t['Label']) for t in inside if re.fullmatch(r'\d+\s*/\s*\d+',t['Label'].strip())}
                    if (region['Label']=='product_card' and not is_receipt and len(titles)==1 and len(prices)==1) or (region['Label']=='material_card' and len(titles)==1 and len(counts)==1):
                        ocr_stats['SkippedRedundantCrops']=ocr_stats.get('SkippedRedundantCrops',0)+1
                        continue
                    x1,y1=int(b['X']*width),int(b['Y']*height)
                    x2,y2=int((b['X']+b['Width'])*width),int((b['Y']+b['Height'])*height)
                    crop=cv2.resize(cv2.cvtColor(np.asarray(pixels.crop((x1,y1,x2,y2))),cv2.COLOR_RGB2BGR),None,fx=3,fy=3)
                    local,_=run_ocr(crop)
                    for points,word,score in local or []:
                        if score < .90: continue
                        xs,ys=zip(*points)
                        token={'Label':word,'Confidence':float(score),'Bounds':{'X':(x1+min(xs)/3)/width,'Y':(y1+min(ys)/3)/height,'Width':(max(xs)-min(xs))/3/width,'Height':(max(ys)-min(ys))/3/height}}
                        tb=token['Bounds']
                        if not any(t['Label'].replace(' ','')==word.replace(' ','') and abs(t['Bounds']['X']-tb['X'])<.01 and abs(t['Bounds']['Y']-tb['Y'])<.01 for t in texts):
                            texts.append(token)
                # Recover small input values missed by full-frame text detection. Keep
                # original pixel geometry; never synthesize a quantity of one.
                roi_started = time.perf_counter()
                for region in regions:
                    if region['Label'] not in ('quantity_field', 'price_field') or region['Confidence'] < .8:
                        continue
                    b = region['Bounds']
                    x1,y1 = int(b['X']*width),int(b['Y']*height)
                    x2,y2 = int((b['X']+b['Width'])*width),int((b['Y']+b['Height'])*height)
                    # Input widgets have minus/plus at their edges; retain center value.
                    pad = int((x2-x1)*.2)
                    x1 += pad; x2 -= pad
                    if x2 <= x1 or y2 <= y1:
                        continue
                    crop = cv2.cvtColor(np.asarray(pixels.crop((x1,y1,x2,y2))),cv2.COLOR_RGB2BGR)
                    values, _ = read_rec(crop)
                    content, confidence = values[0]
                    if confidence < .95 or not content.isdecimal():
                        # Wide dark widgets defeat direct recognition of a tiny digit. Detect
                        # text within an enlarged crop, preserving actual detected geometry.
                        enlarged = cv2.resize(crop, None, fx=3, fy=3)
                        local_words, _ = run_ocr(enlarged)
                        for points, word, score in local_words or []:
                            if score < .90 or not word.isdecimal():
                                continue
                            xs, ys = zip(*points)
                            token = {'Label':word, 'Confidence':float(score), 'Bounds': {
                                'X':(x1+min(xs)/3)/width, 'Y':(y1+min(ys)/3)/height,
                                'Width':(max(xs)-min(xs))/3/width, 'Height':(max(ys)-min(ys))/3/height}}
                            existing = [t for t in texts if t['Label']==word
                                and b['X'] <= t['Bounds']['X'] <= b['X']+b['Width']
                                and b['Y'] <= t['Bounds']['Y'] <= b['Y']+b['Height']]
                            if not existing:
                                texts.append(token)
                        continue
                    center = {'X': x1/width, 'Y': y1/height, 'Width':(x2-x1)/width, 'Height':(y2-y1)/height}
                    existing = [t for t in texts if t['Label'].replace(',','').isdecimal()
                        and b['X'] <= t['Bounds']['X'] <= b['X']+b['Width']
                        and b['Y']-.003 <= t['Bounds']['Y'] <= b['Y']+b['Height']]
                    # Disagreement remains visible and causes fusion to reject ambiguity.
                    if not any(t['Label'].replace(',','') == content for t in existing):
                        texts.append({'Label':content,'Confidence':float(confidence),'Bounds':center})
                # Receipt quantity is independent evidence, never inferred from total / unit price.
                if any(r['Label']=='result_region' and r['Confidence']>=.8 for r in regions):
                    for r in regions:
                        if r['Label']!='product_card' or r['Confidence']<.8:
                            continue
                        b=r['Bounds']; x1=int((b['X']+b['Width']*.7)*width); x2=int((b['X']+b['Width'])*width)
                        y1=int((b['Y']+b['Height']*.8)*height); y2=int((b['Y']+b['Height'])*height)
                        crop=cv2.cvtColor(np.asarray(pixels.crop((x1,y1,x2,y2))),cv2.COLOR_RGB2BGR)
                        local_words,_=run_ocr(cv2.resize(crop,None,fx=3,fy=3))
                        import re
                        for points,word,score in local_words or []:
                            # Preserve low-confidence evidence for diagnostics; fusion still requires .90.
                            if not re.fullmatch(r'[xX×]\d+',word):
                                continue
                            xs,ys=zip(*points)
                            texts.append({'Label':word,'Confidence':float(score),'Bounds':{'X':(x1+min(xs)/3)/width,'Y':(y1+min(ys)/3)/height,'Width':(max(xs)-min(xs))/3/width,'Height':(max(ys)-min(ys))/3/height}})
                roi_ms=(time.perf_counter()-all_roi_started)*1000
                ocr_ms += roi_ms
                region_content={}
                for region in regions:
                    b=region['Bounds']
                    key=region['Label']+'|'+ '|'.join(f"{b[k]:.6f}" for k in ['X','Y','Width','Height'])
                    sample=pixels.crop((int((b['X']+b['Width']*.15)*width),int((b['Y']+b['Height']*.15)*height),int((b['X']+b['Width']*.85)*width),int((b['Y']+b['Height']*.85)*height)))
                    region_content[key]=hashlib.sha256(sample.tobytes()).hexdigest()
                response = {'FrameId': request['Id'], 'CapturedAt': request['CapturedAt'], 'ContentHash': digest,
                    'Width': width, 'Height': height, 'Regions': regions, 'Texts': texts,'RegionContent':region_content,
                    'YoloMs': yolo_ms, 'OcrMs': ocr_ms, 'TotalMs': (time.perf_counter()-start)*1000,
                    'Performance':{'Cold':frame_index==1,'WorkerPid':os.getpid(),'FrameIndex':frame_index,'StartupMs':startup_ms if frame_index==1 else 0,'OcrPasses':int(ocr_stats.get('Passes',0)),'SkippedRedundantCrops':int(ocr_stats.get('SkippedRedundantCrops',0)),
                        'Stages':{'PreprocessMs':preprocess_ms,'YoloPredictMs':yolo_ms,'OcrTotalMs':ocr_ms,'OcrRegionMs':roi_ms,**{k:v for k,v in ocr_stats.items() if k not in ('Passes','SkippedRedundantCrops')}}}}
            except Exception as exc:
                response = {'FrameId': request.get('Id'), 'Error': str(exc)}
            # redirect_stdout is process-global; both futures have completed before this write.
            print(json.dumps(response, ensure_ascii=False), flush=True)

if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        print(json.dumps({'Ready': False, 'Error': str(exc)}), flush=True)
        raise
