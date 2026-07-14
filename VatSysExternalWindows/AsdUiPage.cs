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
.dd-sep{height:1px;background:rgb(120,128,128);margin:1px 2px;}
.dd-back{font-weight:bold;border-bottom:1px solid rgb(120,128,128);}
.dd-group>.dd-item::after{content:'\25B6';margin-left:auto;padding-left:14px;font-size:9px;display:inline-block;}
canvas{flex:1;display:block;touch-action:none;}
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
let vvMinutes=0;

let centerLat=-27,centerLon=153,nmRange=200;
let panX=0,panY=0;

let layerState={};
let aircraft=[];
let acTimer=null;
let labelFontSize=18,mapFontSize=16;
let flashOn=true,flashTimer=null;
let transAlt=10000;
let dynamicLayers=[];

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
  symTable={};
  for(let d of defs) symTable[symKey(d.Type,d.SpecialCode)]=d;
}
// Fallback: Australia profile defaults, used until /api/symbols loads
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
function drawSymbol(type,ssr,x,y,headingDeg,col){
  let def=getSymbol(type,ssr);
  if(!def) return;
  ctx.save();
  ctx.translate(x,y);
  if(def.Rotate&&headingDeg!=null&&headingDeg!==-1)
    ctx.rotate(headingDeg*Math.PI/180);
  let filled=def.StrokeWidth===0;
  for(let sh of def.Shapes){
    if(!traceShape(sh,filled)) continue;
    if(filled){ctx.fillStyle=col;ctx.fill();}
    else{ctx.strokeStyle=col;ctx.lineWidth=def.StrokeWidth;ctx.stroke();}
  }
  ctx.restore();
}

/* ============================================================
   Coordinate conversion (no rotation for ASD)
   ============================================================ */
const NM_TO_DEG_LAT=1/60;
function nmToDegLon(lat){return 1/(60*Math.cos(lat*Math.PI/180));}
function latLonToCanvas(lat,lon){
  let dlat=lat-centerLat,dlon=lon-centerLon;
  let xNm=dlon/nmToDegLon(centerLat),yNm=dlat/NM_TO_DEG_LAT;
  // vatSys SetZoom: DefaultRange spans the full window WIDTH in NM
  let scale=W/nmRange;
  return[W/2+panX+xNm*scale,H/2+panY-yNm*scale];
}

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

function trackColour(ac){
  // Matches ASDControlDX.SelectASDBrush HMI state switch (note: Preactive
  // tracks paint in the NonJurisdiction colour on the ASD)
  if(!colours) return'rgb(0,0,0)';
  let st=(ac.State||'').toLowerCase();
  if(st==='jurisdiction') return rgb(colours.Jurisdiction);
  if(st==='ghostjurisdiction') return rgb(colours.GhostJurisdiction||colours.Jurisdiction);
  if(st==='announced') return rgb(colours.Announced);
  if(st==='handoverin'||st==='handoverout') return rgb(colours.Handover);
  if(st==='postjurisdiction') return rgb(colours.PostJurisdiction||colours.Jurisdiction);
  return rgb(colours.NonJurisdiction);
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

  // Controlled-sector infill ('Dynamic Infill') paints beneath the file maps;
  // active restricted areas (yellow PRD outlines + hatched infills) paint on top
  for(let dl of dynamicLayers) if(dl.Name==='Dynamic Infill') drawMapLayer(dl);
  for(let ls of visibleLayers) drawMapLayer(ls.layer);
  for(let dl of dynamicLayers) if(dl.Name!=='Dynamic Infill') drawMapLayer(dl);

  for(let ac of aircraft) drawAircraft(ac);
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

    // Map labels — map's own colour, ASDMapFont
    ctx.fillStyle=col;
    ctx.font='bold '+mapFontSize+""px 'Terminus','Consolas',monospace"";
    ctx.textAlign='center';ctx.textBaseline='middle';
    for(let lb of layer.Labels){let p=latLonToCanvas(lb.Lat,lb.Lon);ctx.fillText(lb.Text,p[0],p[1]);}

    // Map symbols (navaid dots, etc.)
    if(layer.Symbols&&layer.Symbols.length>0){
      ctx.fillStyle=col;ctx.strokeStyle=col;ctx.lineWidth=1;
      for(let sym of layer.Symbols){
        let sp=latLonToCanvas(sym.Lat,sym.Lon);
        let st=(sym.SymbolType||'').toLowerCase();
        if(st==='solidsquare'){
          ctx.fillRect(sp[0]-2,sp[1]-2,4,4);
        }else if(st==='hollowsquare'){
          ctx.strokeRect(sp[0]-2,sp[1]-2,4,4);
        }else if(st==='hollowtriangle'){
          ctx.beginPath();ctx.moveTo(sp[0],sp[1]-3);ctx.lineTo(sp[0]+3,sp[1]+2);ctx.lineTo(sp[0]-3,sp[1]+2);ctx.closePath();ctx.stroke();
        }else{
          // DotFillCircle or default - filled circle
          ctx.beginPath();ctx.arc(sp[0],sp[1],2,0,Math.PI*2);ctx.fill();
        }
      }
    }
}

/* ============================================================
   Aircraft rendering — ASD style
   Symbol: triangle for most, stroked (outline).
   Velocity vector: line in heading direction.
   Label: Quicktag format (ACID / ALT GS).
   Leader line from symbol to label.
   History dots at past positions.
   ============================================================ */
const LABEL_DIR_DEFAULT=1,LABEL_LEN_DEFAULT=1;
let labelState={};
let labelMovingCS=null;
let labelMovePreview=null;

function getLabelDir(cs){return labelState[cs]?labelState[cs].dir:LABEL_DIR_DEFAULT;}
function getLabelLen(cs){return labelState[cs]?labelState[cs].len:LABEL_LEN_DEFAULT;}

function getLabelOrientation(tx,ty,mx,my){
  let dy=my-ty,dx=mx-tx;
  let angle=Math.atan(dy/Math.abs(dx))*180/Math.PI;
  if(dx<0) angle=360-angle+180;
  if(angle>360) angle-=360;
  if(angle<0) angle+=360;
  let dist=Math.sqrt(dy*dy+dx*dx)-5;
  let dir=Math.round(angle/360*16)%16;
  let len=Math.min(2,Math.max(0,Math.round(dist/35)));
  return[dir,len];
}

const RT_PRI=1,RT_MODEC=2,RT_MODES=4,RT_ADSB=8,RT_SMRADSB=32;

/* Label item text — exact vatSys PaintLabel formats */
// ACID: FDR callsign when coupled; uncoupled ADS-B sources broadcast the
// callsign, SSR-only uncoupled tracks show the squawk (4-digit octal)
function acidText(ac){
  if(ac.Coupled) return ac.Callsign||'';
  let flags=ac.RadarFlags||0;
  if(flags&(RT_ADSB|RT_SMRADSB)) return ac.Callsign||'';
  if((flags&(RT_MODEC|RT_MODES))&&ac.Ssr!=null&&ac.Ssr>=0)
    return ac.Ssr.toString(8).padStart(4,'0');
  return ac.Callsign||'';
}
// LEVEL: CorrectedAltitude/100 as D3 (integer truncation, like C#)
function levelText(alt){
  return alt!=null&&alt>=0?String(Math.trunc(alt/100)).padStart(3,'0'):'';
}
// GROUNDSPEED: '++' outside 0..995, else round(gs/10) 2-padded
function gsText(gs){
  if(gs==null) return'';
  return (gs>995||gs<0)?'++':String(Math.round(gs/10)).padStart(2,'0');
}

function drawAircraft(ac){
  let p=latLonToCanvas(ac.Lat,ac.Lon);
  let col=trackColour(ac);
  let flags=ac.RadarFlags||0;
  let ssr=ac.Ssr!=null?ac.Ssr:-1;

  // History trail: HistoryMarker symbol at past positions
  if(ac.History&&ac.History.length>0){
    for(let h of ac.History){
      let hp=latLonToCanvas(h[0],h[1]);
      drawSymbol('HistoryMarker',-1,hp[0],hp[1],null,col);
    }
  }

  // Velocity vector — exact PaintASDTrack algorithm: segments of min(5,VV)
  // minutes along the track heading, VelocityMarker at intermediate points
  if(vvMinutes>0&&ac.Gs>0&&ac.Hdg!==-1){
    let segMin=Math.min(5,vvMinutes);
    let segNm=ac.Gs/60*segMin;
    let hdgRad=ac.Hdg*Math.PI/180;
    let cosH=Math.cos(hdgRad),sinH=Math.sin(hdgRad);
    let prevLat=ac.Lat,prevLon=ac.Lon,prev=p;
    ctx.strokeStyle=col;ctx.lineWidth=1;
    for(let m=segMin;m<=vvMinutes;m+=segMin){
      let nLat=prevLat+cosH*segNm/60;
      let nLon=prevLon+sinH*segNm/(60*Math.cos(prevLat*Math.PI/180));
      let np=latLonToCanvas(nLat,nLon);
      ctx.beginPath();ctx.moveTo(prev[0],prev[1]);ctx.lineTo(np[0],np[1]);ctx.stroke();
      if(m<vvMinutes) drawSymbol('VelocityMarker',-1,np[0],np[1],null,col);
      prevLat=nLat;prevLon=nLon;prev=np;
    }
  }

  // Symbol selection — exact port of vatSys PaintASDTrack:
  // ModeS(4) → ModeS, else ModeC(2) → ModeA, else ADSB(8) → ADSB.
  // Coasting → CoastADSB/CoastRadar. Identing symbols blink in IdentFlash.
  // A PRI return overlays the Primary cross on the SSR symbol.
  let ssrSym=null;
  if(flags&RT_MODES) ssrSym='ModeS';
  else if(flags&RT_MODEC) ssrSym='ModeA';
  else if(flags&RT_ADSB) ssrSym='ADSB';

  if(ac.Coasting){
    drawSymbol(ssrSym==='ADSB'?'CoastADSB':'CoastRadar',ssr,p[0],p[1],ac.Hdg,col);
  }else if(ssrSym){
    if(!ac.Ident||flashOn){
      let symCol=ac.Ident?(colours&&colours.IdentFlash?rgb(colours.IdentFlash):'#fff'):col;
      drawSymbol(ssrSym,ssr,p[0],p[1],ac.Hdg,symCol);
      if(flags&RT_PRI) drawSymbol('Primary',ssr,p[0],p[1],ac.Hdg,symCol);
    }
  }else if(flags&RT_PRI){
    drawSymbol('Primary',-1,p[0],p[1],ac.Hdg,col);
  }

  // Selection octagon on the track being label-moved (vatSys Selection symbol)
  let cs=ac.Callsign||'';
  if(labelMovingCS===cs) drawSymbol('Selection',-1,p[0],p[1],null,col);

  // Label content — vatSys label templates: coupled tracks get the Normal
  // label (RWY / ACID WAKE / LEVEL VI CFL GS), uncoupled tracks get the
  // Limited label (ACID / LEVEL as FLnnn above transition, 5-digit feet below)
  let lines=[];
  if(ac.Coupled){
    if(ac.Runway) lines.push(ac.Runway);
    let l2=ac.Callsign||'';
    if(ac.WakeCat) l2+=(l2?' ':'')+ac.WakeCat;
    if(l2) lines.push(l2);
    let l3=levelText(ac.Alt);
    // vertical indicator: vatSys Terminus glyphs (® climb / ¬ descend /
    // '>' level, threshold 300fpm), suppressed while coasting
    if(!ac.Coasting){
      if(ac.VSpd>300) l3+='®';
      else if(ac.VSpd<-300) l3+='¬';
      else l3+='>';
    }
    // CFL: 3-digit, 'VSA' for the visual-approach sentinel (2), hidden when -1
    if(ac.CFL!=null&&ac.CFL!==-1)
      l3+=(ac.CFL===2?'VSA':String(Math.trunc(ac.CFL/100)).padStart(3,'0'));
    let g=gsText(ac.Gs);
    if(g) l3+=(l3?' ':'')+g;
    if(l3) lines.push(l3);
  }else{
    let a=acidText(ac);
    if(a) lines.push(a);
    if(ac.Alt!=null&&ac.Alt>=0){
      lines.push(ac.Alt>transAlt
        ?'FL'+String(Math.trunc(ac.Alt/100)).padStart(3,'0')
        :String(ac.Alt).padStart(5,'0'));
    }
  }
  if(lines.length===0) return;

  let ldir=getLabelDir(cs);
  let llen=getLabelLen(cs);

  if(labelMovingCS===cs&&labelMovePreview){
    ldir=labelMovePreview[0];llen=labelMovePreview[1];
  }

  let lAngle=ldir*22.5;
  let leaderPx=llen*35+5;
  let rad=lAngle*Math.PI/180;
  let lx=p[0]+leaderPx*Math.cos(rad),ly=p[1]+leaderPx*Math.sin(rad);

  // Leader line
  ctx.strokeStyle=col;ctx.lineWidth=1;
  ctx.beginPath();ctx.moveTo(p[0],p[1]);ctx.lineTo(lx,ly);ctx.stroke();

  // Label — ASDMainFont (default eurofont_sml = 18px Terminus bold)
  ctx.fillStyle=col;
  ctx.font='bold '+labelFontSize+""px 'Terminus','Consolas',monospace"";
  let fontH=labelFontSize;
  let isWest=ldir>4&&ldir<12;
  ctx.textAlign=isWest?'right':'left';
  ctx.textBaseline='top';

  for(let i=0;i<lines.length;i++) ctx.fillText(lines[i],lx,ly+i*fontH);
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
    let ac=aircraft.find(a=>(a.Callsign||'')===labelMovingCS);
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
      if(ac&&ac.Callsign){
        labelMovingCS=ac.Callsign;
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
   ============================================================ */
let pointers=new Map(),panLast=null,pinchLast=null;
cv.addEventListener('pointerdown',e=>{
  cv.setPointerCapture(e.pointerId);
  pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
  tapStartPos={x:e.clientX,y:e.clientY};
  if(labelMovingCS){pinchLast=null;panLast=null;return;}
  if(pointers.size===1){panLast={x:e.clientX,y:e.clientY};pinchLast=null;}
  else if(pointers.size===2){panLast=null;let pts=[...pointers.values()];pinchLast={dist:Math.hypot(pts[1].x-pts[0].x,pts[1].y-pts[0].y),mx:(pts[0].x+pts[1].x)/2,my:(pts[0].y+pts[1].y)/2};}
});
cv.addEventListener('pointermove',e=>{
  if(!pointers.has(e.pointerId)) return;
  pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
  if(labelMovingCS&&pointers.size===1){
    let rect=cv.getBoundingClientRect();
    let mx=e.clientX-rect.left,my=e.clientY-rect.top;
    let ac=aircraft.find(a=>(a.Callsign||'')===labelMovingCS);
    if(ac){let p=latLonToCanvas(ac.Lat,ac.Lon);labelMovePreview=getLabelOrientation(p[0],p[1],mx,my);scheduleRender();}
    return;
  }
  if(pointers.size===1&&panLast){
    let pt=[...pointers.values()][0];
    panX+=pt.x-panLast.x;panY+=pt.y-panLast.y;
    panLast={x:pt.x,y:pt.y};scheduleRender();
  }else if(pointers.size===2&&pinchLast){
    let pts=[...pointers.values()];
    let dist=Math.hypot(pts[1].x-pts[0].x,pts[1].y-pts[0].y);
    let mx=(pts[0].x+pts[1].x)/2,my=(pts[0].y+pts[1].y)/2;
    if(pinchLast.dist>10){
      let sf=pinchLast.dist/dist;
      let rect=cv.getBoundingClientRect();
      let cx=pinchLast.mx-rect.left-W/2,cy=pinchLast.my-rect.top-H/2;
      panX=cx+(panX-cx)/sf+(mx-pinchLast.mx);
      panY=cy+(panY-cy)/sf+(my-pinchLast.my);
      nmRange*=sf;nmRange=Math.max(1,Math.min(5000,nmRange));
    }
    pinchLast={dist:dist,mx:mx,my:my};scheduleRender();
  }
});
cv.addEventListener('pointerup',e=>{
  pointers.delete(e.pointerId);
  if(pointers.size===1){let pt=[...pointers.values()][0];panLast={x:pt.x,y:pt.y};pinchLast=null;}
  else if(pointers.size===0){panLast=null;pinchLast=null;}
});
cv.addEventListener('pointercancel',e=>{
  pointers.delete(e.pointerId);
  if(pointers.size===1){let pt=[...pointers.values()][0];panLast={x:pt.x,y:pt.y};pinchLast=null;}
  else if(pointers.size===0){panLast=null;pinchLast=null;}
});
cv.addEventListener('wheel',e=>{
  e.preventDefault();
  let sf=e.deltaY>0?1.15:0.87;
  let rect=cv.getBoundingClientRect();
  let cx=e.clientX-rect.left-W/2,cy=e.clientY-rect.top-H/2;
  panX=cx+(panX-cx)/sf;panY=cy+(panY-cy)/sf;
  nmRange*=sf;nmRange=Math.max(1,Math.min(5000,nmRange));
  scheduleRender();
},{passive:false});
cv.addEventListener('dblclick',e=>{e.preventDefault();panX=0;panY=0;if(selectedPos)nmRange=selectedPos.DefaultRange||200;labelMovingCS=null;labelMovePreview=null;scheduleRender();});

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
    centerLat=meta.CenterLat;centerLon=meta.CenterLon;panX=0;panY=0;
  }
  if(ls.visible&&!ls.layer){
    loadLayerGeometry(key).then(()=>scheduleRender());
  }else{
    scheduleRender();
  }
}

function loadLayerGeometry(key){
  return fetch('/api/asd/maplayers?maps='+encodeURIComponent(key))
    .then(r=>r.json())
    .then(layers=>{
      if(layers&&layers.length>0){
        let lk=layerKey(layers[0].Folder,layers[0].Name);
        if(layerState[lk]) layerState[lk].layer=layers[0];
      }
    }).catch(()=>{});
}

function loadMultipleLayers(keys){
  if(keys.length===0) return Promise.resolve();
  return fetch('/api/asd/maplayers?maps='+encodeURIComponent(keys.join(',')))
    .then(r=>r.json())
    .then(layers=>{
      if(!layers) return;
      for(let layer of layers){
        let key=layerKey(layer.Folder,layer.Name);
        if(layerState[key]) layerState[key].layer=layer;
      }
    }).catch(()=>{});
}

/* ============================================================
   Position selection
   ============================================================ */
function selectPosition(pos){
  selectedPos=pos;
  if(acTimer){clearInterval(acTimer);acTimer=null;}
  aircraft=[];

  if(!pos){
    document.title='ASD';
    for(let k in layerState) layerState[k].visible=false;
    centerLat=-27;centerLon=153;nmRange=200;panX=0;panY=0;
    buildPositionMenu();buildMapsMenu();render();
    return;
  }

  document.title='ASD: '+pos.Name;
  centerLat=pos.CenterLat;
  centerLon=pos.CenterLon;
  nmRange=pos.DefaultRange||200;
  panX=0;panY=0;

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
function api(path){return fetch(path).then(r=>r.json());}

function loadPositions(){
  api('/api/asd/positions').then(data=>{
    positions=data||[];
    buildPositionMenu();
  }).catch(()=>{});
}

function loadAsdMaps(){
  api('/api/asd/maps').then(data=>{
    allAsdMaps=data.Maps||[];
    colours=data.Colours||null;
    vvMinutes=data.VelocityVectors||0;
    if(data.TransitionAltitude) transAlt=data.TransitionAltitude;
    applyTheme();
    buildHatch();
    for(let m of allAsdMaps){
      let key=layerKey(m.Folder,m.Name);
      if(!layerState[key]) layerState[key]={visible:false,layer:null};
    }
    buildMapsMenu();
  }).catch(()=>{});
}

function loadSymbols(){
  api('/api/symbols').then(data=>{
    if(data.Symbols&&data.Symbols.length>0) buildSymTable(data.Symbols);
    if(data.LabelFontSize) labelFontSize=data.LabelFontSize;
    if(data.MapFontSize) mapFontSize=data.MapFontSize;
    scheduleRender();
  }).catch(()=>{});
}

// Runtime dynamic maps: controlled-sector infill and active restricted areas
// (yellow PRD outlines). These change as sectors are taken/released, so refresh
// periodically.
function loadDynamicMaps(){
  api('/api/asd/dynamicmaps').then(data=>{
    dynamicLayers=data||[];
    scheduleRender();
  }).catch(()=>{});
}

// Ident flash — vatSys blinks identing symbols on the render flash cycle
function updateFlashTimer(){
  let anyIdent=aircraft.some(a=>a.Ident);
  if(anyIdent&&!flashTimer){
    flashTimer=setInterval(()=>{flashOn=!flashOn;scheduleRender();},500);
  }else if(!anyIdent&&flashTimer){
    clearInterval(flashTimer);flashTimer=null;flashOn=true;
  }
}

function startAircraftPolling(){
  if(acTimer){clearInterval(acTimer);acTimer=null;}
  function poll(){
    // Query around the actual view centre (pan offset included) with a range
    // covering the whole viewport, so tracks never vanish when zooming/panning
    let scale=W/nmRange;
    let vLat=centerLat+(panY/scale)*NM_TO_DEG_LAT;
    let vLon=centerLon-(panX/scale)*nmToDegLon(centerLat);
    let rng=Math.ceil(nmRange*0.5*Math.max(1,H/W)*1.3)+5;
    api('/api/asd/aircraft?lat='+vLat.toFixed(5)+'&lon='+vLon.toFixed(5)+'&range='+rng).then(data=>{
      aircraft=data||[];updateFlashTimer();scheduleRender();
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
setInterval(loadDynamicMaps,5000);
</script>
</body>
</html>";
}
