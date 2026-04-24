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
*{margin:0;padding:0;box-sizing:border-box;-webkit-touch-callout:none;-webkit-user-select:none;user-select:none;}
html,body{width:100%;height:100%;overflow:hidden;background:#000;font-family:Consolas,'Courier New',monospace;color:#ccc;}
body{padding-top:env(safe-area-inset-top);padding-bottom:env(safe-area-inset-bottom);padding-left:env(safe-area-inset-left);padding-right:env(safe-area-inset-right);display:flex;flex-direction:column;}
#menubar{display:flex;background:#333;border-bottom:1px solid #555;height:36px;align-items:center;flex-shrink:0;position:relative;z-index:100;}
.menu-btn{background:none;border:none;color:#ccc;font:13px Consolas,'Courier New',monospace;padding:0 14px;height:100%;cursor:pointer;position:relative;}
.menu-btn:hover,.menu-btn.active{background:#555;}
.dropdown{display:none;position:absolute;top:36px;left:0;background:#333;border:1px solid #555;min-width:180px;z-index:200;max-height:70vh;overflow-y:auto;-webkit-overflow-scrolling:touch;}
.dropdown.show{display:block;}
.dd-item{display:flex;align-items:center;padding:8px 14px 8px 8px;font:13px Consolas,'Courier New',monospace;color:#ccc;cursor:pointer;white-space:nowrap;min-height:36px;}
.dd-item:hover{background:#555;}
.dd-item .chk{width:20px;text-align:center;flex-shrink:0;font-size:12px;}
.dd-sep{height:1px;background:#555;margin:2px 0;}
.dd-hdr{padding:6px 14px 4px 8px;font:bold 11px Consolas,'Courier New',monospace;color:#999;text-transform:uppercase;letter-spacing:0.5px;}
.dd-sub{position:relative;}
.dd-sub>.sub-menu{display:none;position:absolute;left:100%;top:0;background:#333;border:1px solid #555;min-width:160px;max-height:60vh;overflow-y:auto;-webkit-overflow-scrolling:touch;}
.dd-sub:hover>.sub-menu,.dd-sub.touch-open>.sub-menu{display:block;}
.dd-sub>.dd-item::after{content:'\25B6';margin-left:auto;padding-left:14px;font-size:9px;}
#title-text{margin-left:16px;font:13px Consolas,'Courier New',monospace;color:#aaa;flex:1;text-align:right;padding-right:14px;}
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
      <div class='dd-item'>Bearing Range Line</div>
      <div class='dd-item'>Contact Me</div>
      <div class='dd-item'>Decouple</div>
    </div>
  </div>
  <span id='title-text'>Ground</span>
</div>
<canvas id='cv'></canvas>
<script>
'use strict';
const cv=document.getElementById('cv'),ctx=cv.getContext('2d');
let W=0,H=0,dpr=1;
let mapData=null,colours=null,positions=[],selectedPos=null;
let centerLat=0,centerLon=0,rotation=0,magVar=0,nmRange=2;
let panX=0,panY=0;
let layerVisible={};
let aircraft=[];
let acTimer=null;

// --- Symbol geometry from Tracks.xml (exact vatSys shapes) ---
// Each is [points], filled, with coordinates relative to center, rotated to heading.
// StrokeWidth 0 = filled, 1 = stroked outline.
const SYMBOLS={
  Ground:     {pts:[[0,-7.5],[-5.5,3.5],[0,7.5],[5.5,3.5]], stroke:1, fill:false},
  GroundLight:{pts:[[0,-5.5],[-3.5,1.5],[0,5.5],[3.5,1.5]], stroke:0, fill:true},
  GroundMedium:{pts:[[0,-7.5],[-5.5,3.5],[0,7.5],[5.5,3.5]], stroke:0, fill:true},
  GroundHeavy:{pts:[[0,-9],[-7,5],[0,9],[7,5]], stroke:0, fill:true},
  GroundSuper:{pts:[[0,-10],[-9,6.5],[0,10],[9,6.5]], stroke:0, fill:true},
  HistoryDot: {radius:2}
};

function wakeSymbol(wake){
  switch((wake||'').toUpperCase()){
    case 'L': return SYMBOLS.GroundLight;
    case 'M': return SYMBOLS.GroundMedium;
    case 'H': return SYMBOLS.GroundHeavy;
    case 'J': return SYMBOLS.GroundSuper;
    default:  return SYMBOLS.Ground;
  }
}

// --- Coordinate conversion ---
const NM_TO_DEG_LAT=1/60;
function nmToDegLon(lat){return 1/(60*Math.cos(lat*Math.PI/180));}

function latLonToCanvas(lat,lon){
  let dlat=lat-centerLat, dlon=lon-centerLon;
  let xNm=dlon/nmToDegLon(centerLat), yNm=dlat/NM_TO_DEG_LAT;
  let scale=Math.min(W,H)/(nmRange*2);
  let rx=xNm*scale, ry=-yNm*scale;
  // vatSys applies Rotation + MagneticVariation as total rotation
  let totalRot=rotation+magVar;
  let rad=totalRot*Math.PI/180;
  let cx=rx*Math.cos(rad)-ry*Math.sin(rad);
  let cy=rx*Math.sin(rad)+ry*Math.cos(rad);
  return [W/2+panX+cx, H/2+panY+cy];
}

// --- Colours ---
function rgb(arr){return 'rgb('+arr[0]+','+arr[1]+','+arr[2]+')';}
function rgba(arr,a){return 'rgba('+arr[0]+','+arr[1]+','+arr[2]+','+a+')';}
function typeColour(type){
  if(!colours) return '#888';
  let t=(type||'').toLowerCase();
  if(t.includes('rwy')) return rgb(colours.Runway);
  if(t.includes('twy')) return rgb(colours.Taxiway);
  if(t.includes('apr')) return rgb(colours.Apron);
  if(t.includes('bld')) return rgb(colours.Building);
  if(t.includes('inf')) return rgb(colours.Other);
  return rgb(colours.Other);
}
function catColour(cat){
  if(!colours) return '#fff';
  if(cat==='arrival') return rgb(colours.Arrival);
  if(cat==='departure') return rgb(colours.Departure);
  if(cat==='local') return rgb(colours.Local);
  return rgb(colours.Unknown);
}

// --- Rendering ---
let renderPending=false;
function scheduleRender(){
  if(!renderPending){renderPending=true;requestAnimationFrame(()=>{renderPending=false;render();});}
}

function render(){
  ctx.setTransform(dpr,0,0,dpr,0,0);
  ctx.fillStyle=colours?rgb(colours.Background):'#696969';
  ctx.fillRect(0,0,W,H);
  if(!mapData) return;
  // Draw layers in priority order
  for(let layer of mapData.Layers){
    let key=layer.Folder+'|'+layer.Name;
    if(layerVisible[key]===false) continue;
    let col=typeColour(layer.Type);
    // Infills
    ctx.fillStyle=col;
    for(let inf of layer.Infills){
      if(inf.Points.length<3) continue;
      ctx.beginPath();
      let p0=latLonToCanvas(inf.Points[0][0],inf.Points[0][1]);
      ctx.moveTo(p0[0],p0[1]);
      for(let i=1;i<inf.Points.length;i++){
        let p=latLonToCanvas(inf.Points[i][0],inf.Points[i][1]);
        ctx.lineTo(p[0],p[1]);
      }
      ctx.closePath();
      ctx.fill();
    }
    // Lines
    ctx.strokeStyle=col;
    ctx.lineWidth=1;
    for(let ln of layer.Lines){
      if(ln.Points.length<2) continue;
      ctx.beginPath();
      let p0=latLonToCanvas(ln.Points[0][0],ln.Points[0][1]);
      ctx.moveTo(p0[0],p0[1]);
      for(let i=1;i<ln.Points.length;i++){
        let p=latLonToCanvas(ln.Points[i][0],ln.Points[i][1]);
        ctx.lineTo(p[0],p[1]);
      }
      ctx.stroke();
    }
    // Labels
    ctx.fillStyle=colours?rgb(colours.Other):'#999';
    ctx.font='9px Consolas,monospace';
    ctx.textAlign='center';
    ctx.textBaseline='middle';
    for(let lb of layer.Labels){
      let p=latLonToCanvas(lb.Lat,lb.Lon);
      ctx.fillText(lb.Text,p[0],p[1]);
    }
  }
  // Aircraft
  for(let ac of aircraft){
    drawAircraft(ac);
  }
}

function drawAircraft(ac){
  let p=latLonToCanvas(ac.Lat,ac.Lon);
  let col=catColour(ac.Category);
  let sym=wakeSymbol(ac.WakeCat);
  // Heading: vatSys rotates symbol to track heading, offset by map rotation
  let totalRot=rotation+magVar;
  let hdgRad=(ac.Hdg+totalRot)*Math.PI/180;

  ctx.save();
  ctx.translate(p[0],p[1]);
  ctx.rotate(hdgRad);

  // Draw the chevron symbol
  let pts=sym.pts;
  ctx.beginPath();
  ctx.moveTo(pts[0][0],pts[0][1]);
  for(let i=1;i<pts.length;i++) ctx.lineTo(pts[i][0],pts[i][1]);
  ctx.closePath();
  if(sym.fill){
    ctx.fillStyle=col;
    ctx.fill();
  } else {
    ctx.strokeStyle=col;
    ctx.lineWidth=sym.stroke||1;
    ctx.stroke();
  }

  // Heading line (velocity vector) extending from nose
  let topY=pts[0][1]; // top of symbol (negative Y = up)
  ctx.strokeStyle=col;
  ctx.lineWidth=1;
  ctx.beginPath();
  ctx.moveTo(0,topY);
  // Length proportional to ground speed (1px per knot, min 15px)
  let lineLen=Math.max(15, ac.Gs*0.8);
  ctx.lineTo(0,topY-lineLen);
  ctx.stroke();

  ctx.restore();

  // --- Label (tag) ---
  // vatSys Ground label format:
  //   Departure: [RWY] / Callsign / AircraftType WakeCat
  //   Arrival:   Callsign / AircraftType WakeCat
  ctx.fillStyle=col;
  ctx.font='10px Consolas,monospace';
  ctx.textAlign='left';
  ctx.textBaseline='top';
  let lx=p[0]+12, ly=p[1]-8;
  let line1=ac.Callsign||'';
  let line2='';
  if(ac.AircraftType){
    line2=ac.AircraftType;
    if(ac.WakeCat) line2+=' '+ac.WakeCat;
  }
  if(ac.Category==='departure'&&ac.Runway){
    ctx.fillText(ac.Runway,lx,ly);
    ly+=12;
  }
  ctx.fillText(line1,lx,ly);
  if(line2){ly+=12;ctx.fillText(line2,lx,ly);}
}

// --- Resize ---
function resize(){
  dpr=window.devicePixelRatio||1;
  let rect=cv.getBoundingClientRect();
  W=rect.width; H=rect.height;
  cv.width=W*dpr; cv.height=H*dpr;
  render();
}
window.addEventListener('resize',resize);

// --- Touch / Mouse ---
let pointers=new Map();
let lastDist=0,lastMid=null,isPanning=false;

cv.addEventListener('pointerdown',e=>{
  cv.setPointerCapture(e.pointerId);
  pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
  if(pointers.size===1){isPanning=true;lastMid={x:e.clientX,y:e.clientY};}
  if(pointers.size===2){
    let pts=[...pointers.values()];
    lastDist=Math.hypot(pts[1].x-pts[0].x,pts[1].y-pts[0].y);
    lastMid={x:(pts[0].x+pts[1].x)/2,y:(pts[0].y+pts[1].y)/2};
  }
});
cv.addEventListener('pointermove',e=>{
  if(!pointers.has(e.pointerId)) return;
  pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
  if(pointers.size===1&&isPanning){
    let pt=pointers.get(e.pointerId);
    panX+=pt.x-lastMid.x;
    panY+=pt.y-lastMid.y;
    lastMid={x:pt.x,y:pt.y};
    scheduleRender();
  }
  if(pointers.size===2){
    let pts=[...pointers.values()];
    let d=Math.hypot(pts[1].x-pts[0].x,pts[1].y-pts[0].y);
    if(lastDist>0){
      nmRange*=lastDist/d;
      nmRange=Math.max(0.1,Math.min(50,nmRange));
    }
    lastDist=d;
    let mid={x:(pts[0].x+pts[1].x)/2,y:(pts[0].y+pts[1].y)/2};
    panX+=mid.x-lastMid.x;
    panY+=mid.y-lastMid.y;
    lastMid=mid;
    scheduleRender();
  }
});
cv.addEventListener('pointerup',e=>{pointers.delete(e.pointerId);if(pointers.size<2)lastDist=0;if(pointers.size===0)isPanning=false;});
cv.addEventListener('pointercancel',e=>{pointers.delete(e.pointerId);if(pointers.size<2)lastDist=0;if(pointers.size===0)isPanning=false;});
cv.addEventListener('wheel',e=>{
  e.preventDefault();
  nmRange*=e.deltaY>0?1.15:0.87;
  nmRange=Math.max(0.1,Math.min(50,nmRange));
  scheduleRender();
},{passive:false});

// --- Menus ---
const btnPos=document.getElementById('btn-pos'),ddPos=document.getElementById('dd-pos');
const btnMaps=document.getElementById('btn-maps'),ddMaps=document.getElementById('dd-maps');
const btnTools=document.getElementById('btn-tools'),ddTools=document.getElementById('dd-tools');
const titleText=document.getElementById('title-text');
let openDD=null;

function closeAllDD(){
  document.querySelectorAll('.dropdown').forEach(d=>d.classList.remove('show'));
  document.querySelectorAll('.menu-btn').forEach(b=>b.classList.remove('active'));
  document.querySelectorAll('.dd-sub').forEach(s=>s.classList.remove('touch-open'));
  openDD=null;
}
function toggleDD(btn,dd){
  if(openDD===dd){closeAllDD();return;}
  closeAllDD();
  dd.classList.add('show');btn.classList.add('active');openDD=dd;
}
btnPos.addEventListener('click',e=>{e.stopPropagation();toggleDD(btnPos,ddPos);});
btnMaps.addEventListener('click',e=>{e.stopPropagation();toggleDD(btnMaps,ddMaps);});
btnTools.addEventListener('click',e=>{e.stopPropagation();toggleDD(btnTools,ddTools);});
document.addEventListener('click',e=>{
  // Maps dropdown stays open when clicking items inside it (vatSys behaviour)
  if(openDD===ddMaps&&document.getElementById('mw-maps').contains(e.target)) return;
  closeAllDD();
});

// --- Position menu ---
function buildPositionMenu(){
  ddPos.innerHTML='';
  // 'Free' item first
  let freeItem=mkItem('\u2713', selectedPos===null, 'Free');
  freeItem.addEventListener('click',()=>{selectPosition(null);closeAllDD();});
  ddPos.appendChild(freeItem);

  // Ungrouped positions first
  let ungrouped=positions.filter(p=>!p.Group);
  let groups={};
  positions.forEach(p=>{if(p.Group){if(!groups[p.Group])groups[p.Group]=[];groups[p.Group].push(p);}});

  if(ungrouped.length>0){
    ddPos.appendChild(mkSep());
    ungrouped.forEach(p=>ddPos.appendChild(mkPosItem(p)));
  }
  // Grouped positions
  let groupNames=Object.keys(groups).sort();
  for(let gn of groupNames){
    ddPos.appendChild(mkSep());
    let hdr=document.createElement('div');
    hdr.className='dd-hdr';
    hdr.textContent=gn;
    ddPos.appendChild(hdr);
    groups[gn].forEach(p=>ddPos.appendChild(mkPosItem(p)));
  }
}

function mkPosItem(p){
  let item=mkItem('\u2713', selectedPos&&selectedPos.Name===p.Name, p.Name);
  item.addEventListener('click',(()=>{let pp=p;return()=>{selectPosition(pp);closeAllDD();}})());
  return item;
}

function mkItem(checkChar, checked_, text){
  let item=document.createElement('div');
  item.className='dd-item';
  let chk=document.createElement('span');
  chk.className='chk';
  chk.textContent=checked_?checkChar:'';
  item.appendChild(chk);
  item.appendChild(document.createTextNode(text));
  return item;
}
function mkSep(){let s=document.createElement('div');s.className='dd-sep';return s;}

function selectPosition(pos){
  selectedPos=pos;
  if(acTimer){clearInterval(acTimer);acTimer=null;}
  aircraft=[];
  if(!pos){
    titleText.textContent='Ground';
    mapData=null;colours=null;layerVisible={};rotation=0;magVar=0;
    buildPositionMenu();buildMapsMenu();render();
    return;
  }
  titleText.textContent='Ground: '+pos.Name;
  panX=0;panY=0;
  loadMap(pos.Airport);
  buildPositionMenu();
}

// --- Maps menu ---
function buildMapsMenu(){
  ddMaps.innerHTML='';
  if(!mapData) return;
  let folders={};
  for(let layer of mapData.Layers){
    let f=layer.Folder||'';
    if(!folders[f]) folders[f]=[];
    folders[f].push(layer);
  }
  let folderNames=Object.keys(folders).sort();
  for(let fn of folderNames){
    if(fn===''){
      for(let layer of folders[fn]) ddMaps.appendChild(makeMapItem(layer));
    } else {
      let sub=document.createElement('div');
      sub.className='dd-sub';
      let trigger=document.createElement('div');
      trigger.className='dd-item';
      trigger.textContent=fn;
      sub.appendChild(trigger);
      trigger.addEventListener('click',e=>{e.stopPropagation();sub.classList.toggle('touch-open');});
      let subMenu=document.createElement('div');
      subMenu.className='sub-menu';
      for(let layer of folders[fn]) subMenu.appendChild(makeMapItem(layer));
      sub.appendChild(subMenu);
      ddMaps.appendChild(sub);
    }
  }
}
function makeMapItem(layer){
  let key=layer.Folder+'|'+layer.Name;
  if(!(key in layerVisible)) layerVisible[key]=true;
  let item=document.createElement('div');
  item.className='dd-item';
  let chk=document.createElement('span');
  chk.className='chk';
  chk.textContent=layerVisible[key]?'\u2713':'';
  item.appendChild(chk);
  item.appendChild(document.createTextNode(layer.Name));
  item.addEventListener('click',e=>{
    e.stopPropagation();
    layerVisible[key]=!layerVisible[key];
    chk.textContent=layerVisible[key]?'\u2713':'';
    scheduleRender();
  });
  return item;
}

// --- API ---
function api(path){return fetch(path).then(r=>r.json());}

function loadPositions(){
  api('/api/airports').then(data=>{
    positions=data||[];
    buildPositionMenu();
  }).catch(()=>{});
}

function loadMap(icao){
  api('/api/map?icao='+encodeURIComponent(icao)).then(data=>{
    if(!data||!data.Map){mapData=null;colours=null;buildMapsMenu();render();return;}
    mapData=data.Map;
    colours=data.Colours||null;
    centerLat=mapData.CenterLat;
    centerLon=mapData.CenterLon;
    nmRange=mapData.DefaultRange||2;
    rotation=mapData.Rotation||0;
    magVar=mapData.MagneticVariation||0;
    // Set layer visibility based on position's MapRefs
    layerVisible={};
    if(selectedPos&&selectedPos.MapRefs&&selectedPos.MapRefs.length>0){
      let refNames=new Set();
      selectedPos.MapRefs.forEach(r=>{
        // Extract the layer name from ref like 'Sydney/ASMGCS_YSSY_RWY' -> 'ASMGCS_YSSY_RWY'
        let slash=r.lastIndexOf('/');
        refNames.add(slash>=0?r.substring(slash+1):r);
      });
      for(let layer of mapData.Layers){
        let key=layer.Folder+'|'+layer.Name;
        layerVisible[key]=refNames.has(layer.Name);
      }
      // If nothing matched, show all
      if(!Object.values(layerVisible).some(v=>v)){
        for(let k in layerVisible) layerVisible[k]=true;
      }
    } else {
      for(let layer of mapData.Layers){
        layerVisible[layer.Folder+'|'+layer.Name]=true;
      }
    }
    buildMapsMenu();
    render();
    startAircraftPolling(icao);
  }).catch(()=>{});
}

function startAircraftPolling(icao){
  if(acTimer){clearInterval(acTimer);acTimer=null;}
  function poll(){
    api('/api/aircraft?icao='+encodeURIComponent(icao)).then(data=>{
      aircraft=data||[];
      scheduleRender();
    }).catch(()=>{});
  }
  poll();
  acTimer=setInterval(poll,2000);
}

// --- Init ---
resize();
loadPositions();
</script>
</body>
</html>";
}
