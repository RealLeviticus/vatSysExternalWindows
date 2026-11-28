// OzStrips over a web page.
//
// The page polls GET /api/ozstrips/state for a snapshot of the OzStrips window
// in vatSys (OzStripsBridge.cs builds it) and redraws from it; every action is
// a POST to /api/ozstrips/command, which the plugin performs in OzStrips itself.
// Nothing is decided here: a tap asks, and the next snapshot shows the result.
//
// Built for touch (an iPad beside the scope): every action is a tap or a
// double tap, bays scroll under a finger, and the page is not redrawn while a
// finger is down or a list is still gliding.
(function(){
'use strict';

var POLL_MS=500;
var POLL_TIMEOUT_MS=4000;
var DOUBLE_TAP_MS=230;
var SCALE_KEY='ozstrips.stripScale';

// Longest text OzStrips takes for each field (its own entry boxes limit the same way)
var MAX_LENGTH={setClx:3,setRemark:10,setGate:4,setFreq:7,setGlop:10,setRwy:3,setSid:10,setCfl:7,setAerodrome:4};

var app=document.getElementById('app');
var overlay=document.getElementById('overlay');
var toastHost=document.getElementById('toast');

var state=null;            // the last snapshot with Available = true
var lastSig=null;          // that snapshot as text, without its clock
var notice='Connecting to OzStrips...';   // shown while there is no state
var shownNotice=null;
var pollBusy=false;
var pollWanted=false;
var failCount=0;

var selectedCS=null;       // the strip picked on this page
var selectedBar=null;      // or the bar: {bay,type,style,text}
var editState=null;
var moveState=null;
var barState=null;
var runwayState=null;
var topMenuState=null;
var topMenuLeft=4;
var toastTimer=null;
var tapTimers={};
var resizeTimer=null;

var pointers={};           // pointerId -> when it went down
var lastPointerUp=0;
var flings=0;              // bay lists still gliding after a swipe
var deferredRender=false;
var flushTimer=null;
var localScale=readLocalScale();

// ---------------------------------------------------------------------------
// Transport
// ---------------------------------------------------------------------------

var START_RETRY_MS=6000;
var startAskedAt=0;

function poll(){
  if(pollBusy){return;}
  pollBusy=true;
  pollWanted=false;

  var ctl=(typeof AbortController==='function')?new AbortController():null;
  var timer=ctl?setTimeout(function(){ctl.abort();},POLL_TIMEOUT_MS):null;
  var done=function(){
    if(timer){clearTimeout(timer);}
    pollBusy=false;
    if(pollWanted){poll();}
  };

  fetch('/api/ozstrips/state',{cache:'no-store',signal:ctl?ctl.signal:undefined})
    .then(function(r){
      if(!r.ok){throw new Error('HTTP '+r.status);}
      return r.json();
    })
    .then(onState,onPollFail)
    .then(done,done);
}

function onState(s){
  failCount=0;
  try{
    if(!s||s.Available!==true){
      showNotice(s&&s.Reason?String(s.Reason):'OzStrips is not available.');
      // Not running in vatSys: start it there, minimised. Asked again only
      // after a pause, in case it could not start.
      if(s&&s.CanStart===true&&Date.now()-startAskedAt>START_RETRY_MS){
        startAskedAt=Date.now();
        send({Action:'start'});
      }
      return;
    }

    // The clock ticks every second; everything else redraws only when it changes
    var clock=s.UtcTime;
    s.UtcTime='';
    var sig=JSON.stringify(s);
    s.UtcTime=clock;

    state=s;
    if(sig===lastSig){
      updateClock();
      return;
    }

    lastSig=sig;
    dropStaleSelection();
    updateClock();
    requestRender();
  }catch(err){
    if(window.console){console.error(err);}
  }
}

function onPollFail(){
  failCount++;
  // One missed poll is not worth blanking the board
  if(failCount>=2){showNotice('Not connected to vatSys. Retrying...');}
}

function showNotice(text){
  state=null;
  lastSig=null;
  notice=text;
  selectedCS=null;
  selectedBar=null;
  editState=null;
  moveState=null;
  barState=null;
  runwayState=null;
  topMenuState=null;
  requestRender();
}

function send(cmd){
  fetch('/api/ozstrips/command',{
    method:'POST',
    cache:'no-store',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify(cmd)
  }).then(function(r){
    return r.json().then(function(body){return {ok:r.ok,body:body};},function(){return {ok:r.ok,body:null};});
  }).then(function(res){
    var body=res.body;
    if(!res.ok||!body||body.Ok===false){
      toast(body&&body.Error?String(body.Error):'OzStrips did not accept that.',3000);
      return;
    }
    // Show the result now rather than at the next tick
    if(pollBusy){pollWanted=true;}else{poll();}
  },function(){
    toast('Not sent: no connection to vatSys.',3000);
  });
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function esc(v){
  return String(v===null||v===undefined?'':v)
    .replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')
    .replace(/"/g,'&quot;').replace(/'/g,'&#39;');
}

function toast(msg,ms){
  toastHost.innerHTML='<div class="toast">'+esc(msg)+'</div>';
  if(toastTimer){clearTimeout(toastTimer);}
  toastTimer=setTimeout(clearToast,ms||1800);
}

function clearToast(){
  if(toastTimer){clearTimeout(toastTimer);toastTimer=null;}
  toastHost.innerHTML='';
}

function readLocalScale(){
  try{
    var v=Number(window.localStorage.getItem(SCALE_KEY));
    return isFinite(v)&&v>=1&&v<=3?v:0;
  }catch(_){return 0;}
}

function writeLocalScale(v){
  localScale=v;
  try{
    if(v){window.localStorage.setItem(SCALE_KEY,String(v));}
    else{window.localStorage.removeItem(SCALE_KEY);}
  }catch(_){ }
}

// This device's strip size if one was chosen in View, otherwise OzStrips' own
function stripScale(){
  var scale=localScale||Number(state&&state.StripScale);
  if(!isFinite(scale)||scale<1){scale=1;}
  if(scale>3){scale=3;}
  return scale;
}

function groupedBays(){
  var bays=state&&state.Bays?state.Bays:[];
  if(!bays.length){return[];}

  // The widths at which OzStrips itself goes to two columns and to one
  var targetCols=3;
  var width=window.innerWidth||document.documentElement.clientWidth||0;
  if(width<=840){
    targetCols=1;
  }else if(width<=1250){
    targetCols=2;
  }

  if(targetCols<3){
    var compactColumns=[];
    for(var ci=0;ci<targetCols;ci++){
      compactColumns.push([]);
    }

    var maxPerColumn=Math.ceil(bays.length/targetCols);
    var colIdx=0;
    for(var bi=0;bi<bays.length;bi++){
      while(colIdx<targetCols-1&&compactColumns[colIdx].length>=maxPerColumn){
        colIdx++;
      }

      compactColumns[colIdx].push(bays[bi]);
    }

    return compactColumns.filter(function(col){return col.length>0;});
  }

  var byColumn={};
  var ordered=[];

  for(var i=0;i<bays.length;i++){
    var bay=bays[i];
    var col=(bay.Column===undefined||bay.Column===null)?i:bay.Column;
    if(!byColumn[col]){byColumn[col]=[];ordered.push(col);}
    byColumn[col].push(bay);
  }

  ordered.sort(function(a,b){return a-b;});

  var columns=[];
  for(var j=0;j<ordered.length;j++){
    columns.push(byColumn[ordered[j]]);
  }

  return columns;
}

function findStripWithBay(cs){
  if(!state||!state.Bays||!cs){return null;}
  for(var i=0;i<state.Bays.length;i++){
    var bay=state.Bays[i];
    var items=bay.Items||[];
    for(var j=0;j<items.length;j++){
      if(items[j].ItemType==='STRIP'&&items[j].Strip&&items[j].Strip.Callsign===cs){
        return {strip:items[j].Strip,bay:bay};
      }
    }
  }
  return null;
}

function selectedInfo(){
  return findStripWithBay(selectedCS);
}

// A queue bar's text counts its strips, so it is known by its bay alone
function barKey(bay,item){
  var queue=item.ItemType==='QUEUEBAR';
  return {bay:String(bay.Bay),type:item.ItemType,style:queue?0:Number(item.Style||0),text:queue?'':String(item.Text||'')};
}

function sameBar(a,b){
  return !!a&&!!b&&a.bay===b.bay&&a.type===b.type&&a.style===b.style&&a.text===b.text;
}

function barExists(sel){
  var bays=state&&state.Bays?state.Bays:[];
  for(var i=0;i<bays.length;i++){
    var items=bays[i].Items||[];
    for(var j=0;j<items.length;j++){
      if(items[j].ItemType!=='STRIP'&&sameBar(sel,barKey(bays[i],items[j]))){return true;}
    }
  }
  return false;
}

// Forget a strip or bar that has left the board, and any dialog about it
function dropStaleSelection(){
  if(selectedCS&&!findStripWithBay(selectedCS)){selectedCS=null;}
  if(selectedBar&&!barExists(selectedBar)){selectedBar=null;}
  if(moveState&&!findStripWithBay(moveState.cs)){moveState=null;}
}

function runDualTap(key,singleFn,doubleFn){
  if(tapTimers[key]){
    clearTimeout(tapTimers[key]);
    delete tapTimers[key];
    doubleFn();
    return;
  }

  tapTimers[key]=setTimeout(function(){
    delete tapTimers[key];
    singleFn();
  },DOUBLE_TAP_MS);
}

// ---------------------------------------------------------------------------
// When to draw
// ---------------------------------------------------------------------------

// Replacing the page under a finger loses the tap, and replacing a gliding
// list stops it dead, so a redraw for new data waits for both.
function busy(){
  var now=Date.now();
  var down=0;
  for(var id in pointers){
    if(now-pointers[id]>8000){delete pointers[id];}else{down++;}
  }
  return down>0||flings>0||now-lastPointerUp<80;
}

function requestRender(){
  if(busy()){
    deferredRender=true;
    scheduleFlush();
    return;
  }
  deferredRender=false;
  renderMain();
  // Menus and pickers follow the data; a dialog being typed in is left alone
  if(!state||(!editState&&!barState)){renderOverlay();}
}

function scheduleFlush(){
  if(flushTimer){return;}
  flushTimer=setTimeout(function(){
    flushTimer=null;
    if(deferredRender){requestRender();}
  },100);
}

// For something the user just did: draw now
function renderAll(){
  deferredRender=false;
  renderMain();
  renderOverlay();
}

document.addEventListener('pointerdown',function(e){pointers[e.pointerId]=Date.now();},true);
function pointerGone(e){
  delete pointers[e.pointerId];
  lastPointerUp=Date.now();
  if(deferredRender){scheduleFlush();}
}
document.addEventListener('pointerup',pointerGone,true);
document.addEventListener('pointercancel',pointerGone,true);
window.addEventListener('blur',function(){pointers={};});

// ---------------------------------------------------------------------------
// The window: menu bar, bays, control bar
// ---------------------------------------------------------------------------

function updateClock(){
  var el=app.querySelector('.time-box');
  if(el&&state){el.textContent=state.UtcTime||'--:--:--';}
}

function renderMain(){
  if(!state){
    if(shownNotice===notice){return;}
    shownNotice=notice;
    app.innerHTML='<div class="waiting"><div class="waiting-box">'+
      '<div class="waiting-title">OzStrips</div>'+
      '<div class="waiting-msg">'+esc(notice)+'</div>'+
      '<a class="waiting-link" href="/">Back to windows</a>'+
      '</div></div>';
    return;
  }
  shownNotice=null;

  document.documentElement.style.setProperty('--strip-scale',String(stripScale()));

  var h='';
  h+='<div class="top-menu">';
  h+='<a class="label menu-item" href="/">Home</a>';
  h+='<span class="label menu-item '+(topMenuState==='aerodrome'?'open':'')+'" data-menu-action="aerodrome">Aerodrome</span>';
  h+='<span class="label menu-item '+(topMenuState==='view'?'open':'')+'" data-menu-action="view">View</span>';
  h+='<span class="label menu-item '+(topMenuState==='settings'?'open':'')+'" data-menu-action="settings">Settings</span>';
  h+='<span class="label menu-item '+(topMenuState==='info'?'open':'')+'" data-menu-action="info">Info</span>';
  h+='<span class="pending '+((state.PendingPDCs||0)>0?'active':'')+'">Pending PDCs: '+(Number(state.PendingPDCs)||0)+'</span>';
  h+='<span class="autofill '+(state.AutoFillAvailable?'on':'off')+'">AutoFill Status</span>';
  h+='</div>';

  h+='<div class="main"><div class="columns">';
  var columns=groupedBays();
  for(var c=0;c<columns.length;c++){
    h+='<div class="column">';
    var bays=columns[c];
    for(var b=0;b<bays.length;b++){
      h+=renderBay(bays[b]);
    }
    h+='</div>';
  }
  h+='</div></div>';

  h+=renderControlBar();

  var scrollPositions=saveScrollPositions();
  var mainEl=app.querySelector('.main');
  var mainScroll=mainEl?{left:mainEl.scrollLeft,top:mainEl.scrollTop}:null;
  app.innerHTML=h;
  bindMainEvents();
  restoreScrollPositions(scrollPositions);
  if(mainScroll){var newMain=app.querySelector('.main');if(newMain){newMain.scrollLeft=mainScroll.left;newMain.scrollTop=mainScroll.top;}}
}

function saveScrollPositions(){
  var positions={};
  app.querySelectorAll('[data-drop-bay]').forEach(function(el){
    var bay=el.getAttribute('data-drop-bay');
    if(el.scrollTop>0){positions[bay]=el.scrollTop;}
  });
  return positions;
}

function restoreScrollPositions(positions){
  app.querySelectorAll('[data-drop-bay]').forEach(function(el){
    var bay=el.getAttribute('data-drop-bay');
    if(positions[bay]){el.scrollTop=positions[bay];}
  });
}

function renderBay(bay){
  var id=esc(bay.Bay);
  var h='';
  h+='<section class="bay">';
  h+='<header class="bay-top">';
  h+='<span class="bay-name" data-drop-header="'+id+'">'+esc(bay.Name||'Bay')+'</span>';
  h+='<span class="bay-spacer"></span>';
  if(bay.CdmDisplay){
    // This bay's header carries the CDM departure rates instead of the queue buttons
    h+=renderCdmRates();
  }else{
    h+='<button class="bay-btn queue" data-bay-action="queue" data-bay="'+id+'">Add to Queue</button>';
    h+='<button class="bay-btn div" data-bay-action="toggleQueue" data-bay="'+id+'">Toggle Queue Bar</button>';
  }
  h+='</header>';
  h+='<div class="bay-body" data-drop-bay="'+id+'">';

  var items=bay.Items||[];
  for(var i=0;i<items.length;i++){
    var item=items[i];
    if(item.ItemType==='STRIP'){
      if(item.Strip){h+=renderStrip(item.Strip);}
    }else{
      h+=renderBar(bay,item);
    }
  }

  h+='</div></section>';
  return h;
}

// Departures in the last so-many minutes against the rate's target, as OzStrips colours them
function renderCdmRates(){
  var rates=state.CdmEnabled&&state.CdmRates?state.CdmRates:[];
  if(!rates.length){return '';}
  var h='<div class="cdm-stats">';
  for(var i=0;i<rates.length;i++){
    var r=rates[i];
    var colour=/^#[0-9a-fA-F]{6}$/.test(r.Colour)?r.Colour:'#000000';
    h+='<span class="cdm-cell" style="background:'+colour+'">'+esc(Number(r.Minutes)+'m: '+Number(r.Count)+'/'+Number(r.Target))+'</span>';
  }
  return h+'</div>';
}

function renderBar(bay,item){
  var key=barKey(bay,item);
  var cls='bar-item';
  if(key.style>0){cls+=' style'+key.style;}
  if(sameBar(selectedBar,key)){cls+=' picked';}
  return '<div class="bar-shell"><div class="'+cls+'" data-bar-bay="'+esc(key.bay)+'" data-bar-type="'+esc(key.type)+
    '" data-bar-style="'+key.style+'" data-bar-text="'+esc(key.text)+'">'+esc(item.Text||'')+'</div></div>';
}

function acidClass(s){
  if(selectedCS===s.Callsign||s.Picked){
    return 'bg-silver';
  }
  if(s.LastTransmit){
    return 'bg-lime';
  }
  if(s.WorldFlight){
    return 'bg-world';
  }
  return '';
}

function eobtClass(s){
  if(!s.CdmActive){
    return '';
  }
  if(s.CdmReadyToPush){
    return 'bg-limegreen';
  }
  if(s.CdmHasSlot){
    return 'bg-pink';
  }
  return 'bg-lightgray';
}

function sidClass(s){
  var cls=s.VfrSidAlert?'bg-orange':'bg-limegreen';
  if(s.SidTransition){
    cls+=' sid-transition';
  }
  return cls;
}

function pdcClass(s){
  if(!s.PdcRequested){
    return '';
  }
  return s.PdcNeedsAck?'pdc-blink':'bg-yellow';
}

function renderStrip(s){
  var typeClass=s.StripType==='ARRIVAL'?'arr':(s.StripType==='LOCAL'?'loc':(s.StripType==='DEPARTURE'?'dep':'unk'));
  var shell='strip-shell '+typeClass;
  if(s.CockLevel===1){shell+=' cock1';}
  if(s.Crossing){shell+=' crossing';}

  // OzStrips' configured colour for the strip type; the stylesheet has its defaults
  var colour=state.StripColours?state.StripColours[s.StripType]:null;
  var style=(typeof colour==='string'&&/^#[0-9a-fA-F]{3,6}$/.test(colour))?' style="background:'+colour+'"':'';

  var h='';
  h+='<article class="'+shell+'" data-cs="'+esc(s.Callsign||'')+'"'+style+'>';
  h+='<div class="strip-canvas"><div class="strip-grid">';
  h+=cell('c-stand cock-zone '+(s.StandAutofilled?'bg-lightgray':''),s.Gate,'tap-gate');
  h+=cell('c-eobt cock-zone '+eobtClass(s),s.Time,'tap-eobt');
  h+=cell('c-type cock-zone',s.AircraftType,'tap-openfdr');
  h+=cell('c-wtc cock-zone',s.WTC,'');
  h+=cell('c-acid '+acidClass(s),s.Callsign,'tap-acid');
  h+=cell('c-rwy',s.Runway,'tap-rwy');
  h+=cell('c-clx',s.CLX,'tap-clx');
  h+=cell('c-sid '+sidClass(s),s.SID,'tap-sid');
  h+=cell('c-rfl '+(s.RflAlert?'bg-orange':''),s.RFL,'tap-rfl');
  h+=cell('c-ades cock-zone',s.ADES,'tap-openfdr');
  h+=cell('c-route cock-zone',s.RouteIndicator,'tap-route');
  h+=cell('c-frul cock-zone',s.FlightRules,'tap-route');
  h+=cell('c-pdc cock-zone '+pdcClass(s),s.PDCIndicator,'tap-pdc');
  h+=cell('c-ssr cock-zone '+(s.SsrAlert?'bg-orange':''),s.SSR,'tap-ssr');
  h+=cell('c-ready '+(s.ReadyAlert?'bg-orange':''),s.Ready?'RDY':'','tap-ready');
  h+=cell('c-fwpt '+(s.RouteAlert?'bg-orange':''),s.FirstWpt,'tap-fwpt');
  h+=cell('c-cfl',s.CFL,'tap-cfl');
  h+=cell('c-glop cross-zone '+(s.NoHdgAlert?'bg-orange':''),s.GLOP,'tap-glop');
  h+=cell('c-remark cross-zone',s.Remark,'tap-remark');
  h+=cell('c-freq cross-zone',s.DepartureFreq,'tap-freq');
  h+=cell('c-tot '+(s.Tot&&s.Tot!=='00:00'?'tot-running':''),s.Tot||'00:00','tap-tot');
  h+='</div>';
  if(s.NoSlot){h+='<div class="noslot">NO SLOT</div>';}
  h+='</div></article>';
  return h;
}

function cell(cls,text,action){
  var ac=action?(' tap" data-action="'+action):'';
  return '<div class="cell '+cls+ac+'">'+esc(text||'')+'</div>';
}

function renderControlBar(){
  var selected=selectedInfo();
  var disableSel=selected?'':' disabled';
  var disableInhibit=(selected||selectedBar)?'':' disabled';
  var connection=String(state.ConnectionState||(state.Connected?'CONNECTED':'DISCONNECTED')).toLowerCase();
  var connClass='conn-stat '+(connection==='connected'||connection==='reconnecting'?connection:'disconnected')+((state.ConnectionsCount||0)>1?' multi':'');
  var runwayPairs=state.RunwayPairs||[];
  var releaseDisabled=runwayPairs.length?'':' disabled';

  var h='';
  h+='<div class="control-bar">';
  h+='<div class="'+connClass+'" data-control="connections"><span>CONN STAT</span></div>';
  h+='<div class="time-box">'+esc(state.UtcTime||'--:--:--')+'</div>';
  h+='<div class="ad-wrap" data-control="metar"><div class="ad-box">'+esc(state.Aerodrome||'????')+'</div></div>';
  h+='<div class="atis-wrap"><div class="atis-box">'+esc(state.Atis||'Z')+'</div></div>';
  h+='<button class="ctl-btn w96" data-control="inhibit"'+disableInhibit+'>INHIBIT</button>';
  h+='<button class="ctl-btn w142 cross" data-control="crossBar">XX CROSS XX</button>';
  h+='<button class="ctl-btn w142 release" data-control="releaseBar"'+releaseDisabled+'>XX RELEASE XX</button>';
  h+='<button class="ctl-btn w96" data-control="addBar">ADD BAR</button>';
  h+='<button class="ctl-btn w96" data-control="flip"'+disableSel+'>FLIP</button>';
  h+='</div>';
  return h;
}

// ---------------------------------------------------------------------------
// Menus and dialogs
// ---------------------------------------------------------------------------

function menuRow(label,cmd,checked,disabled,value){
  var attrs=' class="menu-row" data-menu-cmd="'+esc(cmd)+'"';
  if(value!==undefined&&value!==null){attrs+=' data-menu-value="'+esc(value)+'"';}
  if(disabled){attrs+=' disabled';}
  return '<button'+attrs+'><span class="menu-check">'+(checked?'&#10003;':'')+'</span><span>'+esc(label)+'</span></button>';
}

function renderTopMenuLayer(){
  if(!topMenuState||!state){return '';}

  var h='';
  h+='<div class="menu-layer">';
  h+='<div class="menu-backdrop" id="menuBackdrop"></div>';
  h+='<div class="menu-panel" style="left:'+topMenuLeft+'px">';

  if(topMenuState==='aerodrome'){
    // OzStrips' quick-switch list, then the box to type any other
    var aerodromes=state.Aerodromes||[];
    for(var ai=0;ai<aerodromes.length;ai++){
      h+=menuRow(aerodromes[ai],'setAerodrome',state.Aerodrome===aerodromes[ai],false,aerodromes[ai]);
    }
    if(aerodromes.length>0){h+='<div class="menu-sep"></div>';}
    h+=menuRow('Other...','enterAerodrome',false,false);
  }else if(topMenuState==='view'){
    var layouts=state.Layouts||[];
    if(layouts.length>0){
      for(var li=0;li<layouts.length;li++){
        h+=menuRow(layouts[li],'setLayout',state.CurrentLayout===layouts[li],false,layouts[li]);
      }
      h+='<div class="menu-sep"></div>';
    }
    h+=menuRow('3 Columns or Less','setSmartResize',state.SmartResizeMode===3,false,'3');
    h+=menuRow('2 Columns or Less','setSmartResize',state.SmartResizeMode===2,false,'2');
    h+=menuRow('1 Column','setSmartResize',state.SmartResizeMode===1,false,'1');
    h+=menuRow('Disabled','setSmartResize',state.SmartResizeMode===0,false,'0');
    h+='<div class="menu-sep"></div>';
    h+=menuRow('Toggle Circuit Bay','toggleCircuit',state.CircuitActive,!state.CircuitToggleAvailable);
    h+=menuRow('Toggle Coordinator Bay','toggleCoordinator',state.CoordinatorActive,false);
    h+=menuRow('Toggle CDM','toggleCdm',state.CdmEnabled,false);
    h+='<div class="menu-sep"></div>';
    // Kept on this device only: strips big enough for a finger without changing the desktop
    h+=menuRow('Strip Size: as OzStrips','localScale',!localScale,false,'0');
    h+=menuRow('Strip Size: 1x','localScale',localScale===1,false,'1');
    h+=menuRow('Strip Size: 1.25x','localScale',localScale===1.25,false,'1.25');
    h+=menuRow('Strip Size: 1.5x','localScale',localScale===1.5,false,'1.5');
    h+=menuRow('Strip Size: 2x','localScale',localScale===2,false,'2');
  }else if(topMenuState==='settings'){
    h+=menuRow('Settings Window','openSettings',false,false);
    h+=menuRow('Keyboard Settings','openKeySettings',false,false);
    h+='<div class="menu-sep"></div>';
    h+=menuRow('PDC Sound','togglePdcSound',state.PdcSoundEnabled,false);
    h+=menuRow('Inhibit Ground Map Updating','toggleGroundMaps',state.GroundMapsInhibited,false);
  }else if(topMenuState==='info'){
    h+=menuRow('GitHub','openUrl',false,false,'https://github.com/maxrumsey/OzStrips/');
    h+=menuRow('Documentation','openUrl',false,false,'https://maxrumsey.xyz/OzStrips/');
    h+=menuRow('Discord Server','openUrl',false,false,'https://discord.gg/VfqFvXeg6V');
    h+=menuRow('Changelog','openUrl',false,false,'https://maxrumsey.xyz/OzStrips/changelog');
    h+=menuRow('About','showAbout',false,false);
    h+='<div class="menu-sep"></div>';
    h+=menuRow('SignalR Log','showSignalRLog',false,false);
    h+=menuRow('ReloadStrip','reloadStripElements',false,false);
    h+=menuRow('ReloadAerodromeList','reloadAerodromeList',false,false);
    h+=menuRow('Override ATIS','overrideAtis',false,false);
  }

  h+='</div>';
  h+='</div>';
  return h;
}

function renderEditModal(){
  var max=MAX_LENGTH[editState.field];
  var h='';
  h+='<div class="modal-overlay" id="editOverlay"><div class="modal">';
  h+='<h4>'+esc(editState.label)+(editState.cs?(' - '+esc(editState.cs)):'')+'</h4>';
  h+='<div class="row"><input id="editInput" type="text" value="'+esc(editState.value||'')+'"'+(max?' maxlength="'+max+'"':'')+
    ' autocapitalize="characters" autocomplete="off" autocorrect="off" spellcheck="false" enterkeyhint="done" /></div>';
  h+='<div class="btns"><button class="ok" id="editOk">OK</button><button class="cancel" id="editCancel">Cancel</button></div>';
  h+='</div></div>';
  return h;
}

function renderMoveModal(){
  var h='';
  h+='<div class="modal-overlay" id="moveOverlay"><div class="modal">';
  h+='<h4>Actions - '+esc(moveState.cs)+'</h4>';
  h+='<div class="grid gap">';
  h+='<button data-move-action="moveUp">Move Up</button>';
  h+='<button data-move-action="moveDown">Move Down</button>';
  h+='<button data-move-action="moveToBarUp">Up To Bar</button>';
  h+='<button data-move-action="moveToBarDown">Down To Bar</button>';
  h+='<button data-move-action="crossing">Toggle Cross</button>';
  h+='<button data-move-action="flip">Flip Type</button>';
  h+='<button data-move-action="inhibitSidAlert">Inhibit SID Alert</button>';
  h+='<button data-move-action="inhibit">Inhibit Strip</button>';
  h+='</div>';
  h+='<h4>Move To Bay</h4>';
  h+='<div class="grid">';

  var bays=state&&state.Bays?state.Bays:[];
  for(var i=0;i<bays.length;i++){
    var bay=bays[i];
    h+='<button data-movebay="'+esc(bay.Bay)+'">'+esc(bay.Name)+'</button>';
  }

  h+='</div>';
  h+='<div class="btns gap-top"><button id="moveDeselect">Deselect</button><button class="cancel" id="moveCancel">Cancel</button></div>';
  h+='</div></div>';
  return h;
}

function renderBarModal(){
  var bays=state&&state.Bays?state.Bays:[];
  var h='';
  h+='<div class="modal-overlay" id="barOverlay"><div class="modal">';
  h+='<h4>Add Bar</h4>';
  h+='<div class="row"><label for="barBaySelect">Bay</label><select id="barBaySelect">';
  for(var i=0;i<bays.length;i++){
    var sel=String(bays[i].Bay)===String(barState.bay)?' selected':'';
    h+='<option value="'+esc(bays[i].Bay)+'"'+sel+'>'+esc(bays[i].Name)+'</option>';
  }
  h+='</select></div>';
  h+='<div class="row"><label for="barStyleSelect">Style</label><select id="barStyleSelect">';
  h+='<option value="1"'+(barState.style==='1'?' selected':'')+'>Grey</option>';
  h+='<option value="2"'+(barState.style==='2'?' selected':'')+'>Orange</option>';
  h+='<option value="3"'+(barState.style==='3'?' selected':'')+'>Red</option>';
  h+='</select></div>';
  h+='<div class="row"><label for="barTextInput">Text</label><input id="barTextInput" type="text" maxlength="64" value="'+esc(barState.text||'')+
    '" autocapitalize="characters" autocomplete="off" autocorrect="off" spellcheck="false" enterkeyhint="done" /></div>';
  h+='<div class="btns"><button class="ok" id="barOk">OK</button><button class="cancel" id="barCancel">Cancel</button></div>';
  h+='</div></div>';
  return h;
}

function renderRunwayModal(){
  var pairs=state&&state.RunwayPairs?state.RunwayPairs:[];
  var title=runwayState&&runwayState.mode==='release'?'Release Runway':'Crossing Runway';
  var h='';
  h+='<div class="modal-overlay" id="runwayOverlay"><div class="modal">';
  h+='<h4>'+title+'</h4>';
  h+='<div class="grid">';

  for(var i=0;i<pairs.length;i++){
    var p=String(pairs[i]||'').trim().toUpperCase();
    if(!p){continue;}
    var label=p;
    if(p.length%2===0){
      label=p.slice(0,p.length/2)+'/'+p.slice(p.length/2);
    }
    h+='<button data-runway="'+esc(p)+'">'+esc(label)+'</button>';
  }

  h+='</div>';
  h+='<div class="btns gap-top"><button class="cancel" id="runwayCancel">Cancel</button></div>';
  h+='</div></div>';
  return h;
}

function preserveDraftValues(){
  if(editState){
    var editInput=document.getElementById('editInput');
    if(editInput){editState.value=editInput.value;}
  }
  if(barState){
    var barText=document.getElementById('barTextInput');
    var barBay=document.getElementById('barBaySelect');
    var barStyle=document.getElementById('barStyleSelect');
    if(barText){barState.text=barText.value;}
    if(barBay){barState.bay=barBay.value;}
    if(barStyle){barState.style=barStyle.value;}
    var active=document.activeElement;
    if(active){barState._focusId=active.id||null;}
  }
}

function restoreModalFocus(){
  if(editState){
    var inp=document.getElementById('editInput');
    if(inp&&document.activeElement!==inp){
      inp.focus();
      var len=inp.value.length;
      try{inp.setSelectionRange(len,len);}catch(_){ }
    }
  }
  if(barState){
    var savedFocusId=barState._focusId;
    if(savedFocusId){
      var savedEl=document.getElementById(savedFocusId);
      if(savedEl){savedEl.focus();return;}
    }
    var modal=document.getElementById('barOverlay');
    var active=document.activeElement;
    var alreadyInModal=modal&&active&&modal.contains(active)&&active.tagName!=='BUTTON'&&active.tagName!=='DIV';
    if(!alreadyInModal){
      var barInp=document.getElementById('barTextInput');
      if(barInp){barInp.focus();}
    }
  }
}

// Draws whatever menu or dialog is open. Called when one opens or closes, and
// for new data only while nothing in it is being typed (see requestRender).
function renderOverlay(){
  preserveDraftValues();

  var h='';
  if(state){
    h+=renderTopMenuLayer();
    if(editState){h+=renderEditModal();}
    if(moveState){h+=renderMoveModal();}
    if(barState){h+=renderBarModal();}
    if(runwayState){h+=renderRunwayModal();}
  }

  if(!h&&!overlay.firstChild){return;}
  overlay.innerHTML=h;
  if(h){
    bindOverlayEvents();
    restoreModalFocus();
  }
}

function openEdit(cs,field,label,value){
  editState={cs:cs,field:field,label:label,value:value||''};
  renderAll();
}

// ---------------------------------------------------------------------------
// Events
// ---------------------------------------------------------------------------

function onStripCell(el){
  var bayBody=el.closest('.bay-body');
  if(bayBody&&bayBody._touchScrolled){return;}
  var shell=el.closest('.strip-shell');
  var cs=shell?shell.getAttribute('data-cs'):null;
  if(!cs){return;}
  var info=findStripWithBay(cs);
  if(!info||!info.strip){return;}

  var s=info.strip;
  var action=el.getAttribute('data-action');

  switch(action){
    case 'tap-acid':
      // First tap picks the strip, a tap on the picked strip opens its actions
      if(selectedCS===cs){
        moveState={cs:cs};
      }else{
        selectedCS=cs;
        selectedBar=null;
      }
      renderAll();
      break;
    case 'tap-eobt':
      runDualTap(cs+'-eobt',function(){
        send({Action:'cock',Callsign:cs});
      },function(){
        send({Action:'openCdm',Callsign:cs});
      });
      break;
    case 'tap-openfdr':
      send({Action:'openFdr',Callsign:cs});
      break;
    case 'tap-route':
      runDualTap(cs+'-route',function(){
        send({Action:'showRoute',Callsign:cs});
      },function(){
        send({Action:'inhibitAlert',Callsign:cs,Value:'ROUTE'});
      });
      break;
    case 'tap-pdc':
      runDualTap(cs+'-pdc',function(){
        send({Action:'openPdc',Callsign:cs});
      },function(){
        send({Action:'openPm',Callsign:cs});
      });
      break;
    case 'tap-fwpt':
      runDualTap(cs+'-fwpt',function(){
        send({Action:'openFdr',Callsign:cs});
      },function(){
        send({Action:'openReroute',Callsign:cs});
      });
      break;
    case 'tap-ready':
      runDualTap(cs+'-ready',function(){
        send({Action:'ready',Callsign:cs});
      },function(){
        send({Action:'inhibitAlert',Callsign:cs,Value:'READY'});
      });
      break;
    case 'tap-clx':
      openEdit(cs,'setClx','Clearance',s.CLX||'');
      break;
    case 'tap-remark':
      openEdit(cs,'setRemark','Remark',s.Remark||'');
      break;
    case 'tap-gate':
      // A greyed stand is OzStrips' allocation, not something entered
      openEdit(cs,'setGate','Gate/Stand',s.StandAutofilled?'':(s.Gate||''));
      break;
    case 'tap-freq':
      openEdit(cs,'setFreq','Dep Frequency',s.DepartureFreq||'');
      break;
    case 'tap-rwy':
      openEdit(cs,'setRwy','Runway',s.Runway||'');
      break;
    case 'tap-cfl':
      openEdit(cs,'setCfl','CFL',s.CFL==='BLK'?'':(s.CFL||''));
      break;
    case 'tap-glop':
      runDualTap(cs+'-glop',function(){
        openEdit(cs,'setGlop','Global Ops',s.GLOP||'');
      },function(){
        send({Action:'inhibitAlert',Callsign:cs,Value:'HDG'});
      });
      break;
    case 'tap-ssr':
      runDualTap(cs+'-ssr',function(){
        send({Action:'assignSsr',Callsign:cs});
      },function(){
        send({Action:'inhibitAlert',Callsign:cs,Value:'SSR'});
      });
      break;
    case 'tap-sid':
      runDualTap(cs+'-sid',function(){
        send({Action:'sidTrigger',Callsign:cs});
      },function(){
        openEdit(cs,'setSid','SID',s.SID||'');
      });
      break;
    case 'tap-rfl':
      runDualTap(cs+'-rfl',function(){
        send({Action:'openFdr',Callsign:cs});
      },function(){
        send({Action:'inhibitAlert',Callsign:cs,Value:'RFL'});
      });
      break;
    case 'tap-tot':
      send({Action:'toggleTot',Callsign:cs});
      break;
  }
}

// One finger on a bay: up and down scrolls the bay (then the page, once the bay
// is at its end), sideways scrolls the page, and letting go at speed glides on.
function bindBayScroll(el){
  function main(){return el.closest('.main');}

  // Whole pixels only, so "did not move" reliably means the bay is at its end
  function scrollBy(sd,dx,dy){
    sd.restX+=dx;
    sd.restY+=dy;
    var px=sd.restX<0?Math.ceil(sd.restX):Math.floor(sd.restX);
    var py=sd.restY<0?Math.ceil(sd.restY):Math.floor(sd.restY);
    sd.restX-=px;
    sd.restY-=py;
    if(py){
      var before=el.scrollTop;
      el.scrollTop=before+py;
      if(el.scrollTop===before){var mv=main();if(mv){mv.scrollTop+=py;}}
    }
    if(px){var mh=main();if(mh){mh.scrollLeft+=px;}}
  }

  function stopFling(){
    if(!el._fling){return;}
    cancelAnimationFrame(el._fling);
    el._fling=null;
    flings=Math.max(0,flings-1);
  }

  el.addEventListener('pointerdown',function(e){
    if(e.pointerType==='mouse'){return;}
    stopFling();
    el._touchScrolled=false;
    el._scrollData={id:e.pointerId,lastX:e.clientX,lastY:e.clientY,pendingDx:0,pendingDy:0,restX:0,restY:0,raf:null,samples:[],tracking:true,moveX:0,moveY:0,axis:null};
  });

  el.addEventListener('pointermove',function(e){
    var sd=el._scrollData;
    if(!sd||!sd.tracking||e.pointerId!==sd.id){return;}
    e.preventDefault();
    var dx=sd.lastX-e.clientX;
    var dy=sd.lastY-e.clientY;
    sd.lastX=e.clientX;
    sd.lastY=e.clientY;
    sd.moveX+=Math.abs(dx);
    sd.moveY+=Math.abs(dy);
    if(!sd.axis){
      if(sd.moveX+sd.moveY<=8){return;}
      // Past the slop of a tap: this is a scroll, in whichever direction leads
      sd.axis=sd.moveX>sd.moveY?'x':'y';
      el._touchScrolled=true;
    }
    if(sd.axis==='x'){sd.pendingDx+=dx;}else{sd.pendingDy+=dy;sd.samples.push({dy:dy,t:Date.now()});}
    if(sd.samples.length>6){sd.samples.shift();}
    if(!sd.raf){
      sd.raf=requestAnimationFrame(function(){
        sd.raf=null;
        var px=sd.pendingDx,py=sd.pendingDy;
        sd.pendingDx=0;sd.pendingDy=0;
        scrollBy(sd,px,py);
      });
    }
  });

  el.addEventListener('pointerup',function(e){
    var sd=el._scrollData;
    if(!sd||e.pointerId!==sd.id){return;}
    sd.tracking=false;
    if(sd.raf){cancelAnimationFrame(sd.raf);sd.raf=null;}
    scrollBy(sd,sd.pendingDx,sd.pendingDy);
    sd.pendingDx=0;sd.pendingDy=0;

    var vy=0;
    var now=Date.now();
    var recent=sd.samples.filter(function(s){return now-s.t<80;});
    if(recent.length>=2){
      var totalDy=0;var totalDt=recent[recent.length-1].t-recent[0].t;
      for(var i=0;i<recent.length;i++){totalDy+=recent[i].dy;}
      if(totalDt>0){vy=totalDy/totalDt;}
    }
    el._scrollData=null;
    // The click that follows a scroll is not a tap on a strip
    if(el._touchScrolled){setTimeout(function(){el._touchScrolled=false;},300);}
    if(Math.abs(vy)<0.15){return;}

    var velocity=vy*1000;
    var friction=0.95;
    var lastT=performance.now();
    flings++;
    function flingStep(t){
      // A frame's timestamp can predate the pointerup that started the glide
      var dt=Math.max(0,(t-lastT)/16.667);
      lastT=t;
      velocity*=Math.pow(friction,dt);
      var step=velocity*(dt*16.667/1000);
      var before=el.scrollTop;
      el.scrollTop=before+step;
      var moved=el.scrollTop!==before;
      if(!moved){
        // The bay is at its end: let the page carry on
        var page=main();
        if(page){
          var pageBefore=page.scrollTop;
          page.scrollTop=pageBefore+step;
          moved=page.scrollTop!==pageBefore;
        }
      }
      // Spent, or nothing left to scroll
      if(Math.abs(velocity)<10||(dt>0&&!moved)){
        el._fling=null;
        flings=Math.max(0,flings-1);
        if(deferredRender){scheduleFlush();}
        return;
      }
      el._fling=requestAnimationFrame(flingStep);
    }
    el._fling=requestAnimationFrame(flingStep);
  });

  el.addEventListener('pointercancel',function(e){
    var sd=el._scrollData;
    if(sd&&e.pointerId===sd.id){
      if(sd.raf){cancelAnimationFrame(sd.raf);}
      el._scrollData=null;
    }
  });
}

// OzStrips lets go of a strip once it has been moved, queued, crossed or
// flipped, unless its "keep strip picked" setting is on
function releasePick(cs){
  if(selectedCS!==cs||(state&&state.KeepStripPicked)){return;}
  selectedCS=null;
  renderAll();
}

function moveSelectedTo(bay){
  var cs=selectedCS;
  if(!cs||!bay){return;}
  send({Action:'move',Callsign:cs,Bay:bay});
  releasePick(cs);
}

function bindMainEvents(){
  app.querySelectorAll('[data-menu-action]').forEach(function(el){
    el.addEventListener('click',function(){
      var action=el.getAttribute('data-menu-action');
      var rect=el.getBoundingClientRect();
      var width=window.innerWidth||document.documentElement.clientWidth||0;
      topMenuLeft=Math.max(2,Math.min(Math.floor(rect.left),width-266));
      topMenuState=(topMenuState===action)?null:action;
      renderAll();
    });
  });

  app.querySelectorAll('[data-action]').forEach(function(el){
    el.addEventListener('click',function(e){
      e.stopPropagation();
      onStripCell(el);
    });
  });

  // A bar is picked like a strip; INHIBIT then removes it
  app.querySelectorAll('[data-bar-type]').forEach(function(el){
    el.addEventListener('click',function(e){
      e.stopPropagation();
      var bayBody=el.closest('.bay-body');
      if(bayBody&&bayBody._touchScrolled){return;}
      var key={
        bay:el.getAttribute('data-bar-bay'),
        type:el.getAttribute('data-bar-type'),
        style:Number(el.getAttribute('data-bar-style')||0),
        text:el.getAttribute('data-bar-text')||''
      };
      selectedBar=sameBar(selectedBar,key)?null:key;
      if(selectedBar){selectedCS=null;}
      renderAll();
    });
  });

  app.querySelectorAll('[data-bay-action]').forEach(function(el){
    el.addEventListener('click',function(e){
      e.stopPropagation();
      var bay=el.getAttribute('data-bay');
      var action=el.getAttribute('data-bay-action');

      if(action==='toggleQueue'){
        send({Action:'toggleQueueBar',Bay:bay});
        return;
      }

      if(action==='queue'){
        var info=selectedInfo();
        if(!info||!selectedCS){
          toast('Select a strip first.');
          return;
        }

        if(String(info.bay.Bay)!==bay){
          toast('Selected strip must be in this bay.');
          return;
        }

        var queued=selectedCS;
        send({Action:'queueUp',Callsign:queued});
        releasePick(queued);
      }
    });
  });

  // With a strip picked, a tap on a bay's empty space or its name moves it there
  app.querySelectorAll('[data-drop-bay]').forEach(function(el){
    el.addEventListener('click',function(e){
      if(el._touchScrolled){return;}
      var target=e.target;
      if(!target||typeof target.closest!=='function'){return;}
      if(target.closest('.strip-shell')){return;}
      if(target.closest('.bar-item')){return;}
      moveSelectedTo(el.getAttribute('data-drop-bay'));
    });
    bindBayScroll(el);
  });

  app.querySelectorAll('[data-drop-header]').forEach(function(el){
    el.addEventListener('click',function(){
      moveSelectedTo(el.getAttribute('data-drop-header'));
    });
  });

  app.querySelectorAll('[data-control]').forEach(function(el){
    el.addEventListener('click',function(){
      var action=el.getAttribute('data-control');
      var info=selectedInfo();

      // What the desktop shows in tooltips over these two boxes
      if(action==='connections'){
        var clients=state&&state.Connections?state.Connections:[];
        toast(clients.length?'Online Clients\n'+clients.join('\n'):'No other clients online.',4000);
        return;
      }

      if(action==='metar'){
        toast(state&&state.Metar?'METAR\n'+state.Metar:'No METAR.',6000);
        return;
      }

      if(action==='inhibit'){
        if(selectedBar){
          if(selectedBar.type==='QUEUEBAR'){
            send({Action:'toggleQueueBar',Bay:selectedBar.bay});
          }else{
            send({Action:'deleteBar',Bay:selectedBar.bay,Value:selectedBar.text,Style:selectedBar.style});
          }
          selectedBar=null;
          renderAll();
          return;
        }
        if(!info||!selectedCS){toast('Select a strip first.');return;}
        send({Action:'inhibit',Callsign:selectedCS});
        return;
      }

      if(action==='flip'){
        if(!info||!selectedCS){toast('Select a strip first.');return;}
        var flipped=selectedCS;
        send({Action:'flip',Callsign:flipped});
        releasePick(flipped);
        return;
      }

      if(action==='crossBar'){
        var crossPairs=state&&state.RunwayPairs?state.RunwayPairs:[];
        if(crossPairs.length){
          runwayState={mode:'cross'};
          renderAll();
          return;
        }
        send({Action:'toggleCrossBarDirect'});
        return;
      }

      if(action==='releaseBar'){
        var releasePairs=state&&state.RunwayPairs?state.RunwayPairs:[];
        if(!releasePairs.length){
          toast('Release bar unavailable for this aerodrome.');
          return;
        }
        runwayState={mode:'release'};
        renderAll();
        return;
      }

      if(action==='addBar'){
        var bays=state&&state.Bays?state.Bays:[];
        if(!bays.length){toast('No bays available.');return;}
        barState={bay:String(bays[0].Bay),style:'1',text:''};
        renderAll();
      }
    });
  });
}

function bindOverlayEvents(){
  var menuBackdrop=document.getElementById('menuBackdrop');
  if(menuBackdrop){
    menuBackdrop.addEventListener('click',function(){
      topMenuState=null;
      renderAll();
    });
  }

  overlay.querySelectorAll('[data-menu-cmd]').forEach(function(el){
    el.addEventListener('click',function(e){
      e.stopPropagation();
      var cmd=el.getAttribute('data-menu-cmd');
      var value=el.getAttribute('data-menu-value');
      topMenuState=null;

      if(cmd==='openUrl'){
        if(value){window.open(value,'_blank','noopener,noreferrer');}
        renderAll();
        return;
      }

      if(cmd==='enterAerodrome'){
        openEdit('','setAerodrome','Set Aerodrome (ICAO)',state&&state.Aerodrome?state.Aerodrome:'');
        return;
      }

      if(cmd==='localScale'){
        writeLocalScale(Number(value)||0);
        renderAll();
        return;
      }

      var payload={Action:cmd};
      if(value!==null&&value!==undefined&&value!==''){
        payload.Value=value;
      }
      send(payload);
      renderAll();
    });
  });

  var editOk=document.getElementById('editOk');
  if(editOk){
    editOk.addEventListener('click',function(){
      if(!editState){return;}
      var input=document.getElementById('editInput');
      var val=input?input.value:'';
      if(editState.field==='setAerodrome'){
        var aerodrome=String(val||'').trim().toUpperCase();
        if(aerodrome.length!==4){
          toast('Enter a 4-letter ICAO code.');
          return;
        }

        send({Action:'setAerodrome',Value:aerodrome});
      }else{
        send({Action:editState.field,Callsign:editState.cs,Value:String(val||'').toUpperCase()});
      }
      editState=null;
      renderAll();
    });
  }

  var editCancel=document.getElementById('editCancel');
  if(editCancel){editCancel.addEventListener('click',function(){editState=null;renderAll();});}
  var editOverlay=document.getElementById('editOverlay');
  if(editOverlay){editOverlay.addEventListener('click',function(e){if(e.target===editOverlay){editState=null;renderAll();}});}

  var editInput=document.getElementById('editInput');
  if(editInput){
    editInput.addEventListener('keydown',function(e){
      if(e.key==='Enter'){var ok=document.getElementById('editOk');if(ok){ok.click();}}
      if(e.key==='Escape'){editState=null;renderAll();}
    });
  }

  var moveCancel=document.getElementById('moveCancel');
  if(moveCancel){moveCancel.addEventListener('click',function(){moveState=null;renderAll();});}
  var moveDeselect=document.getElementById('moveDeselect');
  if(moveDeselect){moveDeselect.addEventListener('click',function(){moveState=null;selectedCS=null;renderAll();});}
  var moveOverlay=document.getElementById('moveOverlay');
  if(moveOverlay){moveOverlay.addEventListener('click',function(e){if(e.target===moveOverlay){moveState=null;renderAll();}});}

  overlay.querySelectorAll('[data-movebay]').forEach(function(el){
    el.addEventListener('click',function(){
      if(!moveState){return;}
      var cs=moveState.cs;
      send({Action:'move',Callsign:cs,Bay:el.getAttribute('data-movebay')});
      moveState=null;
      if(!(state&&state.KeepStripPicked)&&selectedCS===cs){selectedCS=null;}
      renderAll();
    });
  });

  overlay.querySelectorAll('[data-move-action]').forEach(function(el){
    el.addEventListener('click',function(){
      if(!moveState){return;}
      var action=el.getAttribute('data-move-action');
      var cs=moveState.cs;
      send({Action:action,Callsign:cs});
      moveState=null;
      if((action==='crossing'||action==='flip')&&!(state&&state.KeepStripPicked)&&selectedCS===cs){selectedCS=null;}
      renderAll();
    });
  });

  var barOk=document.getElementById('barOk');
  if(barOk){
    barOk.addEventListener('click',function(){
      if(!barState){return;}
      var textEl=document.getElementById('barTextInput');
      var bayEl=document.getElementById('barBaySelect');
      var styleEl=document.getElementById('barStyleSelect');

      var text=textEl?textEl.value:'';
      if(!text||!text.trim()){
        toast('Enter bar text.');
        return;
      }

      send({
        Action:'addBar',
        Bay:bayEl?bayEl.value:barState.bay,
        Style:parseInt(styleEl?styleEl.value:barState.style,10),
        Value:text.toUpperCase()
      });

      barState=null;
      renderAll();
    });
  }

  var barCancel=document.getElementById('barCancel');
  if(barCancel){barCancel.addEventListener('click',function(){barState=null;renderAll();});}
  var barOverlay=document.getElementById('barOverlay');
  if(barOverlay){barOverlay.addEventListener('click',function(e){if(e.target===barOverlay){barState=null;renderAll();}});}
  var barTextInput=document.getElementById('barTextInput');
  if(barTextInput){
    barTextInput.addEventListener('keydown',function(e){
      if(e.key==='Enter'){var ok=document.getElementById('barOk');if(ok){ok.click();}}
      if(e.key==='Escape'){barState=null;renderAll();}
    });
  }

  var runwayCancel=document.getElementById('runwayCancel');
  if(runwayCancel){runwayCancel.addEventListener('click',function(){runwayState=null;renderAll();});}
  var runwayOverlay=document.getElementById('runwayOverlay');
  if(runwayOverlay){runwayOverlay.addEventListener('click',function(e){if(e.target===runwayOverlay){runwayState=null;renderAll();}});}
  overlay.querySelectorAll('[data-runway]').forEach(function(el){
    el.addEventListener('click',function(){
      if(!runwayState){return;}
      var pair=String(el.getAttribute('data-runway')||'').trim().toUpperCase();
      if(!pair){return;}
      if(runwayState.mode==='release'){
        send({Action:'toggleReleaseBar',Value:pair});
      }else{
        send({Action:'toggleCrossBar',Value:pair});
      }
      runwayState=null;
      renderAll();
    });
  });
}

toastHost.addEventListener('click',clearToast);

// The column count follows the width, so the bays are laid out again; an open
// dialog is left as it is (an on-screen keyboard resizes the page on Android).
window.addEventListener('resize',function(){
  if(!state){return;}
  if(resizeTimer){clearTimeout(resizeTimer);}
  resizeTimer=setTimeout(function(){
    resizeTimer=null;
    if(!state){return;}
    if(topMenuState){
      topMenuState=null;
      renderOverlay();
    }
    renderMain();
  },90);
});

poll();
setInterval(poll,POLL_MS);
})();
