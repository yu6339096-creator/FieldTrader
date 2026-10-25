namespace FieldTrader.Market;

public static class AbsoluteMouseCoordinates {
 // Target the centre of a physical pixel in the full virtual desktop, including negative origins.
 public static (int X,int Y) Normalize(int x,int y,int left,int top,int width,int height) {
  if(width<=0||height<=0||x<(long)left||y<(long)top||x>=(long)left+width||y>=(long)top+height)
   throw new InputNotSentException("鼠标目标不在当前桌面范围内，尚未发送点击");
  int Axis(int pixel,int origin,int size)=>(int)Math.Clamp((((long)pixel-origin)*65536+32768)/size,0,65535);
  return(Axis(x,left,width),Axis(y,top,height));
 }
}
