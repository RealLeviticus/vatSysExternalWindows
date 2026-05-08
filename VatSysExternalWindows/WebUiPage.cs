namespace VatSysExternalWindows;

public static class WebUiPage
{
    public const string Html = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover'>
<meta name='apple-mobile-web-app-capable' content='yes'>
<meta name='apple-mobile-web-app-status-bar-style' content='black-translucent'>
<title>Ground</title>
<style>
@font-face{font-family:'Terminus';src:local('Terminus (TTF)'),local('Terminus'),local('Terminus (TTF) for Windows'),local('TerminusTTF'),local('TerminusTTFWindows');font-weight:bold;}
*{margin:0;padding:0;box-sizing:border-box;-webkit-touch-callout:none;-webkit-user-select:none;user-select:none;}
html,body{width:100%;height:100%;overflow:hidden;background:rgb(105,105,105);font-family:'Terminus','Consolas','Courier New',monospace;color:rgb(0,0,96);}
body{padding-top:env(safe-area-inset-top);padding-bottom:env(safe-area-inset-bottom);padding-left:env(safe-area-inset-left);padding-right:env(safe-area-inset-right);display:flex;flex-direction:column;}

/* MenuStrip: vatSys WindowBackground + Border3DStyle.RaisedOuter, eurofont_winsml=16px */
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

/* Folder submenus - inline accordion style (touch-friendly) */
.dd-sub>.sub-menu{display:none;padding-left:16px;}
.dd-sub.open>.sub-menu{display:block;}
.dd-sub>.dd-item::after{content:'\25B6';margin-left:auto;padding-left:14px;font-size:9px;display:inline-block;transition:transform 0.15s;}
.dd-sub.open>.dd-item::after{transform:rotate(90deg);}

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
      <div class='dd-item disabled'>Contact Me</div>
      <div class='dd-item disabled'>Decouple</div>
    </div>
  </div>
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
let allGroundMaps=[];
let colours=null;
let selectedPos=null;

let centerLat=0,centerLon=0,rotation=0,magVar=0,nmRange=2;
let panX=0,panY=0;

// key = 'Folder/Name', value = { visible: bool, layer: MapLayer|null }
let layerState={};
let aircraft=[];
let acTimer=null;

/* ============================================================
   Symbol geometry - exact from Tracks.xml
   Ground: StrokeWidth=1, FigureBegin=Filled → filled + 1px stroke
   GroundLight/Medium/Heavy/Super: StrokeWidth=0, FigureBegin=Filled → filled only
   ============================================================ */
const SYMBOLS={
  Ground:      {pts:[[0,-7.5],[-5.5,3.5],[0,7.5],[5.5,3.5]], sw:1},
  GroundLight: {pts:[[0,-5.5],[-3.5,1.5],[0,5.5],[3.5,1.5]], sw:0},
  GroundMedium:{pts:[[0,-7.5],[-5.5,3.5],[0,7.5],[5.5,3.5]], sw:0},
  GroundHeavy: {pts:[[0,-9],[-7,5],[0,9],[7,5]], sw:0},
  GroundSuper: {pts:[[0,-10],[-9,6.5],[0,10],[9,6.5]], sw:0}
};
function wakeSymbol(w){
  switch((w||'').toUpperCase()){
    case'L':return SYMBOLS.GroundLight;
    case'M':return SYMBOLS.GroundMedium;
    case'H':return SYMBOLS.GroundHeavy;
    case'J':return SYMBOLS.GroundSuper;
    default:return SYMBOLS.Ground;
  }
}

/* ============================================================
   Coordinate conversion
   ============================================================ */
const NM_TO_DEG_LAT=1/60;
function nmToDegLon(lat){return 1/(60*Math.cos(lat*Math.PI/180));}
function latLonToCanvas(lat,lon){
  let dlat=lat-centerLat,dlon=lon-centerLon;
  let xNm=dlon/nmToDegLon(centerLat),yNm=dlat/NM_TO_DEG_LAT;
  let scale=Math.min(W,H)/(nmRange*2);
  let rx=xNm*scale,ry=-yNm*scale;
  let totalRot=rotation+magVar;
  let rad=totalRot*Math.PI/180;
  let cx=rx*Math.cos(rad)-ry*Math.sin(rad);
  let cy=rx*Math.sin(rad)+ry*Math.cos(rad);
  return[W/2+panX+cx,H/2+panY+cy];
}

/* ============================================================
   Colours
   ============================================================ */
function rgb(a){return'rgb('+a[0]+','+a[1]+','+a[2]+')';}

function applyTheme(){
  if(!colours) return;
  let wb=colours.WindowBackground;
  let dp=colours.DepressedButton;
  let it=colours.InteractiveText;
  let gt=colours.GenericText;
  if(!wb) return;
  let c=rgb(wb);
  let hi='rgb('+Math.min(255,wb[0]+50)+','+Math.min(255,wb[1]+48)+','+Math.min(255,wb[2]+48)+')';
  let sh='rgb('+Math.max(0,wb[0]-60)+','+Math.max(0,wb[1]-62)+','+Math.max(0,wb[2]-62)+')';
  let textCol=it?rgb(it):'rgb(0,0,96)';
  let disabledCol=gt?rgb(gt):'rgb(96,0,0)';
  let depCol=dp?rgb(dp):c;
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
    '.dd-item:hover{background:'+depCol+';}'+
    '.dd-item.disabled{color:'+disabledCol+';}';
}

function typeColour(type){
  if(!colours) return'#888';
  let t=(type||'').toLowerCase();
  if(t.includes('rwy')) return rgb(colours.Runway);
  if(t.includes('twy')) return rgb(colours.Taxiway);
  if(t.includes('apr')) return rgb(colours.Apron);
  if(t.includes('bld')) return rgb(colours.Building);
  return rgb(colours.Other);
}
function catColour(cat){
  if(!colours) return'#fff';
  if(cat==='arrival') return rgb(colours.Arrival);
  if(cat==='departure') return rgb(colours.Departure);
  if(cat==='local') return rgb(colours.Local);
  return rgb(colours.Unknown);
}

/* ============================================================
   Layer key
   ============================================================ */
function layerKey(folder,name){return folder?(folder+'/'+name):name;}

/* ============================================================
   Rendering
   ============================================================ */
let renderPending=false;
function scheduleRender(){if(!renderPending){renderPending=true;requestAnimationFrame(()=>{renderPending=false;render();});}}

function render(){
  ctx.setTransform(dpr,0,0,dpr,0,0);
  ctx.fillStyle=colours?rgb(colours.Background):'rgb(105,105,105)';
  ctx.fillRect(0,0,W,H);

  let visibleLayers=[];
  for(let key in layerState){
    let ls=layerState[key];
    if(ls.visible&&ls.layer) visibleLayers.push(ls);
  }
  // Priority descending: highest priority drawn first (behind)
  visibleLayers.sort((a,b)=>(b.layer.Priority||0)-(a.layer.Priority||0));

  for(let ls of visibleLayers){
    let layer=ls.layer;
    let col=typeColour(layer.Type);

    // Infills
    ctx.fillStyle=col;
    for(let inf of layer.Infills){
      if(inf.Points.length<3) continue;
      ctx.beginPath();
      let p0=latLonToCanvas(inf.Points[0][0],inf.Points[0][1]);
      ctx.moveTo(p0[0],p0[1]);
      for(let i=1;i<inf.Points.length;i++){let p=latLonToCanvas(inf.Points[i][0],inf.Points[i][1]);ctx.lineTo(p[0],p[1]);}
      ctx.closePath();ctx.fill();
    }

    // Lines
    ctx.strokeStyle=col;ctx.lineWidth=1;ctx.lineJoin='round';ctx.lineCap='round';
    for(let ln of layer.Lines){
      if(ln.Points.length<2) continue;
      ctx.beginPath();
      let p0=latLonToCanvas(ln.Points[0][0],ln.Points[0][1]);
      ctx.moveTo(p0[0],p0[1]);
      for(let i=1;i<ln.Points.length;i++){let p=latLonToCanvas(ln.Points[i][0],ln.Points[i][1]);ctx.lineTo(p[0],p[1]);}
      ctx.stroke();
    }

    // Map labels - eurofont_xsml = 16px Terminus bold
    ctx.fillStyle=colours?rgb(colours.Other):'rgb(150,150,150)';
    ctx.font=""bold 16px 'Terminus','Consolas',monospace"";
    ctx.textAlign='center';ctx.textBaseline='middle';
    for(let lb of layer.Labels){let p=latLonToCanvas(lb.Lat,lb.Lon);ctx.fillText(lb.Text,p[0],p[1]);}
  }

  for(let ac of aircraft) drawAircraft(ac);
}

/* ============================================================
   Aircraft rendering - matches vatSys PaintGroundTracks exactly
   Ground symbols: FigureBegin=Filled diamond, wake-category sized.
   Ground default StrokeWidth=1 → filled + 1px outline.
   GroundLight/Medium/Heavy/Super StrokeWidth=0 → filled only.
   Airborne (OnGround=false): ModeS circle (6.5px radius, filled).
   NO velocity vector for ground tracks (vatSys doesn't render them).
   Leader line: Direction=1 (22.5° NE), Length=1 (40px).
   Label font: eurofont_sml = 18px Terminus bold.
   History dots: 2px radius filled circles at past positions.
   ============================================================ */
const LABEL_DIR=1,LABEL_LEN=1;
const LABEL_ANGLE=LABEL_DIR*22.5;
const LEADER_PX=LABEL_LEN*35+5;

function drawAircraft(ac){
  let p=latLonToCanvas(ac.Lat,ac.Lon);
  let col=catColour(ac.Category);

  // History trail dots (2px radius filled circles)
  if(ac.History&&ac.History.length>0){
    ctx.fillStyle=col;
    for(let h of ac.History){
      let hp=latLonToCanvas(h[0],h[1]);
      ctx.beginPath();ctx.arc(hp[0],hp[1],2,0,Math.PI*2);ctx.fill();
    }
  }

  let hdgRad=(ac.Hdg+rotation+magVar)*Math.PI/180;

  if(ac.OnGround){
    // Ground symbol: wake-category diamond
    let sym=wakeSymbol(ac.WakeCat);
    ctx.save();
    ctx.translate(p[0],p[1]);
    ctx.rotate(hdgRad);
    let pts=sym.pts;
    ctx.beginPath();
    ctx.moveTo(pts[0][0],pts[0][1]);
    for(let i=1;i<pts.length;i++) ctx.lineTo(pts[i][0],pts[i][1]);
    ctx.closePath();
    ctx.fillStyle=col;ctx.fill();
    if(sym.sw>0){ctx.strokeStyle=col;ctx.lineWidth=sym.sw;ctx.stroke();}
    ctx.restore();
  }else{
    // Airborne symbol: ModeS circle (6.5px radius)
    ctx.beginPath();ctx.arc(p[0],p[1],6.5,0,Math.PI*2);
    ctx.strokeStyle=col;ctx.lineWidth=1;ctx.stroke();
  }

  // Leader line
  let rad=LABEL_ANGLE*Math.PI/180;
  let lx=p[0]+LEADER_PX*Math.cos(rad),ly=p[1]+LEADER_PX*Math.sin(rad);
  ctx.strokeStyle=col;ctx.lineWidth=1;
  ctx.beginPath();ctx.moveTo(p[0],p[1]);ctx.lineTo(lx,ly);ctx.stroke();

  // Label - eurofont_sml = 18px Terminus bold (ASDMainFont default)
  ctx.fillStyle=col;
  ctx.font=""bold 18px 'Terminus','Consolas',monospace"";
  let fontH=18;
  let isWest=LABEL_DIR>4&&LABEL_DIR<12;
  ctx.textAlign=isWest?'right':'left';
  ctx.textBaseline='top';
  let labelY=ly;

  // Label lines per Labels.xml ground label types
  let lines=[];
  if(!ac.OnGround){
    // GroundAir: RWY / ACID / ATYPE WakeCat / Level GS
    if(ac.Runway) lines.push(ac.Runway);
    if(ac.Callsign) lines.push(ac.Callsign);
    if(ac.AircraftType){let t=ac.AircraftType;if(ac.WakeCat)t+=' '+ac.WakeCat;lines.push(t);}
    let lvl='';if(ac.Gs>0)lvl=Math.round(ac.Gs)+'';if(lvl)lines.push(lvl);
  }else if(ac.Category==='departure'){
    // GroundDeparture: RWY / ACID / ATYPE WakeCat
    if(ac.Runway) lines.push(ac.Runway);
    if(ac.Callsign) lines.push(ac.Callsign);
    if(ac.AircraftType){let t=ac.AircraftType;if(ac.WakeCat)t+=' '+ac.WakeCat;lines.push(t);}
  }else if(ac.Category==='arrival'){
    // GroundArrival: Bay / ACID / ATYPE WakeCat
    if(ac.Bay) lines.push(ac.Bay);
    if(ac.Callsign) lines.push(ac.Callsign);
    if(ac.AircraftType){let t=ac.AircraftType;if(ac.WakeCat)t+=' '+ac.WakeCat;lines.push(t);}
  }else{
    // GroundLimited: callsign only
    if(ac.Callsign) lines.push(ac.Callsign);
  }

  for(let i=0;i<lines.length;i++) ctx.fillText(lines[i],lx,labelY+i*fontH);
}

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
  if(pointers.size===1){panLast={x:e.clientX,y:e.clientY};pinchLast=null;}
  else if(pointers.size===2){panLast=null;let pts=[...pointers.values()];pinchLast={dist:Math.hypot(pts[1].x-pts[0].x,pts[1].y-pts[0].y),mx:(pts[0].x+pts[1].x)/2,my:(pts[0].y+pts[1].y)/2};}
});
cv.addEventListener('pointermove',e=>{
  if(!pointers.has(e.pointerId)) return;
  pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
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
      nmRange*=sf;nmRange=Math.max(0.1,Math.min(50,nmRange));
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
  nmRange*=sf;nmRange=Math.max(0.1,Math.min(50,nmRange));
  scheduleRender();
},{passive:false});
cv.addEventListener('dblclick',e=>{e.preventDefault();panX=0;panY=0;if(selectedPos)nmRange=selectedPos.DefaultRange||2;scheduleRender();});

/* ============================================================
   Menu system
   Dropdown behavior matches vatSys:
   - Position & Tools: close on item click
   - Maps: stays open on item click (MapsDropDown_Closing cancel)
   - Folder submenus: expand on hover (desktop) or tap (touch)
   ============================================================ */
const btnPos=document.getElementById('btn-pos'),ddPos=document.getElementById('dd-pos');
const btnMaps=document.getElementById('btn-maps'),ddMaps=document.getElementById('dd-maps');
const btnTools=document.getElementById('btn-tools'),ddTools=document.getElementById('dd-tools');
let openDD=null;

function closeAllDD(){
  document.querySelectorAll('.dropdown').forEach(d=>d.classList.remove('show'));
  document.querySelectorAll('.menu-btn').forEach(b=>b.classList.remove('active'));
  closeAllSubs();
  openDD=null;
}

function closeAllSubs(){
  document.querySelectorAll('.dd-sub.open').forEach(s=>s.classList.remove('open'));
}

function toggleDD(btn,dd){
  if(openDD===dd){closeAllDD();return;}
  closeAllDD();
  dd.classList.add('show');
  btn.classList.add('active');
  openDD=dd;
}

btnPos.addEventListener('click',e=>{e.stopPropagation();toggleDD(btnPos,ddPos);});
btnMaps.addEventListener('click',e=>{e.stopPropagation();toggleDD(btnMaps,ddMaps);});
btnTools.addEventListener('click',e=>{e.stopPropagation();toggleDD(btnTools,ddTools);});

// Global click: close dropdowns unless clicking inside Maps (which stays open)
document.addEventListener('click',e=>{
  if(!openDD) return;
  // Check if click is inside any dropdown or menu bar
  let inMenubar=document.getElementById('menubar').contains(e.target);
  if(inMenubar) return; // handled by button handlers
  closeAllDD();
});

/* ============================================================
   Position menu
   ============================================================ */
function buildPositionMenu(){
  ddPos.innerHTML='';
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
    let sub=document.createElement('div');sub.className='dd-sub';
    let trigger=document.createElement('div');trigger.className='dd-item';
    let chkSpan=document.createElement('span');chkSpan.className='chk';chkSpan.textContent='';
    trigger.appendChild(chkSpan);trigger.appendChild(document.createTextNode(gn));
    sub.appendChild(trigger);
    // Toggle submenu on click/tap
    trigger.addEventListener('click',e=>{
      e.stopPropagation();
      let isOpen=sub.classList.contains('open');
      // Close sibling submenus first
      sub.parentElement.querySelectorAll(':scope > .dd-sub.open').forEach(s=>{if(s!==sub)s.classList.remove('open');});
      sub.classList.toggle('open',!isOpen);
    });
    let subMenu=document.createElement('div');subMenu.className='sub-menu';
    groups[gn].sort((a,b)=>a.Name.localeCompare(b.Name));
    groups[gn].forEach(p=>subMenu.appendChild(mkPosItem(p)));
    sub.appendChild(subMenu);
    ddPos.appendChild(sub);
  }
  ungrouped.sort((a,b)=>a.Name.localeCompare(b.Name));
  ungrouped.forEach(p=>ddPos.appendChild(mkPosItem(p)));
}

function mkPosItem(p){
  let item=mkItem('\u2713',selectedPos&&selectedPos.Name===p.Name,p.Name);
  item.addEventListener('click',e=>{
    e.stopPropagation();
    selectPosition(p);
    closeAllDD();
  });
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
   Maps menu
   Shows ALL Ground-category maps from the profile, grouped by folder.
   Checked state from position's MapRefs.
   Submenus expand on click/tap and stay open.
   Map item toggle stays open (MapsDropDown_Closing cancels close).
   ============================================================ */
function buildMapsMenu(){
  ddMaps.innerHTML='';
  if(!allGroundMaps||allGroundMaps.length===0) return;

  let folders={};
  for(let m of allGroundMaps){
    let f=m.Folder||'';
    if(!folders[f]) folders[f]=[];
    folders[f].push(m);
  }
  let folderNames=Object.keys(folders).sort();
  for(let fn of folderNames){
    let maps=folders[fn];
    maps.sort((a,b)=>a.Name.localeCompare(b.Name));
    if(fn===''){
      for(let m of maps) ddMaps.appendChild(makeMapItem(m));
    }else{
      let sub=document.createElement('div');sub.className='dd-sub';
      let trigger=document.createElement('div');trigger.className='dd-item';
      trigger.appendChild(document.createTextNode(fn));
      sub.appendChild(trigger);
      // Toggle folder submenu on click/tap
      trigger.addEventListener('click',e=>{
        e.stopPropagation();
        let isOpen=sub.classList.contains('open');
        // Close sibling submenus
        sub.parentElement.querySelectorAll(':scope > .dd-sub.open').forEach(s=>{if(s!==sub)s.classList.remove('open');});
        sub.classList.toggle('open',!isOpen);
      });
      let subMenu=document.createElement('div');subMenu.className='sub-menu';
      for(let m of maps) subMenu.appendChild(makeMapItem(m));
      sub.appendChild(subMenu);
      ddMaps.appendChild(sub);
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
    e.stopPropagation(); // Keep menu open (MapsDropDown_Closing behavior)
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
  if(ls.visible&&!ls.layer){
    loadLayerGeometry(key).then(()=>scheduleRender());
  }else{
    scheduleRender();
  }
}

function loadLayerGeometry(key){
  return fetch('/api/maplayers?maps='+encodeURIComponent(key))
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
  return fetch('/api/maplayers?maps='+encodeURIComponent(keys.join(',')))
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
    document.title='Ground';
    for(let k in layerState) layerState[k].visible=false;
    centerLat=0;centerLon=0;rotation=0;magVar=0;nmRange=2;panX=0;panY=0;
    buildPositionMenu();buildMapsMenu();render();
    return;
  }

  document.title='Ground: '+pos.Name;
  centerLat=pos.CenterLat;
  centerLon=pos.CenterLon;
  nmRange=pos.DefaultRange||2;
  rotation=pos.Rotation||0;
  magVar=pos.MagneticVariation||0;
  panX=0;panY=0;

  // Uncheck all, then check position's MapRefs
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
      startAircraftPolling(pos.Airport);
    });
  }else{
    scheduleRender();
    startAircraftPolling(pos.Airport);
  }
}

/* ============================================================
   API
   ============================================================ */
function api(path){return fetch(path).then(r=>r.json());}

function loadPositions(){
  api('/api/airports').then(data=>{
    positions=data||[];
    buildPositionMenu();
  }).catch(()=>{});
}

function loadGroundMaps(){
  api('/api/groundmaps').then(data=>{
    allGroundMaps=data.Maps||[];
    colours=data.Colours||null;
    applyTheme();
    for(let m of allGroundMaps){
      let key=layerKey(m.Folder,m.Name);
      if(!layerState[key]) layerState[key]={visible:false,layer:null};
    }
    buildMapsMenu();
  }).catch(()=>{});
}

function startAircraftPolling(icao){
  if(acTimer){clearInterval(acTimer);acTimer=null;}
  function poll(){
    api('/api/aircraft?icao='+encodeURIComponent(icao)).then(data=>{
      aircraft=data||[];scheduleRender();
    }).catch(()=>{});
  }
  poll();
  acTimer=setInterval(poll,2000);
}

/* Init */
resize();
loadPositions();
loadGroundMaps();
</script>
</body>
</html>";
}
