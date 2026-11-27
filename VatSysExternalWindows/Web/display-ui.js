'use strict';
/* ============================================================
   The window around the renderer: BaseForm title bar, the MenuStrip
   (ASDWindow / ASMGCSWindow), pointer handling (ASD_MouseDown /
   ASD_MouseMove) and the data polling.
   ============================================================ */

let positions = [];
let mapList = [];
const WINDOW_NAME = GROUND ? 'Ground' : 'ASD';
const STORE_KEY = 'vatsys-external-' + TYPE + '-position';

function api(path) {
  // Rejects on HTTP errors so an error body is never mistaken for data
  return fetch(path).then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); });
}

/* ---------- Title bar ---------- */

const titleEl = document.getElementById('title');
function setTitle(text) {
  titleEl.textContent = text;
  document.title = text;
  fitTitle();
}
// The title is centred on the whole bar unless it would run under the buttons
function fitTitle() {
  let bar = document.getElementById('titlebar').clientWidth;
  let textWidth = titleEl.textContent.length * 8;
  titleEl.classList.toggle('narrow', textWidth >= bar - 23 - 69);
}

document.getElementById('tb-close').addEventListener('click', () => { location.href = '/'; });
document.getElementById('tb-max').addEventListener('click', () => {
  let d = document, el = d.documentElement;
  if (d.fullscreenElement || d.webkitFullscreenElement) (d.exitFullscreen || d.webkitExitFullscreen).call(d);
  else if (el.requestFullscreen) el.requestFullscreen().catch(() => { });
  else if (el.webkitRequestFullscreen) el.webkitRequestFullscreen();
});
function fullscreenChanged() {
  let full = !!(document.fullscreenElement || document.webkitFullscreenElement);
  document.getElementById('ico-max').style.display = full ? 'none' : '';
  document.getElementById('ico-restore').style.display = full ? '' : 'none';
}
document.addEventListener('fullscreenchange', fullscreenChanged);
document.addEventListener('webkitfullscreenchange', fullscreenChanged);

/* ---------- Theme: the profile's window colours ---------- */

let focused = document.hasFocus();
function applyTheme() {
  let g = colours.Gdi || {}, root = document.documentElement.style;
  let set = (name, id) => { if (g[id]) root.setProperty(name, css(g[id])); };
  set('--win-bg', 'WindowBackground');
  set('--win-text', 'GenericText');
  set('--win-interactive', 'InteractiveText');
  set('--win-depressed', 'WindowButtonDepressed');
  set('--win-selected', 'WindowButtonSelected');
  // A secondary ASD window's border takes the focus colour while it has focus
  set('--win-border', !GROUND && focused ? 'PopupASDWindowBorderFocused' : 'WindowBorder');
  root.setProperty('--view-bg', backgroundColour());
}
window.addEventListener('focus', () => { focused = true; applyTheme(); });
window.addEventListener('blur', () => { focused = false; applyTheme(); });

/* ============================================================
   Menus: a MenuStrip with cascading ToolStripDropDownMenus
   ============================================================ */

const menubar = document.getElementById('menubar');
let openMenus = [];   // [{ el, owner }] by depth
let openTop = null;

function closeMenus(fromDepth) {
  fromDepth = fromDepth || 0;
  while (openMenus.length > fromDepth) {
    let m = openMenus.pop();
    m.el.remove();
    if (m.owner) m.owner.classList.remove('open');
  }
  if (fromDepth === 0) openTop = null;
}

// items: [{ text, checked, disabled, sep, sub: () => items, click, keepOpen, isChecked }]
function showMenu(depth, owner, itemsFn) {
  closeMenus(depth);
  let items = itemsFn();
  let el = document.createElement('div');
  el.className = 'menu-drop show' + (settings.CheckMarks === 'Traditional' ? ' margin' : '');
  for (let item of items) {
    if (item.sep) { let s = document.createElement('div'); s.className = 'menu-sep'; el.appendChild(s); continue; }
    let row = document.createElement('div');
    row.className = 'menu-item' + (item.checked ? ' checked' : '') + (item.disabled ? ' disabled' : '') + (item.sub ? ' sub' : '');
    row.textContent = item.text;
    if (item.sub) {
      let open = () => { if (!row.classList.contains('open')) showMenu(depth + 1, row, item.sub); };
      row.addEventListener('click', e => { e.stopPropagation(); open(); });
      row.addEventListener('pointerenter', e => { if (e.pointerType === 'mouse') open(); });
    } else {
      row.addEventListener('pointerenter', e => { if (e.pointerType === 'mouse') closeMenus(depth + 1); });
      row.addEventListener('click', e => {
        e.stopPropagation();
        if (item.disabled) return;
        if (item.click) item.click();
        // The Maps dropdowns stay open on a click: only the item's check changes
        if (item.keepOpen) row.classList.toggle('checked', !!(item.isChecked && item.isChecked()));
        else closeMenus(0);
      });
    }
    el.appendChild(row);
  }
  document.body.appendChild(el);

  // Below a menu bar item, to the right of a dropdown item; kept on screen
  let r = owner.getBoundingClientRect(), vw = window.innerWidth, vh = window.innerHeight;
  let x = depth === 0 ? r.left : r.right - 1, y = depth === 0 ? r.bottom : r.top - 2;
  el.style.position = 'fixed';
  el.style.left = '0px'; el.style.top = '0px';
  let w = el.offsetWidth, h = el.offsetHeight;
  if (x + w > vw) x = depth === 0 ? Math.max(0, vw - w) : Math.max(0, r.left - w + 1);
  if (y + h > vh) y = Math.max(0, vh - h);
  el.style.left = x + 'px'; el.style.top = y + 'px';
  el.style.maxHeight = (vh - y) + 'px';

  owner.classList.add('open');
  openMenus.push({ el: el, owner: owner });
}

function addTopMenu(text, itemsFn) {
  let top = document.createElement('div');
  top.className = 'menu-top';
  top.textContent = text;
  top.addEventListener('click', e => {
    e.stopPropagation();
    if (openTop === top) { closeMenus(0); return; }
    showMenu(0, top, itemsFn);
    openTop = top;
  });
  top.addEventListener('pointerenter', e => {
    if (e.pointerType === 'mouse' && openTop && openTop !== top) { showMenu(0, top, itemsFn); openTop = top; }
  });
  menubar.appendChild(top);
}
document.addEventListener('click', () => closeMenus(0));
window.addEventListener('blur', () => closeMenus(0));

// Position: Free, then each group as a submenu, then the ungrouped positions
function positionItems() {
  let items = [{ text: 'Free', checked: freeChecked, click: () => selectPosition(null) }];
  let groups = {};
  for (let p of positions) if (p.Group && p.Group.trim()) (groups[p.Group] = groups[p.Group] || []).push(p);
  let item = p => ({ text: p.Name, checked: position === p, click: () => selectPosition(p) });
  for (let name of Object.keys(groups).sort()) {
    let members = groups[name];
    items.push({ text: name, sub: () => members.map(item) });
  }
  for (let p of positions) if (!p.Group) items.push(item(p));
  return items;
}

function layerKey(m) { return m.Folder ? m.Folder + '/' + m.Name : m.Name; }

// Maps: each folder as a submenu, then the maps outside any folder
function mapItems() {
  let items = [], folders = [], byFolder = {};
  for (let m of mapList) {
    let f = m.Folder || '';
    if (!byFolder[f]) { byFolder[f] = []; folders.push(f); }
    byFolder[f].push(m);
  }
  let item = m => {
    let key = layerKey(m), shown = () => !!(layerState[key] && layerState[key].visible);
    return { text: m.Name, checked: shown(), isChecked: shown, keepOpen: true, click: () => setMapVisible(key, !shown()) };
  };
  for (let f of folders) {
    if (f === '') continue;
    let members = byFolder[f];
    items.push({ text: f, sub: () => members.map(item) });
  }
  for (let m of (byFolder[''] || [])) items.push(item(m));
  return items;
}

// Tools. The entries that act on vatSys's own flight data are shown but
// cannot be used from here.
function selectedTrack() { return aircraft.find(a => a.Syms.some(s => s.Type === 'Selection')) || null; }
function toolItems() {
  let brl = { text: 'Bearing Range Line', disabled: !selectedTrack(), click: addBrlFromSelection };
  if (GROUND) return [brl, { text: 'Contact Me', disabled: true }, { text: 'Decouple', disabled: true }];
  return [
    { text: 'Add Text Area', disabled: true },
    { sep: true },
    { text: 'Quick Tag', disabled: true },
    brl,
    { text: 'Conflict Area', disabled: true },
    { text: 'Closest Approach', disabled: true },
  ];
}

addTopMenu('Position', positionItems);
addTopMenu('Maps', mapItems);
addTopMenu('Tools', toolItems);

/* ============================================================
   Positions and maps: LoadPosition / SetMapVisible
   ============================================================ */

let freeChecked = false;
let selectSeq = 0;

function setInteractive(on) { viewEl.classList.toggle('interactive', !!on); }

function setMapVisible(key, visible) {
  let ls = layerState[key] || (layerState[key] = { visible: false, layer: null });
  ls.visible = visible;
  if (visible && !ls.layer) loadLayers([key]);
  backgroundDirty = true;
  scheduleRender();
}

function loadLayers(keys) {
  if (keys.length === 0) return Promise.resolve();
  return api('/api/maplayers?maps=' + encodeURIComponent(keys.join(','))).then(layers => {
    if (!Array.isArray(layers)) return;
    for (let layer of layers) {
      let key = layer.Folder ? layer.Folder + '/' + layer.Name : layer.Name;
      (layerState[key] || (layerState[key] = { visible: false, layer: null })).layer = layer;
    }
    backgroundDirty = true;
    scheduleRender();
  }).catch(() => { });
}

function selectPosition(pos) {
  position = pos;
  if (!pos) {
    // LoadPosition(null): only the rotation is reset; centre, zoom and maps stay
    freeChecked = true;
    rotation = 0;
    backgroundDirty = true;
    setTitle(WINDOW_NAME);
    try { localStorage.removeItem(STORE_KEY); } catch (e) { }
    scheduleRender();
    pollAircraft();
    return;
  }

  freeChecked = false;
  setCentre(pos.CenterLat, pos.CenterLon);
  setZoom(pos.DefaultRange, false);
  rotation = Math.fround(Math.fround(Math.fround(pos.Rotation) + Math.fround(pos.MagneticVariation)) * D2R);
  for (let key in layerState) layerState[key].visible = false;
  let missing = [];
  for (let key of (pos.MapRefs || [])) {
    let ls = layerState[key] || (layerState[key] = { visible: false, layer: null });
    ls.visible = true;
    if (!ls.layer) missing.push(key);
  }
  backgroundDirty = true;
  setTitle(WINDOW_NAME + ': ' + pos.Name);
  try { localStorage.setItem(STORE_KEY, pos.Name); } catch (e) { }
  loadLayers(missing);
  scheduleRender();
  pollAircraft();
}

/* ============================================================
   Pointer handling. Mouse buttons behave as in vatSys: right-click moves a
   label or draws a zoom box, middle-drag is the quick look-around pan.
   Touch has no vatSys counterpart: drag pans, pinch zooms, a tap picks up
   and puts down a label, double-tap returns to the position's default view.
   ============================================================ */

function clientPoint(e) { let r = cv.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; }
function inArea(a, x, y) { return x >= a[0] && x <= a[0] + a[2] && y >= a[1] && y <= a[1] + a[3]; }

// The track clickspot under a client point (the topmost painted track first)
function trackAt(x, y, touch) {
  for (let i = aircraft.length - 1; i >= 0; i--) {
    let ac = aircraft[i], p = ac._p;
    if (!p) continue;
    let half = touch ? 20 : (GROUND ? 6 : 11);
    if (Math.abs(p[0] + CX - x) <= half && Math.abs(p[1] + CY - y) <= half) return ac;
  }
  return null;
}
function spotAt(x, y, kind) {
  for (let i = clickspots.length - 1; i >= 0; i--) if (clickspots[i].kind === kind && inArea(clickspots[i].area, x, y)) return clickspots[i];
  return null;
}

// GetLabelOrientation: direction (22.5 degree steps) and leader length (0-2)
// from the track to the pointer
function labelOrientation(tx, ty, mx, my) {
  let dy = my - ty, dxx = mx - tx;
  if (dxx === 0 && dy === 0) return [LABEL_DIR_DEFAULT, LABEL_LEN_DEFAULT];
  let angle = Math.atan(dy / Math.abs(dxx)) * 180 / Math.PI;
  if (dxx < 0) angle = 360 - angle + 180;
  if (angle > 360) angle -= 360;
  let dist = Math.sqrt(dy * dy + dxx * dxx) - 5;
  let len = Math.round(dist / 35);
  return [Math.round(angle / 360 * 16), len < 3 ? Math.max(0, len) : 2];
}

function beginLabelMove(ac) {
  if (!ac || !ac.Label) return false;
  let d = labelDirection(ac);
  labelMoving = { id: ac.Id, dir: d[0], len: d[1] };
  setInteractive(true);
  return true;
}
function trackLabelMove(x, y) {
  let ac = trackById(labelMoving.id);
  if (!ac || !ac._p) return;
  let o = labelOrientation(Math.round(ac._p[0]) + CX, Math.round(ac._p[1]) + CY, x, y);
  labelMoving.dir = o[0]; labelMoving.len = o[1];
}
function endLabelMove(x, y) {
  trackLabelMove(x, y);
  labelState[labelMoving.id] = { dir: labelMoving.dir, len: labelMoving.len };
  labelMoving = null;
  setInteractive(false);
}

function addBrlFromSelection() {
  let ac = selectedTrack();
  if (!ac) return;
  brls.push({ origin: [ac.Lat, ac.Lon], end: [ac.Lat, ac.Lon], originId: ac.Id, endId: null, type: settings.BrlLabelType || 'Normal' });
  activeBrl = brls.length - 1;
  setInteractive(true);
  scheduleRender();
}
function placeBrl(x, y, touch) {
  let ac = trackAt(x, y, touch);
  if (ac) brls[activeBrl].endId = ac.Id;
  activeBrl = -1;
  setInteractive(false);
}

function moveToolTo(r, x, y, centred) {
  r.x = centred ? x - r.w / 2 : x; r.y = centred ? y - r.h / 2 : y;
  clampToClient(r);
}

// The zoom box grows from its centre, keeping the window's aspect ratio
function growZoomBox(x, y) {
  let w = Math.abs(x - zooming.cx) * 2, h = Math.abs(y - zooming.cy) * 2, aspect = W / H;
  if (h * aspect > w) w = h * aspect; else h = w / aspect;
  zooming.rect = [zooming.cx - Math.trunc(Math.trunc(w) / 2), zooming.cy - Math.trunc(Math.trunc(h) / 2), Math.trunc(w), Math.trunc(h)];
}
function applyZoomBox() {
  let r = zooming.rect, cy = r[1] + r[3] / 2;
  let newCentre = s2ll(zooming.cx, zooming.cy);
  let nRange = distanceNm(s2ll(r[0], cy), s2ll(r[0] + r[2], cy));
  zooming = null;
  setInteractive(false);
  if (!(nRange > 0)) return;
  setCentre(newCentre[0], newCentre[1]);
  setZoom(nRange, false);
}

// The position's default view (the DefaultASD key)
function defaultView() {
  if (!position) return;
  setCentre(position.CenterLat, position.CenterLon);
  setZoom(position.DefaultRange, false);
}

let pointers = new Map();
let pan = null;        // touch / left-drag pan: { x, y }
let pinch = null;      // { dist, range, ll }
let quickPan = null;   // middle-drag: { centre, x, y }
let tap = null;        // { x, y, t } of a touch that has not moved
let lastTap = 0;

cv.addEventListener('contextmenu', e => e.preventDefault());

function movingSomething() { return labelMoving || activeBrl !== -1 || atcMoving || scaleMoving || zooming; }

function rightDown(x, y) {
  if (zooming) { applyZoomBox(); return; }
  if (atcMoving) { atcRect = atcMoving; atcMoving = null; setInteractive(false); return; }
  if (scaleMoving) { scaleRect = scaleMoving; scaleMoving = null; setInteractive(false); return; }
  if (labelMoving) { endLabelMove(x, y); return; }

  let ac = trackAt(x, y, false);
  if (ac && beginLabelMove(ac)) return;
  if (spotAt(x, y, 'atc')) { atcMoving = Object.assign({}, atcRect); setInteractive(true); return; }
  if (spotAt(x, y, 'scale')) { scaleMoving = Object.assign({}, scaleRect); setInteractive(true); return; }
  if (activeBrl !== -1) return;
  zooming = { cx: x, cy: y, rect: [x, y, 0, 0] };
  setInteractive(true);
}

function middleDown(x, y) {
  if (zooming) { zooming = null; setInteractive(false); return; }
  if (atcMoving) { atcMoving = null; setInteractive(false); return; }
  if (scaleMoving) { scaleMoving = null; setInteractive(false); return; }
  if (activeBrl !== -1) { brls.splice(activeBrl, 1); activeBrl = -1; setInteractive(false); return; }
  if (labelMoving) return;
  if (spotAt(x, y, 'scale')) { scaleMoving = Object.assign({}, scaleRect); setInteractive(true); return; }
  quickPan = { centre: centre, x: x, y: y };
  setInteractive(true);
}

cv.addEventListener('pointerdown', e => {
  closeMenus(0);
  cv.setPointerCapture(e.pointerId);
  let m = clientPoint(e), x = m[0], y = m[1];

  if (e.pointerType === 'mouse') {
    if (e.button === 2) { rightDown(x, y); scheduleRender(); return; }
    if (e.button === 1) { e.preventDefault(); middleDown(x, y); scheduleRender(); return; }
    if (e.button !== 0) return;
    if (activeBrl !== -1) { placeBrl(x, y, false); scheduleRender(); return; }
    let spot = spotAt(x, y, 'brl');
    if (spot) { activeBrl = spot.value; brls[activeBrl].endId = null; setInteractive(true); scheduleRender(); return; }
    if (movingSomething()) return;
    pan = { x: x, y: y };
    pointers.set(e.pointerId, { x: x, y: y });
    return;
  }

  // Touch / pen
  pointers.set(e.pointerId, { x: x, y: y });
  if (pointers.size === 1) {
    tap = { x: x, y: y, t: Date.now() };
    pan = movingSomething() ? null : { x: x, y: y };
    pinch = null;
  } else if (pointers.size === 2) {
    tap = null; pan = null;
    let pts = [...pointers.values()];
    let mx = (pts[0].x + pts[1].x) / 2, my = (pts[0].y + pts[1].y) / 2;
    pinch = { dist: Math.hypot(pts[1].x - pts[0].x, pts[1].y - pts[0].y), range: range, ll: s2ll(mx, my) };
  }
});

cv.addEventListener('pointermove', e => {
  let m = clientPoint(e), x = m[0], y = m[1];
  cursorLL = s2ll(x, y);

  if (zooming) { growZoomBox(x, y); scheduleRender(); }
  else if (labelMoving) { trackLabelMove(x, y); scheduleRender(); }
  else if (activeBrl !== -1) { brls[activeBrl].end = cursorLL; brls[activeBrl].endId = null; scheduleRender(); }
  else if (atcMoving) { moveToolTo(atcMoving, x, y, false); scheduleRender(); }
  else if (scaleMoving) { moveToolTo(scaleMoving, x, y, true); scheduleRender(); }
  else if (quickPan) {
    // The view follows the drag, measured from where the pan began
    let live = centre;
    centre = quickPan.centre; pj = null;
    let c = s2ll(CX - (x - quickPan.x), CY - (y - quickPan.y));
    centre = live; pj = null;
    setCentre(c[0], c[1]);
    scheduleRender();
  }

  if (!pointers.has(e.pointerId)) return;
  pointers.set(e.pointerId, { x: x, y: y });
  if (tap && Math.hypot(x - tap.x, y - tap.y) > 8) tap = null;

  if (pointers.size === 1 && pan && !movingSomething()) {
    if (e.pointerType === 'mouse' || !tap) {
      anchorAt(s2ll(pan.x, pan.y), x, y);
      pan = { x: x, y: y };
      scheduleRender();
    }
  } else if (pointers.size === 2 && pinch) {
    let pts = [...pointers.values()];
    let dist = Math.hypot(pts[1].x - pts[0].x, pts[1].y - pts[0].y);
    if (pinch.dist > 10 && dist > 0) {
      setZoom(pinch.range * pinch.dist / dist, false);
      anchorAt(pinch.ll, (pts[0].x + pts[1].x) / 2, (pts[0].y + pts[1].y) / 2);
      scheduleRender();
    }
  }
});

function pointerEnd(e) {
  let m = clientPoint(e), x = m[0], y = m[1];
  if (e.pointerType === 'mouse' && e.button === 1 && quickPan) {
    // Releasing the middle button returns to where the pan started
    setCentre(quickPan.centre.lat, quickPan.centre.lon);
    quickPan = null; setInteractive(false); scheduleRender();
  }
  let had = pointers.delete(e.pointerId);
  if (e.pointerType !== 'mouse' && had && e.type === 'pointerup') {
    if (labelMoving && pointers.size === 0) { endLabelMove(x, y); scheduleRender(); }
    else if (activeBrl !== -1 && pointers.size === 0) { placeBrl(x, y, true); scheduleRender(); }
    else if (atcMoving) { atcRect = atcMoving; atcMoving = null; setInteractive(false); scheduleRender(); }
    else if (scaleMoving) { scaleRect = scaleMoving; scaleMoving = null; setInteractive(false); scheduleRender(); }
    else if (tap && Date.now() - tap.t < 500) {
      let now = Date.now();
      let ac = trackAt(x, y, true), spot;
      if (ac && beginLabelMove(ac)) { }
      else if ((spot = spotAt(x, y, 'brl'))) { activeBrl = spot.value; brls[activeBrl].endId = null; setInteractive(true); }
      else if (spotAt(x, y, 'atc')) { atcMoving = Object.assign({}, atcRect); setInteractive(true); }
      else if (spotAt(x, y, 'scale')) { scaleMoving = Object.assign({}, scaleRect); setInteractive(true); }
      else if (now - lastTap < 350) { defaultView(); lastTap = 0; }
      else lastTap = now;
      scheduleRender();
    }
  }
  tap = null;
  if (pointers.size === 1) { let pt = [...pointers.values()][0]; pan = movingSomething() ? null : { x: pt.x, y: pt.y }; pinch = null; }
  else if (pointers.size === 0) { pan = null; pinch = null; }
}
cv.addEventListener('pointerup', pointerEnd);
cv.addEventListener('pointercancel', pointerEnd);
cv.addEventListener('pointerleave', e => {
  if (quickPan && e.pointerType === 'mouse' && !cv.hasPointerCapture(e.pointerId)) {
    setCentre(quickPan.centre.lat, quickPan.centre.lon);
    quickPan = null; setInteractive(false); scheduleRender();
  }
});
cv.addEventListener('auxclick', e => e.preventDefault());

// The range keys step by 0.9 / 1.1; the wheel does the same about the cursor
cv.addEventListener('wheel', e => {
  e.preventDefault();
  let m = clientPoint(e), ll = s2ll(m[0], m[1]);
  setZoom(range * (e.deltaY > 0 ? 1.100000023841858 : 0.8999999761581421), false);
  anchorAt(ll, m[0], m[1]);
  scheduleRender();
}, { passive: false });

cv.addEventListener('dblclick', e => { if (e.button === 0 && !movingSomething()) { defaultView(); scheduleRender(); } });

/* ============================================================
   Data
   ============================================================ */

let staticRetries = 0;
function loadStatic() {
  api('/api/static?type=' + TYPE).then(data => {
    positions = Array.isArray(data.Positions) ? data.Positions : [];
    mapList = Array.isArray(data.Maps) ? data.Maps : [];
    if (Array.isArray(data.Symbols) && data.Symbols.length > 0) buildSymTable(data.Symbols);

    // Reopen on the position this window last showed
    if (!position) {
      let saved = null;
      try { saved = localStorage.getItem(STORE_KEY); } catch (e) { }
      let pos = saved ? positions.find(p => p.Name === saved) : null;
      if (pos) selectPosition(pos);
    }
    scheduleRender();
    // The lists are empty until vatSys has finished loading the profile
    if ((mapList.length === 0 || positions.length === 0) && staticRetries++ < 40) setTimeout(loadStatic, 3000);
  }).catch(() => { if (staticRetries++ < 40) setTimeout(loadStatic, 3000); });
}

let lastState = {};
function changed(name, value) {
  let text = JSON.stringify(value);
  if (lastState[name] === text) return false;
  lastState[name] = text;
  return true;
}

function pollState() {
  api('/api/state?type=' + TYPE).then(data => {
    if (data.Colours && changed('colours', data.Colours)) {
      colours = data.Colours; applyTheme(); clearGlyphs(); hatch = null; backgroundDirty = true;
    }
    if (data.Fonts && changed('fonts', data.Fonts)) { fonts = data.Fonts; clearGlyphs(); backgroundDirty = true; }
    if (data.Dynamic && changed('dynamic', data.Dynamic)) {
      dynamicBelow = data.Dynamic.Below || []; dynamicAbove = data.Dynamic.Above || [];
      backgroundDirty = true;
    }
    if (data.Overlays) { overlays = data.Overlays; settings = data.Overlays.Settings || {}; }
    if (Array.isArray(data.VisibleMaps)) syncCrossingMaps(data.VisibleMaps);
    scheduleRender();
  }).catch(() => { });
}

// Crossing / Release maps follow the vatSys Ground window (OzStrips toggles them)
function syncCrossingMaps(keys) {
  let missing = [], dirty = false;
  for (let key of keys) {
    let ls = layerState[key] || (layerState[key] = { visible: false, layer: null });
    if (!ls.visible) { ls.visible = true; dirty = true; }
    if (!ls.layer) missing.push(key);
  }
  for (let key in layerState) {
    let ls = layerState[key];
    if (!ls.layer || !ls.visible) continue;
    let custom = (ls.layer.CustomColour || '').toLowerCase();
    if ((custom === 'release' || custom === 'crossing') && keys.indexOf(key) < 0) { ls.visible = false; dirty = true; }
  }
  if (dirty) backgroundDirty = true;
  loadLayers(missing);
}

function pollAircraft() {
  if (!(W > 0) || !(range > 0)) return;
  // Everything within reach of the window: its far corner, the 50 px margin
  // symbols are still painted in, and room for a label hanging off a track
  let reach = (Math.hypot(W, H) / 2 + 350) * range / W;
  let q = '/api/aircraft?type=' + TYPE + '&lat=' + centre.lat.toFixed(6) + '&lon=' + centre.lon.toFixed(6) + '&range=' + reach.toFixed(3);
  if (GROUND) q += '&icao=' + encodeURIComponent(position ? position.Airport : '');
  api(q).then(data => {
    aircraft = Array.isArray(data) ? data : [];
    scheduleRender();
  }).catch(() => { });
}

/* ---------- Start ---------- */

setTitle(GROUND ? 'Ground' : 'ASD: Free');
window.addEventListener('resize', () => { resize(); fitTitle(); });
if (window.ResizeObserver) new ResizeObserver(() => resize()).observe(viewEl);
resize();
applyTheme();

// Glyphs are rasterised from the web font: redraw once it has arrived
if (document.fonts && document.fonts.load) {
  Promise.all([document.fonts.load('bold 16px Terminus'), document.fonts.load('bold 18px Terminus')])
    .then(() => { clearGlyphs(); backgroundDirty = true; scheduleRender(); }).catch(() => { });
}

// The label glyphs come from the font file's bitmap strikes
loadBitmapFont().then(() => scheduleRender());

loadStatic();
pollState();
setInterval(pollState, 2000);
setInterval(pollAircraft, 500);
