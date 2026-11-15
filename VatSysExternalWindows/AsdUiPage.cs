namespace VatSysExternalWindows;

public static class AsdUiPage
{
    public const string Html = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover'>
<meta name='apple-mobile-web-app-capable' content='yes'>
<meta name='apple-mobile-web-app-status-bar-style' content='black-translucent'>
<title>ASD</title>
<style>
@font-face{font-family:'Terminus';src:url('/font.ttf') format('truetype'),local('Terminus (TTF)'),local('Terminus'),local('Terminus (TTF) for Windows'),local('TerminusTTF'),local('TerminusTTFWindows');font-weight:bold;}
*{margin:0;padding:0;box-sizing:border-box;-webkit-touch-callout:none;-webkit-user-select:none;user-select:none;}
html,body{width:100%;height:100%;overflow:hidden;background:rgb(127,127,127);font-family:'Terminus','Consolas','Courier New',monospace;color:rgb(0,0,96);}
body{padding-top:env(safe-area-inset-top);padding-bottom:env(safe-area-inset-bottom);padding-left:env(safe-area-inset-left);padding-right:env(safe-area-inset-right);display:flex;flex-direction:column;}
#menubar{display:flex;background:rgb(160,170,170);height:25px;align-items:stretch;flex-shrink:0;position:relative;z-index:100;padding:3px 0 3px 8px;
  border-top:1px solid rgb(210,218,218);border-left:1px solid rgb(210,218,218);border-bottom:1px solid rgb(100,108,108);border-right:1px solid rgb(100,108,108);}
.menu-wrap{position:relative;}
.menu-btn{background:rgb(160,170,170);border:none;color:rgb(0,0,96);font:bold 16px 'Terminus','Consolas',monospace;padding:0 8px;cursor:pointer;position:relative;height:100%;line-height:19px;}
.menu-btn:hover{background:rgb(130,146,146);}
.menu-btn.active{background:rgb(130,146,146);border-top:1px solid rgb(100,108,108);border-left:1px solid rgb(100,108,108);border-bottom:1px solid rgb(210,218,218);border-right:1px solid rgb(210,218,218);}
.dropdown{display:none;position:absolute;top:25px;left:0;background:rgb(160,170,170);min-width:180px;z-index:200;max-height:70vh;overflow-y:auto;-webkit-overflow-scrolling:touch;
  border-top:1px solid rgb(210,218,218);border-left:1px solid rgb(210,218,218);border-bottom:1px solid rgb(100,108,108);border-right:1px solid rgb(100,108,108);}
.dropdown.show{display:block;}
.dd-item{display:flex;align-items:center;padding:2px 16px 2px 4px;font:bold 16px 'Terminus','Consolas',monospace;color:rgb(0,0,96);cursor:pointer;white-space:nowrap;min-height:22px;}
.dd-item:hover{background:rgb(130,146,146);}
.dd-item.disabled{color:rgb(96,0,0);cursor:default;}
.dd-item.disabled:hover{background:transparent;}
.dd-item .chk{width:22px;text-align:center;flex-shrink:0;font-size:14px;}
.dd-back{font-weight:bold;border-bottom:1px solid rgb(120,128,128);}
.dd-group::after{content:'\25B6';margin-left:auto;padding-left:14px;font-size:9px;display:inline-block;}
/* min-height:0 lets the canvas shrink with the window (a flex item's
   default minimum is its own bitmap size) */
canvas{flex:1;min-height:0;display:block;touch-action:none;}
</style>
</head>
<body>
<div id='menubar'>
  <div class='menu-wrap' id='mw-pos'>
    <button class='menu-btn' id='btn-pos'>Position</button>
    <div class='dropdown' id='dd-pos'></div>
  </div>
  <div class='menu-wrap' id='mw-maps'>
    <button class='menu-btn' id='btn-maps'>Maps</button>
    <div class='dropdown' id='dd-maps'></div>
  </div>
  <div class='menu-wrap' id='mw-tools'>
    <button class='menu-btn' id='btn-tools'>Tools</button>
    <div class='dropdown' id='dd-tools'>
      <div class='dd-item disabled'>Bearing Range Line</div>
      <div class='dd-item disabled'>LATC</div>
      <div class='dd-item disabled'>QuickTag</div>
    </div>
  </div>
  <a href='/' class='menu-btn' id='btn-back' style='margin-left:auto;text-decoration:none;display:flex;align-items:center;'>◀ Back</a>
</div>
<canvas id='cv'></canvas>
<script>
'use strict';
const cv=document.getElementById('cv'),ctx=cv.getContext('2d');
let W=0,H=0,dpr=1;

/* ============================================================
   State
   ============================================================ */
let positions=[];
let allAsdMaps=[];
let colours=null;
let selectedPos=null;

let centerLat=-27,centerLon=153,nmRange=200;

let layerState={};
let aircraft=[];
let acTimer=null;
let labelFontSize=18,mapFontSize=16;
// Map label character cell (ASDControlDX MapFontWidth / MapFontHeight)
let mapFontW=0,mapFontH=0;
let dynamicBelow=[],dynamicAbove=[];

/* ============================================================
   Track symbols — served from the profile's Tracks.xml via
   /api/symbols, rendered exactly like vatSys geometry
   realisations: StrokeWidth 0 → filled (Filled figures only),
   otherwise stroked outline of that width. SpecialCode
   variants keyed by squawk code.
   ============================================================ */
let symTable={};
function symKey(t,c){return t+'|'+c;}
function buildSymTable(defs){
  for(let d of defs) symTable[symKey(d.Type,d.SpecialCode)]=d;
}
// Fallback: Australia profile defaults, used until /api/symbols loads and for
// any symbol type the server doesn't return
buildSymTable([
 {Type:'Primary',SpecialCode:-1,StrokeWidth:2,Rotate:false,Shapes:[{Kind:'path',Figures:[
   {Start:[-6,0],Filled:false,Closed:false,Segs:[{T:'l',P:[6,0]}]},
   {Start:[0,6],Filled:false,Closed:false,Segs:[{T:'l',P:[0,-6]}]}]}]},
 {Type:'ModeA',SpecialCode:-1,StrokeWidth:2,Rotate:false,Shapes:[{Kind:'ellipse',Center:[0,0],RadiusX:6.5,RadiusY:6.5}]},
 {Type:'ModeS',SpecialCode:-1,StrokeWidth:2,Rotate:false,Shapes:[{Kind:'ellipse',Center:[0,0],RadiusX:6.5,RadiusY:6.5}]},
 {Type:'ADSB',SpecialCode:-1,StrokeWidth:2,Rotate:false,Shapes:[{Kind:'path',Figures:[
   {Start:[0,-4],Filled:false,Closed:true,Segs:[
     {T:'b',B:[[0,-4],[12,-12],[4,0]]},{T:'b',B:[[4,0],[12,12],[0,4]]},
     {T:'b',B:[[0,4],[-12,12],[-4,0]]},{T:'b',B:[[-4,0],[-12,-12],[0,-4]]}]}]}]},
 {Type:'CoastRadar',SpecialCode:-1,StrokeWidth:3,Rotate:false,Shapes:[{Kind:'path',Figures:[
   {Start:[-3,0],Filled:false,Closed:false,Segs:[{T:'l',P:[-7,0]}]},
   {Start:[0,-3],Filled:false,Closed:false,Segs:[{T:'l',P:[0,-7]}]},
   {Start:[3,0],Filled:false,Closed:false,Segs:[{T:'l',P:[7,0]}]},
   {Start:[0,3],Filled:false,Closed:false,Segs:[{T:'l',P:[0,7]}]}]}]},
 {Type:'CoastADSB',SpecialCode:-1,StrokeWidth:3,Rotate:false,Shapes:[{Kind:'path',Figures:[
   {Start:[-3,-3],Filled:false,Closed:false,Segs:[{T:'l',P:[-7,-7]}]},
   {Start:[3,-3],Filled:false,Closed:false,Segs:[{T:'l',P:[7,-7]}]},
   {Start:[3,3],Filled:false,Closed:false,Segs:[{T:'l',P:[7,7]}]},
   {Start:[-3,3],Filled:false,Closed:false,Segs:[{T:'l',P:[-7,7]}]}]}]},
 {Type:'Cancelled',SpecialCode:-1,StrokeWidth:2,Rotate:false,Shapes:[{Kind:'ellipse',Center:[0,0],RadiusX:3,RadiusY:3}]},
 {Type:'HistoryMarker',SpecialCode:-1,StrokeWidth:0,Rotate:false,Shapes:[{Kind:'ellipse',Center:[0,0],RadiusX:2,RadiusY:2}]},
 {Type:'VelocityMarker',SpecialCode:-1,StrokeWidth:0,Rotate:false,Shapes:[{Kind:'ellipse',Center:[0,0],RadiusX:2,RadiusY:2}]},
 {Type:'Selection',SpecialCode:-1,StrokeWidth:2,Rotate:false,Shapes:[{Kind:'path',Figures:[
   {Start:[11,-5],Filled:false,Closed:true,Segs:[{T:'l',P:[5,-11]},{T:'l',P:[-5,-11]},{T:'l',P:[-11,-5]},{T:'l',P:[-11,5]},{T:'l',P:[-5,11]},{T:'l',P:[5,11]},{T:'l',P:[11,5]}]}]}]}
]);
function getSymbol(type,ssr){
  return symTable[symKey(type,ssr)]||symTable[symKey(type,-1)]||null;
}
function traceShape(sh,fillOnly){
  if(sh.Kind==='ellipse'){
    ctx.beginPath();
    ctx.ellipse(sh.Center?sh.Center[0]:0,sh.Center?sh.Center[1]:0,sh.RadiusX,sh.RadiusY,0,0,Math.PI*2);
    return true;
  }
  if(sh.Kind==='rect'){
    let tl=sh.TopLeft||[0,0],br=sh.BottomRight||[0,0];
    ctx.beginPath();
    ctx.rect(tl[0],tl[1],br[0]-tl[0],br[1]-tl[1]);
    return true;
  }
  if(sh.Kind==='path'&&sh.Figures){
    ctx.beginPath();
    let traced=false;
    for(let f of sh.Figures){
      if(fillOnly&&!f.Filled) continue; // Hollow figures excluded from fills (D2D FigureBegin)
      ctx.moveTo(f.Start[0],f.Start[1]);
      for(let s of f.Segs){
        if(s.T==='b'&&s.B) ctx.bezierCurveTo(s.B[0][0],s.B[0][1],s.B[1][0],s.B[1][1],s.B[2][0],s.B[2][1]);
        else if(s.P) ctx.lineTo(s.P[0],s.P[1]);
      }
      if(f.Closed) ctx.closePath();
      traced=true;
    }
    return traced;
  }
  return false;
}
function drawSymbol(type,ssr,x,y,headingDeg,col,lat,lon){
  let def=getSymbol(type,ssr);
  if(!def) return;
  ctx.save();
  ctx.translate(x,y);
  // PaintTrackGeometryRealisation: heading + stereographic north correction + screen rotation
  if(def.Rotate&&headingDeg!=null&&headingDeg!==-1)
    ctx.rotate(headingDeg*D2R+(lat!=null?northCorrection(lat,lon):0)+viewRotationRad());
  let filled=def.StrokeWidth===0;
  for(let sh of def.Shapes){
    if(!traceShape(sh,filled)) continue;
    if(filled){ctx.fillStyle=col;ctx.fill();}
    else{ctx.strokeStyle=col;ctx.lineWidth=def.StrokeWidth;ctx.stroke();}
  }
  ctx.restore();
}

/* ============================================================
   Projection — port of vatSys ASDControlDX
   Stereographic about the screen centre (ConvertLLToScreen /
   ConvertScreenToLL), zoomed so the range spans the window width
   (SetZoom / CalculateZoom). centerLat/centerLon are the SCREEN
   centre: panning and zooming move them, as vatSys does.
   ============================================================ */
const D2R=Math.PI/180;
const MIN_RANGE=10,MAX_RANGE=9999; // MMI.MinimumASDRange / MaximumASDRange for the ASD
function viewRotationRad(){return 0;}
function clampRange(r){return Math.max(MIN_RANGE,Math.min(MAX_RANGE,r));}

let pj={};
function proj(){
  let rot=viewRotationRad();
  if(pj.lat!==centerLat||pj.lon!==centerLon||pj.range!==nmRange||pj.W!==W||pj.rot!==rot){
    let la=centerLat*D2R;
    // The point range/2 NM due east of the centre lands on the right-hand edge.
    // 1 NM = 1 arc minute, as in Conversions.CalculateLLFromBearingRange.
    let c=nmRange/2*Math.PI/10800;
    pj={lat:centerLat,lon:centerLon,range:nmRange,W:W,rot:rot,
        sin0:Math.sin(la),cos0:Math.cos(la),
        scale:(W/2)/(2*Math.tan(c/2)),
        cr:Math.cos(rot),sr:Math.sin(rot)};
  }
  return pj;
}
function latLonToCanvas(lat,lon){
  let p=proj(),la=lat*D2R,dl=(lon-centerLon)*D2R;
  let sl=Math.sin(la),cl=Math.cos(la),cd=Math.cos(dl);
  let k=2/(1+p.sin0*sl+p.cos0*cl*cd)*p.scale;
  let x=k*cl*Math.sin(dl),y=-k*(p.cos0*sl-p.sin0*cl*cd);
  return[W/2+x*p.cr-y*p.sr,H/2+x*p.sr+y*p.cr];
}
function canvasToLatLon(px,py){
  let p=proj(),dx=px-W/2,dy=py-H/2;
  let x=(dx*p.cr+dy*p.sr)/p.scale,y=(dx*p.sr-dy*p.cr)/p.scale;
  let rho=Math.hypot(x,y);
  if(rho===0) return[centerLat,centerLon];
  let c=2*Math.atan(rho/2),sc=Math.sin(c),cc=Math.cos(c);
  let lat=Math.asin(Math.max(-1,Math.min(1,cc*p.sin0+y*sc*p.cos0/rho)));
  let lon=centerLon*D2R+Math.atan2(x*sc,rho*p.cos0*cc-y*p.sin0*sc);
  return[lat/D2R,((lon/D2R+540)%360)-180];
}
// Angle of true north at a point, for rotate-to-track symbols
// (CalculateStereographicNorthCorrection)
function northCorrection(lat,lon){
  if(Math.abs(centerLat)===90) return 0;
  let p=proj(),la=lat*D2R,dl=(lon-centerLon)*D2R;
  let sl=Math.sin(la),cl=Math.cos(la),cd=Math.cos(dl);
  let y=2/(1+p.sin0*sl+p.cos0*cl*cd)*(p.cos0*sl-p.sin0*cl*cd);
  let v=Math.PI/2-Math.acos((-2*Math.tan(centerLat*D2R)-y)/(2/(p.cos0*Math.sin(dl))));
  return isFinite(v)?v:0;
}
// Great-circle point at a bearing and range (Conversions.CalculateLLFromBearingRange)
function fromBearingRange(lat,lon,distNm,bearingDeg){
  let la=lat*D2R,b=bearingDeg*D2R,d=distNm*Math.PI/10800;
  let la2=Math.asin(Math.sin(la)*Math.cos(d)+Math.cos(la)*Math.sin(d)*Math.cos(b));
  let dlo=Math.atan2(Math.sin(b)*Math.sin(d)*Math.cos(la),Math.cos(d)-Math.sin(la)*Math.sin(la2));
  return[la2/D2R,((lon+dlo/D2R+540)%360)-180];
}

// Move the screen centre so that lat/lon ll sits under canvas point (px,py)
function anchorAt(ll,px,py){
  if(!(proj().scale>0)) return;
  for(let i=0;i<3;i++){
    let p=latLonToCanvas(ll[0],ll[1]);
    let c=canvasToLatLon(W/2+p[0]-px,H/2+p[1]-py);
    if(!isFinite(c[0])||!isFinite(c[1])) return;
    centerLat=Math.max(-89,Math.min(89,c[0]));centerLon=c[1];
  }
}
// The view a double-tap returns to: the selected position's default centre and range
let home={lat:centerLat,lon:centerLon,range:nmRange};
function resetView(){centerLat=home.lat;centerLon=home.lon;nmRange=clampRange(home.range);}
function setHome(lat,lon,range){home={lat:lat,lon:lon,range:range};resetView();}

/* ============================================================
   Colours
   ============================================================ */
function rgb(a){return'rgb('+a[0]+','+a[1]+','+a[2]+')';}

function applyTheme(){
  if(!colours) return;
  let wb=colours.WindowBackground;
  if(!wb) return;
  let c=rgb(wb);
  let hi='rgb('+Math.min(255,wb[0]+50)+','+Math.min(255,wb[1]+48)+','+Math.min(255,wb[2]+48)+')';
  let sh='rgb('+Math.max(0,wb[0]-60)+','+Math.max(0,wb[1]-62)+','+Math.max(0,wb[2]-62)+')';
  let textCol=colours.InteractiveText?rgb(colours.InteractiveText):'rgb(0,0,96)';
  let depCol='rgb('+Math.max(0,wb[0]-30)+','+Math.max(0,wb[1]-24)+','+Math.max(0,wb[2]-24)+')';
  let style=document.getElementById('theme-overrides');
  if(!style){style=document.createElement('style');style.id='theme-overrides';document.head.appendChild(style);}
  style.textContent=
    'html,body{color:'+textCol+';}'+
    '#menubar{background:'+c+';border-top-color:'+hi+';border-left-color:'+hi+';border-bottom-color:'+sh+';border-right-color:'+sh+';}'+
    '.menu-btn{background:'+c+';color:'+textCol+';}'+
    '.menu-btn:hover{background:'+depCol+';}'+
    '.menu-btn.active{background:'+depCol+';border-top-color:'+sh+';border-left-color:'+sh+';border-bottom-color:'+hi+';border-right-color:'+hi+';}'+
    '.dropdown{background:'+c+';border-top-color:'+hi+';border-left-color:'+hi+';border-bottom-color:'+sh+';border-right-color:'+sh+';}'+
    '.dd-item{color:'+textCol+';}'+
    '.dd-item:hover{background:'+depCol+';}';
}

function findCustomColour(name){
  if(!colours||!colours.CustomColours) return null;
  if(colours.CustomColours[name]) return colours.CustomColours[name];
  let lower=name.toLowerCase();
  for(let k in colours.CustomColours) if(k.toLowerCase()===lower) return colours.CustomColours[k];
  return null;
}
function mapTypeColour(type,customColour){
  // Matches ASDControlDX.SelectMapBrush: any CustomColourName resolves through the
  // profile's Custom colours first; then System→PrimaryMap, System2/Flex→SecondaryMap,
  // Filled→Infill, Local/Global_Private→UserMap, Supervisor/TDA/REST_NTZ_DAIW→PRDArea
  if(!colours) return'#888';
  if(customColour){
    let cc=findCustomColour(customColour);
    if(cc) return rgb(cc);
  }
  let t=(type||'').toLowerCase();
  if(t==='system') return rgb(colours.PrimaryMap);
  if(t==='system2'||t==='flex') return rgb(colours.SecondaryMap);
  if(t==='filled') return rgb(colours.Infill);
  if(t==='local_private'||t==='global_private') return rgb(colours.UserMap||colours.PrimaryMap);
  if(t==='supervisor'||t==='tda'||t==='rest_ntz_daiw') return rgb(colours.PRDArea||colours.PrimaryMap);
  return rgb(colours.PrimaryMap);
}


/* ============================================================
   Layer key
   ============================================================ */
function layerKey(folder,name){return folder?(folder+'/'+name):name;}

/* ============================================================
   Line patterns — exact vatSys StrokeStyles (dash arrays are in
   multiples of the stroke width, as Direct2D custom dashes are):
   Broken = {12,6,2,6}, Dashed = {8,8}, Hash = {4,4},
   Dotted = D2D Dot (a dot every 2 x width).
   ============================================================ */
function lineDash(pat,w){
  if(pat==='broken') return [12*w,6*w,2*w,6*w];
  if(pat==='dashed') return [8*w,8*w];
  if(pat==='hash') return [4*w,4*w];
  if(pat==='dotted') return [1,Math.max(0.5,2*w-1)];
  return null;
}
// vatSys draws each polyline segment with its own DrawLine, so the
// dash phase restarts at every vertex — replicate that here.
function strokePolyline(pts,w,dash){
  ctx.lineWidth=w;
  if(!dash){
    ctx.setLineDash([]);
    ctx.beginPath();
    let p0=latLonToCanvas(pts[0][0],pts[0][1]);
    ctx.moveTo(p0[0],p0[1]);
    for(let i=1;i<pts.length;i++){let p=latLonToCanvas(pts[i][0],pts[i][1]);ctx.lineTo(p[0],p[1]);}
    ctx.stroke();
    return;
  }
  ctx.setLineDash(dash);
  let prev=latLonToCanvas(pts[0][0],pts[0][1]);
  for(let i=1;i<pts.length;i++){
    let p=latLonToCanvas(pts[i][0],pts[i][1]);
    ctx.beginPath();ctx.moveTo(prev[0],prev[1]);ctx.lineTo(p[0],p[1]);ctx.stroke();
    prev=p;
  }
  ctx.setLineDash([]);
}

// vatSys fills non-solid-pattern infills with an 8x8 checkerboard of
// Infill colour on ASDBackground (infillBrokenBrush bitmap)
let hatchPattern=null;
function buildHatch(){
  if(!colours) return;
  let c=document.createElement('canvas');c.width=8;c.height=8;
  let g=c.getContext('2d');
  g.fillStyle=rgb(colours.Background);g.fillRect(0,0,8,8);
  g.fillStyle=rgb(colours.Infill);g.fillRect(0,0,4,4);g.fillRect(4,4,4,4);
  hatchPattern=ctx.createPattern(c,'repeat');
}

/* ============================================================
   Rendering
   ============================================================ */
let renderPending=false;
function scheduleRender(){if(!renderPending){renderPending=true;requestAnimationFrame(()=>{renderPending=false;render();});}}

function render(){
  ctx.setTransform(dpr,0,0,dpr,0,0);
  ctx.imageSmoothingEnabled=false;
  ctx.fillStyle=colours?rgb(colours.Background):'rgb(127,127,127)';
  ctx.fillRect(0,0,W,H);

  let visibleLayers=[];
  for(let key in layerState){
    let ls=layerState[key];
    if(ls.visible&&ls.layer) visibleLayers.push(ls);
  }
  visibleLayers.sort((a,b)=>(b.layer.Priority||0)-(a.layer.Priority||0));

  // vatSys paint order: controlled-sector infill and active restricted-area
  // infills beneath the file maps, restricted-area outlines on top of them
  for(let dl of dynamicBelow) drawMapLayer(dl);
  for(let ls of visibleLayers) drawMapLayer(ls.layer);
  for(let dl of dynamicAbove) drawMapLayer(dl);

  drawTracks();
}

function drawMapLayer(layer){
    let col=mapTypeColour(layer.Type,layer.CustomColour);

    // Infills — Type=""Background"" paints in ASDBackground; non-solid patterns
    // fill with the Infill/Background checkerboard; everything else (including
    // Type=""None"") fills with the map's own colour (vatSys PaintMap)
    if(layer.Infills&&layer.Infills.length>0){
      for(let inf of layer.Infills){
        if(inf.Points.length<3) continue;
        let it=(inf.InfillType||'').toLowerCase();
        let ip=(inf.Pattern||'').toLowerCase();
        if(it==='background') ctx.fillStyle=colours?rgb(colours.Background):'rgb(127,127,127)';
        else if(hatchPattern&&(ip==='broken'||ip==='hash'||ip==='dashed'||ip==='dotted')) ctx.fillStyle=hatchPattern;
        else ctx.fillStyle=col;
        ctx.beginPath();
        let p0=latLonToCanvas(inf.Points[0][0],inf.Points[0][1]);
        ctx.moveTo(p0[0],p0[1]);
        for(let i=1;i<inf.Points.length;i++){let p=latLonToCanvas(inf.Points[i][0],inf.Points[i][1]);ctx.lineTo(p[0],p[1]);}
        ctx.closePath();ctx.fill();
      }
    }

    // Lines — flat caps like vatSys's aliased map lines. Dashed lines on
    // System maps replicate the two-tone bitmap brush: 8px PrimaryMap
    // alternating with 8px ASDBackground (the gaps paint opaquely).
    ctx.strokeStyle=col;ctx.lineJoin='round';ctx.lineCap='butt';
    let isSystem=(layer.Type||'').toLowerCase()==='system';
    for(let ln of layer.Lines){
      if(ln.Points.length<2) continue;
      let pat=(ln.Pattern||'').toLowerCase();
      if(pat==='none') continue;
      let w=ln.Width||1;
      if(pat==='dashed'&&isSystem){
        ctx.strokeStyle=colours?rgb(colours.Background):'rgb(127,127,127)';
        strokePolyline(ln.Points,w,null);
        ctx.strokeStyle=col;
        strokePolyline(ln.Points,w,[8,8]);
        continue;
      }
      strokePolyline(ln.Points,w,lineDash(pat,w));
    }
    ctx.lineWidth=1;

    drawRunways(layer,col);
    drawMapSymbols(layer,col);
    drawMapLabels(layer,col);
}

/* ============================================================
   Map runways, symbols and labels — ports of vatSys
   ASDControlDX.PaintMap (drawn in that order, after the lines)
   ============================================================ */
// Runway: 2.5px line between the thresholds, 1px extended centrelines
// with 12px tick marks across them.
function drawRunways(layer,col){
  if(!layer.Runways||layer.Runways.length===0) return;
  ctx.strokeStyle=col;ctx.setLineDash([]);ctx.lineCap='butt';
  let rot=viewRotationRad();
  for(let rw of layer.Runways){
    let a=latLonToCanvas(rw.P1[0],rw.P1[1]),b=latLonToCanvas(rw.P2[0],rw.P2[1]);
    ctx.lineWidth=2.5;
    ctx.beginPath();ctx.moveTo(a[0],a[1]);ctx.lineTo(b[0],b[1]);ctx.stroke();
    ctx.lineWidth=1;
    for(let cl of (rw.Centrelines||[])){
      let f=latLonToCanvas(cl.From[0],cl.From[1]),t=latLonToCanvas(cl.To[0],cl.To[1]);
      let ang=cl.Track*Math.PI/180+rot,dx=6*Math.cos(ang),dy=6*Math.sin(ang);
      ctx.beginPath();ctx.moveTo(f[0],f[1]);ctx.lineTo(t[0],t[1]);
      for(let tk of (cl.Ticks||[])){
        let q=latLonToCanvas(tk[0],tk[1]);
        ctx.moveTo(q[0]+dx,q[1]+dy);ctx.lineTo(q[0]-dx,q[1]-dy);
      }
      ctx.stroke();
    }
  }
}

// Map symbols: the 14 vatSys Map.Symbol.Types, same geometry as the
// 16x16 bitmaps vatSys builds in CreateSymbolOpacityBitmaps.
const SYM_TRIANGLE=[[0,-4],[4,3],[-4,3]];
const SYM_STAR=[[0,8],[2,2],[8,0],[2,-2],[0,-8],[-2,-2],[-8,0],[-2,2]];
const SYM_MALTESE=[[-3,7],[3,7],[1,1],[7,3],[7,-3],[1,-1],[3,-7],[-3,-7],[-1,-1],[-7,-3],[-7,3],[-1,1]];
const SYM_HEXAGON=[[3,-5],[-3,-5],[-6,0],[-3,5],[3,5],[6,0]];
function symPoly(pts){
  ctx.beginPath();ctx.moveTo(pts[0][0],pts[0][1]);
  for(let i=1;i<pts.length;i++) ctx.lineTo(pts[i][0],pts[i][1]);
  ctx.closePath();
}
function symCircle(r){ctx.beginPath();ctx.arc(0,0,r,0,Math.PI*2);}
function drawMapSymbols(layer,col){
  if(!layer.Symbols||layer.Symbols.length===0) return;
  ctx.fillStyle=col;ctx.strokeStyle=col;ctx.lineWidth=1;ctx.setLineDash([]);
  for(let sym of layer.Symbols){
    let p=latLonToCanvas(sym.Lat,sym.Lon);
    if(p[0]<0||p[1]<0||p[0]>W||p[1]>H) continue;
    ctx.save();ctx.translate(p[0],p[1]);
    switch(sym.SymbolType){
      case'SolidTriangle':symPoly(SYM_TRIANGLE);ctx.fill();ctx.stroke();break;
      case'HollowTriangle':symPoly(SYM_TRIANGLE);ctx.stroke();break;
      case'SolidTriangleWithBorder':symPoly(SYM_TRIANGLE);ctx.fill();ctx.strokeRect(-4,-4,8,7);break;
      case'HollowTriangleWithBorder':symPoly(SYM_TRIANGLE);ctx.stroke();ctx.strokeRect(-4,-4,8,7);break;
      case'HollowSquare':ctx.strokeRect(-3,-3,6,6);break;
      case'SolidStar':symPoly(SYM_STAR);ctx.fill();ctx.stroke();break;
      case'HollowStar':symPoly(SYM_STAR);ctx.stroke();break;
      case'MalteseCross':symPoly(SYM_MALTESE);ctx.fill();break;
      case'Circle':symCircle(6);ctx.stroke();break;
      case'DashedCircle':ctx.setLineDash([8,8]);symCircle(6);ctx.stroke();ctx.setLineDash([]);break;
      case'DotFillCircle':
        symCircle(2);ctx.stroke();
        ctx.lineWidth=0.5;ctx.setLineDash([1,1]);
        for(let r=3;r<=7;r++){symCircle(r);ctx.stroke();}
        ctx.setLineDash([]);ctx.lineWidth=1;
        break;
      case'Reticle':
        ctx.beginPath();
        ctx.moveTo(0,5);ctx.lineTo(0,8);ctx.moveTo(5,0);ctx.lineTo(8,0);
        ctx.moveTo(0,-5);ctx.lineTo(0,-8);ctx.moveTo(-5,0);ctx.lineTo(-8,0);
        ctx.stroke();
        symCircle(6);ctx.stroke();
        break;
      case'Hexagon':symPoly(SYM_HEXAGON);ctx.stroke();break;
      default:ctx.fillRect(-4,-4,8,8); // SolidSquare, the vatSys default
    }
    ctx.restore();
  }
}

// Map labels: ASDMapFont in the map's own colour. With a leader the text is
// anchored at the end of a short line in the LabelOrientation direction;
// Alignment/VerticalAlignment then position the text box on that anchor.
// A label that would overlap one already drawn for the same map is skipped.
const LABEL_OFFSET={N:[0,-14],NE:[10,-10],E:[14,0],SE:[10,10],S:[0,14],SW:[-10,10],W:[-19,0],NW:[-15,-15]};
function drawMapLabels(layer,col){
  if(!layer.Labels||layer.Labels.length===0) return;
  ctx.font='bold '+mapFontSize+""px 'Terminus','Consolas',monospace"";
  ctx.textAlign='left';ctx.textBaseline='top';
  ctx.fillStyle=col;ctx.strokeStyle=col;ctx.lineWidth=1;ctx.setLineDash([]);
  let cw=mapFontW||ctx.measureText('0').width,fh=mapFontH||mapFontSize;
  let placed=[];
  for(let lb of layer.Labels){
    let p=latLonToCanvas(lb.Lat,lb.Lon);
    if(p[0]<0||p[1]<0||p[0]>W||p[1]>H) continue;
    let text=lb.Text||'';
    let tw=cw*text.length;
    let ax=lb.Align==='Center'?tw/2:(lb.Align==='Right'?tw:0);
    let ay=lb.VAlign==='Top'?0:(lb.VAlign==='Middle'?fh/2:fh);
    let leader=lb.Leader!==false;
    let anchor=p;
    if(leader){let o=LABEL_OFFSET[lb.Dir]||LABEL_OFFSET.NE;anchor=[p[0]+o[0],p[1]+o[1]];}
    let x=anchor[0]-ax,y=anchor[1]-ay;
    let clash=false;
    for(let r of placed){
      if(x<r[0]+r[2]&&x+tw>r[0]&&y<r[1]+fh&&y+fh>r[1]){clash=true;break;}
    }
    if(clash) continue;
    placed.push([x,y,tw]);
    if(leader){ctx.beginPath();ctx.moveTo(p[0],p[1]);ctx.lineTo(anchor[0],anchor[1]);ctx.stroke();}
    ctx.fillText(text,x,y);
    // Border flags: 1=Left 2=Right 4=Top 8=Bottom
    let b=lb.Border||0;
    if(b){
      let bx=x-1,by=y+1,bw=tw+1;
      ctx.beginPath();
      if(b&1){ctx.moveTo(bx,by);ctx.lineTo(bx,by+fh);}
      if(b&2){ctx.moveTo(bx+bw,by);ctx.lineTo(bx+bw,by+fh);}
      if(b&4){ctx.moveTo(bx,by);ctx.lineTo(bx+bw,by);}
      if(b&8){ctx.moveTo(bx,by+fh);ctx.lineTo(bx+bw,by+fh);}
      ctx.stroke();
    }
  }
}

/* ============================================================
   Tracks
   The server makes every decision vatSys's PaintGroundTracks,
   PaintASDTrack and PaintLabel make — which symbols, what colour,
   which label cells — from vatSys's own state and the profile's
   label definitions. This only projects the position and draws.
   ============================================================ */
const LABEL_DIR_DEFAULT=1,LABEL_LEN_DEFAULT=1;
// Label positions moved on this page: track id -> {dir, len}. Until a label is
// moved here, the position set in vatSys is used.
let labelState={};
let labelMovingCS=null; // id of the track whose label is being moved
let labelMovePreview=null;
// Label character cell (ASDControlDX MainFontWidth / MainFontHeight)
let labelFontW=0,labelFontH=0;

// vatSys FlashShareTimer: the flash phase flips every 600 ms and the
// timeshare phase every fourth tick
let flashOn=true,timeshareOn=false,flashCount=0;
setInterval(()=>{
  flashOn=flashCount%2===0;
  if(++flashCount>=4){flashCount=0;timeshareOn=!timeshareOn;}
  let animated=aircraft.some(a=>(a.Label&&(a.Label.Blink||a.Label.Shared))||(a.Syms||[]).some(s=>s.Ident));
  if(animated) scheduleRender();
},600);

// vatSys GetLabelOrientation — direction (0-15) and leader length (0-2)
// from the track's screen position to the pointer
function getLabelOrientation(tx,ty,mx,my){
  let dy=my-ty,dx=mx-tx;
  if(dx===0&&dy===0) return[LABEL_DIR_DEFAULT,LABEL_LEN_DEFAULT]; // pointer exactly on the track
  let angle=Math.atan(dy/Math.abs(dx))*180/Math.PI;
  if(dx<0) angle=360-angle+180;
  if(angle>360) angle-=360;
  if(angle<0) angle+=360;
  let dist=Math.sqrt(dy*dy+dx*dx)-5;
  let dir=Math.round(angle/360*16)%16;
  let len=Math.min(2,Math.max(0,Math.round(dist/35)));
  return[dir,len];
}

// The Air window paints tracks grouped by HMI state: every symbol of a
// group, then its labels. The Ground window (no State) paints each track's
// symbol and label together.
function drawTracks(){
  let groups=new Map();
  for(let ac of aircraft){
    if(ac.State==null){drawTrackSymbols(ac);drawTrackLabel(ac);continue;}
    if(!groups.has(ac.State)) groups.set(ac.State,[]);
    groups.get(ac.State).push(ac);
  }
  for(let list of groups.values()){
    for(let ac of list) drawTrackSymbols(ac);
    for(let ac of list) drawTrackLabel(ac);
  }
}

function drawTrackSymbols(ac){
  let p=latLonToCanvas(ac.Lat,ac.Lon);
  ac._p=p;
  // Ground: a track whose position is outside the window is not painted at all
  ac._hidden=!!ac.Clip&&(p[0]<0||p[1]<0||p[0]>=W||p[1]>=H);
  if(ac._hidden) return;
  let col=rgb(ac.Col);

  // History trail
  if(ac.History){
    for(let h of ac.History){
      let hp=latLonToCanvas(h[0],h[1]);
      drawSymbol('HistoryMarker',ac.HistSsr,hp[0],hp[1],h[2],col,h[0],h[1]);
    }
  }

  // Velocity vector: 1-minute segments (5-minute once the setting is above 5),
  // each a great-circle step along the track, a VelocityMarker between them
  if(ac.Vv>0){
    let segMin=ac.Vv>5?5:1;
    let segNm=ac.Gs/60*segMin;
    let ll=[ac.Lat,ac.Lon],prev=p;
    ctx.strokeStyle=col;ctx.lineWidth=1;ctx.setLineDash([]);
    for(let m=segMin;m<=ac.Vv;m+=segMin){
      ll=fromBearingRange(ll[0],ll[1],segNm,ac.Trk);
      let np=latLonToCanvas(ll[0],ll[1]);
      ctx.beginPath();ctx.moveTo(prev[0],prev[1]);ctx.lineTo(np[0],np[1]);ctx.stroke();
      if(m<ac.Vv) drawSymbol('VelocityMarker',ac.HistSsr,np[0],np[1],ac.Trk,col,ll[0],ll[1]);
      prev=np;
    }
  }

  // Symbols; an identing symbol is painted only on the flash phase
  for(let s of (ac.Syms||[])){
    if(s.Ident&&!flashOn) continue;
    drawSymbol(s.Type,s.Ssr,p[0],p[1],s.Hdg,s.Col?rgb(s.Col):col,ac.Lat,ac.Lon);
  }

  // This page's own marker for the track whose label is being moved
  if(labelMovingCS===ac.Id) drawSymbol('Selection',-1,p[0],p[1],null,col);
}

// Port of the layout half of PaintLabel: a box of Width x Rows character
// cells hung off the leader end, each line's cells placed by GetItemRectF.
function drawTrackLabel(ac){
  let lb=ac.Label;
  if(!lb||ac._hidden) return;
  if(lb.Blink&&flashOn) return; // handover-in / post-jurisdiction labels blink
  let p=ac._p||latLonToCanvas(ac.Lat,ac.Lon);

  let st=labelState[ac.Id];
  let dir=st?st.dir:lb.Dir,len=st?st.len:lb.Len;
  if(labelMovingCS===ac.Id&&labelMovePreview){dir=labelMovePreview[0];len=labelMovePreview[1];}

  let fw=labelFontW||Math.round(labelFontSize/2),fh=labelFontH||labelFontSize;
  // CalculateLabelLocation: whole pixels, measured from the window centre
  let reach=len*35+5,ang=dir*22.5*D2R;
  let lx=W/2+Math.trunc(p[0]-W/2+reach*Math.cos(ang));
  let ly=H/2+Math.trunc(p[1]-H/2+reach*Math.sin(ang));
  let west=dir>4&&dir<12;
  let bw=lb.Width*fw,bh=lb.Rows*fh;
  let bx=lx-(west?bw:0),by=ly-lb.AnchorRow*fh;
  if(bx>W||bx+bw<0||by>H||by+bh<0) return; // label box outside the window

  ctx.setLineDash([]);ctx.lineWidth=1;
  ctx.strokeStyle=rgb(lb.Col);
  ctx.beginPath();ctx.moveTo(p[0],p[1]);ctx.lineTo(lx,ly);ctx.stroke();

  ctx.font='bold '+labelFontSize+""px 'Terminus','Consolas',monospace"";
  ctx.textAlign='left';ctx.textBaseline='middle';
  let lines=(lb.Shared&&timeshareOn)?lb.Shared:lb.Lines;
  for(let i=0;i<lines.length;i++){
    let line=lines[i],y=by+i*fh;
    for(let cell of line.Cells){
      let cw=cell.W*fw;
      let cx=(line.J&&west)?(bx+bw-cell.X*fw-cw):(bx+cell.X*fw);
      if(west&&cx+cw>bx+bw) cx=bx+bw-cw;
      if(cell.Back){ctx.fillStyle=rgb(cell.Back);ctx.fillRect(cx,y,cw,fh);}
      for(let run of cell.Runs){
        let rx=cx+Math.trunc(run.Dx*fw)+run.Px,ry=y+run.Dy*fh;
        // DrawCharacters: one character per cell, centred vertically in the row
        ctx.fillStyle=rgb(run.C);
        for(let k=0;k<run.T.length;k++) ctx.fillText(run.T[k],rx+k*fw,ry-1+fh/2);
        if(run.B){
          let tw=run.T.length*fw;
          ctx.strokeStyle=rgb(run.BC||run.C);
          ctx.beginPath();
          if(run.B&1){ctx.moveTo(rx,ry);ctx.lineTo(rx,ry+fh);}
          if(run.B&2){ctx.moveTo(rx+tw,ry);ctx.lineTo(rx+tw,ry+fh);}
          if(run.B&4){ctx.moveTo(rx,ry);ctx.lineTo(rx+tw,ry);}
          if(run.B&8){ctx.moveTo(rx,ry+fh);ctx.lineTo(rx+tw,ry+fh);}
          ctx.stroke();
        }
      }
    }
  }

  // Alert box around the label
  if(lb.Box){
    ctx.strokeStyle=rgb(lb.Box);
    ctx.strokeRect(bx-1,by,lb.BoxWidth*fw+2,bh);
  }
}

/* ============================================================
   Label dragging
   ============================================================ */
let tapStartPos=null;

function findAircraftAt(cx,cy){
  for(let ac of aircraft){
    let p=latLonToCanvas(ac.Lat,ac.Lon);
    if(Math.abs(p[0]-cx)<12&&Math.abs(p[1]-cy)<12) return ac;
  }
  return null;
}

cv.addEventListener('contextmenu',e=>{e.preventDefault();});

cv.addEventListener('pointerup',e=>{
  if(pointers.size>1) return;
  let rect=cv.getBoundingClientRect();
  let ux=e.clientX-rect.left,uy=e.clientY-rect.top;

  if(labelMovingCS){
    let ac=aircraft.find(a=>a.Id===labelMovingCS);
    if(ac){
      let p=latLonToCanvas(ac.Lat,ac.Lon);
      let ori=getLabelOrientation(p[0],p[1],ux,uy);
      if(!labelState[labelMovingCS]) labelState[labelMovingCS]={dir:LABEL_DIR_DEFAULT,len:LABEL_LEN_DEFAULT};
      labelState[labelMovingCS].dir=ori[0];
      labelState[labelMovingCS].len=ori[1];
    }
    labelMovingCS=null;labelMovePreview=null;
    scheduleRender();
    return;
  }

  if(tapStartPos){
    let dx=Math.abs(e.clientX-tapStartPos.x),dy=Math.abs(e.clientY-tapStartPos.y);
    if(dx<6&&dy<6){
      let ac=findAircraftAt(ux,uy);
      if(ac&&ac.Id){
        labelMovingCS=ac.Id;
        labelMovePreview=null;
        scheduleRender();
      }
    }
    tapStartPos=null;
  }
});

/* ============================================================
   Resize
   ============================================================ */
function resize(){
  dpr=window.devicePixelRatio||1;
  let rect=cv.getBoundingClientRect();
  W=rect.width;H=rect.height;
  cv.width=W*dpr;cv.height=H*dpr;
  render();
}
window.addEventListener('resize',resize);

/* ============================================================
   Touch / Mouse - pan, pinch-zoom, scroll-zoom
   Panning and zooming move the screen centre, keeping the point
   under the finger or cursor where it is.
   ============================================================ */
let pointers=new Map(),panLast=null,pinchLast=null;
function canvasPoint(x,y){let r=cv.getBoundingClientRect();return[x-r.left,y-r.top];}
cv.addEventListener('pointerdown',e=>{
  cv.setPointerCapture(e.pointerId);
  pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
  tapStartPos={x:e.clientX,y:e.clientY};
  if(labelMovingCS){pinchLast=null;panLast=null;return;} // suppress pan during label move
  if(pointers.size===1){panLast={x:e.clientX,y:e.clientY};pinchLast=null;}
  else if(pointers.size===2){panLast=null;let pts=[...pointers.values()];pinchLast={dist:Math.hypot(pts[1].x-pts[0].x,pts[1].y-pts[0].y),mx:(pts[0].x+pts[1].x)/2,my:(pts[0].y+pts[1].y)/2};}
});
cv.addEventListener('pointermove',e=>{
  if(!pointers.has(e.pointerId)) return;
  pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
  // Label move preview
  if(labelMovingCS&&pointers.size===1){
    let m=canvasPoint(e.clientX,e.clientY);
    let ac=aircraft.find(a=>a.Id===labelMovingCS);
    if(ac){let p=latLonToCanvas(ac.Lat,ac.Lon);labelMovePreview=getLabelOrientation(p[0],p[1],m[0],m[1]);scheduleRender();}
    return;
  }
  if(pointers.size===1&&panLast){
    let pt=[...pointers.values()][0];
    let from=canvasPoint(panLast.x,panLast.y),to=canvasPoint(pt.x,pt.y);
    anchorAt(canvasToLatLon(from[0],from[1]),to[0],to[1]);
    panLast={x:pt.x,y:pt.y};scheduleRender();
  }else if(pointers.size===2&&pinchLast){
    let pts=[...pointers.values()];
    let dist=Math.hypot(pts[1].x-pts[0].x,pts[1].y-pts[0].y);
    let mx=(pts[0].x+pts[1].x)/2,my=(pts[0].y+pts[1].y)/2;
    if(pinchLast.dist>10&&dist>0){
      let from=canvasPoint(pinchLast.mx,pinchLast.my),to=canvasPoint(mx,my);
      let ll=canvasToLatLon(from[0],from[1]);
      nmRange=clampRange(nmRange*pinchLast.dist/dist);
      anchorAt(ll,to[0],to[1]);
    }
    pinchLast={dist:dist,mx:mx,my:my};scheduleRender();
  }
});
function pointerGone(e){
  pointers.delete(e.pointerId);
  if(pointers.size===1){let pt=[...pointers.values()][0];panLast={x:pt.x,y:pt.y};pinchLast=null;}
  else if(pointers.size===0){panLast=null;pinchLast=null;}
}
cv.addEventListener('pointerup',pointerGone);
cv.addEventListener('pointercancel',pointerGone);
cv.addEventListener('wheel',e=>{
  e.preventDefault();
  let m=canvasPoint(e.clientX,e.clientY);
  let ll=canvasToLatLon(m[0],m[1]);
  nmRange=clampRange(nmRange*(e.deltaY>0?1.15:0.87));
  anchorAt(ll,m[0],m[1]);
  scheduleRender();
},{passive:false});
cv.addEventListener('dblclick',e=>{e.preventDefault();resetView();labelMovingCS=null;labelMovePreview=null;scheduleRender();});

/* ============================================================
   Menu system
   ============================================================ */
const btnPos=document.getElementById('btn-pos'),ddPos=document.getElementById('dd-pos');
const btnMaps=document.getElementById('btn-maps'),ddMaps=document.getElementById('dd-maps');
const btnTools=document.getElementById('btn-tools'),ddTools=document.getElementById('dd-tools');
let openDD=null;

function closeAllDD(){
  document.querySelectorAll('.dropdown').forEach(d=>d.classList.remove('show'));
  document.querySelectorAll('.menu-btn').forEach(b=>b.classList.remove('active'));
  openDD=null;
}
function toggleDD(btn,dd){
  if(openDD===dd){closeAllDD();return;}
  closeAllDD();dd.classList.add('show');btn.classList.add('active');openDD=dd;
}
btnPos.addEventListener('click',e=>{e.stopPropagation();toggleDD(btnPos,ddPos);});
btnMaps.addEventListener('click',e=>{e.stopPropagation();toggleDD(btnMaps,ddMaps);});
btnTools.addEventListener('click',e=>{e.stopPropagation();toggleDD(btnTools,ddTools);});
document.addEventListener('click',e=>{
  if(!openDD) return;
  let inMenubar=document.getElementById('menubar').contains(e.target);
  if(inMenubar) return;
  closeAllDD();
});

/* ============================================================
   Position menu (drill-down navigation)
   ============================================================ */
function buildPositionMenu(groupFilter){
  ddPos.innerHTML='';

  if(groupFilter){
    // Show back button + items in this group
    let back=document.createElement('div');back.className='dd-item dd-back';
    let chkB=document.createElement('span');chkB.className='chk';chkB.textContent='\u25C0';
    back.appendChild(chkB);back.appendChild(document.createTextNode('Back'));
    back.addEventListener('click',e=>{e.stopPropagation();buildPositionMenu(null);});
    ddPos.appendChild(back);

    let groups={};
    positions.forEach(p=>{if(p.Group&&p.Group.trim()){if(!groups[p.Group])groups[p.Group]=[];groups[p.Group].push(p);}});
    let items=(groups[groupFilter]||[]).sort((a,b)=>a.Name.localeCompare(b.Name));
    items.forEach(p=>ddPos.appendChild(mkPosItem(p)));
    return;
  }

  let freeItem=mkItem('\u2713',selectedPos===null,'Free');
  freeItem.addEventListener('click',()=>{selectPosition(null);closeAllDD();});
  ddPos.appendChild(freeItem);

  let groups={},ungrouped=[];
  positions.forEach(p=>{
    if(p.Group&&p.Group.trim()){if(!groups[p.Group])groups[p.Group]=[];groups[p.Group].push(p);}
    else ungrouped.push(p);
  });
  let groupNames=Object.keys(groups).sort();
  for(let gn of groupNames){
    let trigger=document.createElement('div');trigger.className='dd-item dd-group';
    let chkSpan=document.createElement('span');chkSpan.className='chk';chkSpan.textContent='';
    trigger.appendChild(chkSpan);trigger.appendChild(document.createTextNode(gn));
    trigger.addEventListener('click',(function(name){return function(e){e.stopPropagation();buildPositionMenu(name);};})(gn));
    ddPos.appendChild(trigger);
  }
  ungrouped.sort((a,b)=>a.Name.localeCompare(b.Name));
  ungrouped.forEach(p=>ddPos.appendChild(mkPosItem(p)));
}

function mkPosItem(p){
  let item=mkItem('\u2713',selectedPos&&selectedPos.Name===p.Name,p.Name);
  item.addEventListener('click',e=>{e.stopPropagation();selectPosition(p);closeAllDD();});
  return item;
}

function mkItem(chkChar,checked_,text){
  let item=document.createElement('div');item.className='dd-item';
  let chk=document.createElement('span');chk.className='chk';
  chk.textContent=checked_?chkChar:'';
  item.appendChild(chk);item.appendChild(document.createTextNode(text));
  return item;
}

/* ============================================================
   Maps menu (drill-down navigation)
   ============================================================ */
function buildMapsMenu(folderFilter){
  ddMaps.innerHTML='';
  if(!allAsdMaps||allAsdMaps.length===0) return;

  let folders={};
  for(let m of allAsdMaps){
    let f=m.Folder||'';
    if(!folders[f]) folders[f]=[];
    folders[f].push(m);
  }

  if(folderFilter){
    // Show back button + items in this folder
    let back=document.createElement('div');back.className='dd-item dd-back';
    let chkB=document.createElement('span');chkB.className='chk';chkB.textContent='\u25C0';
    back.appendChild(chkB);back.appendChild(document.createTextNode('Back'));
    back.addEventListener('click',e=>{e.stopPropagation();buildMapsMenu(null);});
    ddMaps.appendChild(back);

    let maps=(folders[folderFilter]||[]).sort((a,b)=>a.Name.localeCompare(b.Name));
    for(let m of maps) ddMaps.appendChild(makeMapItem(m));
    return;
  }

  let folderNames=Object.keys(folders).sort();
  for(let fn of folderNames){
    let maps=folders[fn];
    maps.sort((a,b)=>a.Name.localeCompare(b.Name));
    if(fn===''){
      for(let m of maps) ddMaps.appendChild(makeMapItem(m));
    }else{
      let trigger=document.createElement('div');trigger.className='dd-item dd-group';
      trigger.appendChild(document.createTextNode(fn));
      trigger.addEventListener('click',(function(name){return function(e){e.stopPropagation();buildMapsMenu(name);};})(fn));
      ddMaps.appendChild(trigger);
    }
  }
}

function makeMapItem(meta){
  let key=layerKey(meta.Folder,meta.Name);
  let ls=layerState[key];
  let visible=ls?ls.visible:false;
  let item=document.createElement('div');item.className='dd-item';
  let chk=document.createElement('span');chk.className='chk';
  chk.textContent=visible?'\u2713':'';
  item.appendChild(chk);item.appendChild(document.createTextNode(meta.Name));
  item.addEventListener('click',e=>{
    e.stopPropagation();
    toggleMapLayer(key,meta,chk);
  });
  return item;
}

/* ============================================================
   On-demand map layer loading
   ============================================================ */
function toggleMapLayer(key,meta,chkEl){
  if(!layerState[key]) layerState[key]={visible:false,layer:null};
  let ls=layerState[key];
  ls.visible=!ls.visible;
  chkEl.textContent=ls.visible?'\u2713':'';
  // Centre on map's own center when enabling, if no position selected
  if(ls.visible&&!selectedPos&&meta.CenterLat&&meta.CenterLon){
    setHome(meta.CenterLat,meta.CenterLon,nmRange);
  }
  if(ls.visible&&!ls.layer){
    loadLayerGeometry(key).then(()=>scheduleRender());
  }else{
    scheduleRender();
  }
}

function loadLayerGeometry(key){
  return api('/api/asd/maplayers?maps='+encodeURIComponent(key))
    .then(layers=>{
      if(Array.isArray(layers)&&layers.length>0){
        let lk=layerKey(layers[0].Folder,layers[0].Name);
        if(layerState[lk]) layerState[lk].layer=layers[0];
      }
    }).catch(()=>{});
}

function loadMultipleLayers(keys){
  if(keys.length===0) return Promise.resolve();
  return api('/api/asd/maplayers?maps='+encodeURIComponent(keys.join(',')))
    .then(layers=>{
      if(!Array.isArray(layers)) return;
      for(let layer of layers){
        let key=layerKey(layer.Folder,layer.Name);
        if(layerState[key]) layerState[key].layer=layer;
      }
    }).catch(()=>{});
}

/* ============================================================
   Position selection
   ============================================================ */
let selectSeq=0;
function selectPosition(pos){
  let seq=++selectSeq; // a slow layer load must not act for a superseded selection
  selectedPos=pos;
  if(acTimer){clearInterval(acTimer);acTimer=null;}
  aircraft=[];

  if(!pos){
    document.title='ASD';
    for(let k in layerState) layerState[k].visible=false;
    setHome(-27,153,200);
    buildPositionMenu();buildMapsMenu();render();
    return;
  }

  document.title='ASD: '+pos.Name;
  setHome(pos.CenterLat,pos.CenterLon,pos.DefaultRange||200);

  for(let k in layerState) layerState[k].visible=false;
  let checkedKeys=[];
  if(pos.MapRefs){
    for(let ref of pos.MapRefs){
      let key=ref;
      if(!layerState[key]) layerState[key]={visible:false,layer:null};
      layerState[key].visible=true;
      if(!layerState[key].layer) checkedKeys.push(key);
    }
  }

  buildPositionMenu();
  buildMapsMenu();

  if(checkedKeys.length>0){
    loadMultipleLayers(checkedKeys).then(()=>{
      if(seq!==selectSeq) return;
      scheduleRender();
      startAircraftPolling();
    });
  }else{
    scheduleRender();
    startAircraftPolling();
  }
}

/* ============================================================
   API
   ============================================================ */
// Rejects on HTTP errors so an error body is never mistaken for data
function api(path){
  return fetch(path).then(r=>{
    if(!r.ok) throw new Error('HTTP '+r.status);
    return r.json();
  });
}

function loadPositions(){
  api('/api/asd/positions').then(data=>{
    positions=Array.isArray(data)?data:[];
    buildPositionMenu();
  }).catch(()=>{});
}

let mapListRetries=0;
function loadAsdMaps(){
  api('/api/asd/maps').then(data=>{
    allAsdMaps=data.Maps||[];
    colours=data.Colours||null;
    applyTheme();
    buildHatch();
    for(let m of allAsdMaps){
      let key=layerKey(m.Folder,m.Name);
      if(!layerState[key]) layerState[key]={visible:false,layer:null};
    }
    buildMapsMenu();
    scheduleRender();
    // The map list is empty until vatSys has finished loading the profile's maps
    if(allAsdMaps.length===0&&mapListRetries++<20) setTimeout(loadAsdMaps,3000);
  }).catch(()=>{});
}

function loadSymbols(){
  api('/api/symbols').then(data=>{
    if(data.Symbols&&data.Symbols.length>0) buildSymTable(data.Symbols);
    if(data.LabelFontSize) labelFontSize=data.LabelFontSize;
    if(data.MapFontSize) mapFontSize=data.MapFontSize;
    labelFontW=data.LabelFontWidth||0;labelFontH=data.LabelFontHeight||0;
    mapFontW=data.MapFontWidth||0;mapFontH=data.MapFontHeight||0;
    scheduleRender();
  }).catch(()=>{});
}

// Runtime dynamic maps: controlled-sector infill and active restricted areas
// (yellow PRD outlines). These change as sectors are taken/released, so refresh
// every 2 seconds.
function loadDynamicMaps(){
  api('/api/asd/dynamicmaps').then(data=>{
    dynamicBelow=data&&Array.isArray(data.Below)?data.Below:[];
    dynamicAbove=data&&Array.isArray(data.Above)?data.Above:[];
    scheduleRender();
  }).catch(()=>{});
}

function startAircraftPolling(){
  if(acTimer){clearInterval(acTimer);acTimer=null;}
  function poll(){
    // Query around the screen centre with a range covering the whole viewport,
    // so tracks never vanish when zooming/panning
    if(!(W>0)) return; // not laid out yet (hidden tab)
    let rng=Math.ceil(nmRange*0.5*Math.max(1,H/W)*1.3)+5;
    api('/api/asd/aircraft?lat='+centerLat.toFixed(5)+'&lon='+centerLon.toFixed(5)+'&range='+rng).then(data=>{
      aircraft=Array.isArray(data)?data:[];scheduleRender();
    }).catch(()=>{});
  }
  poll();
  acTimer=setInterval(poll,500);
}

/* Init */
resize();
loadPositions();
loadAsdMaps();
loadSymbols();
loadDynamicMaps();
setInterval(loadDynamicMaps,2000);
</script>
</body>
</html>";
}
