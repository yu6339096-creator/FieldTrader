"""Geometry unit checks only: stub detections are not model-accuracy evidence."""
from pathlib import Path
import sys,unittest
from types import SimpleNamespace
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/FieldTrader/Market'))
from item_vision import predict_items

class Values(list):
 def tolist(self):return list(self)
class Model:
 names={0:'a',1:'b'}
 def __init__(self):self.calls=0
 def predict(self,pixels,**kwargs):
  self.calls+=1
  data=([(1200,200,1300,300)],[.99],[0]) if self.calls==1 else ([],[],[])
  if self.calls==3:data=([(40,70,140,170),(40,70,140,170)],[.8,.85],[0,1])
  return [SimpleNamespace(boxes=SimpleNamespace(xyxy=Values(data[0]),conf=Values(data[1]),cls=Values(data[2])))]
class Pixels:
 def __init__(self,size):self.size=size
 def crop(self,box):return Pixels((box[2]-box[0],box[3]-box[1]))
class PolicyChecks(unittest.TestCase):
 def test_original_coordinates_and_classwise_suppression(self):
  model=Model();items,passes,_=predict_items(model,Pixels((2000,1000)))
  self.assertEqual(model.calls,3);self.assertEqual(len(passes),3);self.assertEqual(len(items),2)
  self.assertEqual(items[0]['Confidence'],.99)
  for item in items:
   for key,expected in dict(X=.6,Y=.2,Width=.05,Height=.1).items():self.assertAlmostEqual(item['Bounds'][key],expected)
  self.assertEqual(passes[2]['Detections'][0]['Confidence'],.8)
 def test_small_input_does_not_use_scene_rois(self):
  model=Model();_,passes,_=predict_items(model,Pixels((343,114)))
  self.assertEqual(model.calls,1);self.assertEqual(passes[0]['View'],'full')
if __name__=='__main__':unittest.main()
