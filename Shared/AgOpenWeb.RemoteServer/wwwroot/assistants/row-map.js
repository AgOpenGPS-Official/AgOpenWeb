let epoch=0,layer=null;
export const layerEpoch=()=>epoch;
export function setLayer(name,value){layer=value;}
window.addEventListener('assistant:fieldchanged',()=>{epoch++;layer=null;});
window.addEventListener('assistant:disconnected',()=>{epoch++;layer=null;});
window.FieldAssistantMap={draw(canvas,{CK,w2s,pw}){
  if(!layer)return;const paint=new CK.Paint();paint.setAntiAlias(true);paint.setStyle(CK.PaintStyle.Stroke);
  const draw=points=>{for(let i=1;i<points.length;i++){const a=points[i-1],b=points[i];if(pw(a.e,a.n)<1||pw(b.e,b.n)<1)continue;const [ax,ay]=w2s(a.e,a.n),[bx,by]=w2s(b.e,b.n);canvas.drawLine(ax,ay,bx,by,paint);}};
  layer.paths.forEach((row,index)=>{paint.setColor(CK.Color(...(index===layer.selected?[70,230,170]:[80,155,215]),1));paint.setStrokeWidth(index===layer.selected?4:2);draw(row.points);});
  paint.setColor(CK.Color(255,135,90,1));paint.setStrokeWidth(4);draw(layer.recording);paint.delete();
}};
