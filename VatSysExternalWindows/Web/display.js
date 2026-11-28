'use strict';
/* ============================================================
   vatSys ASD / Ground window renderer.

   A port of vatsys.ASDControlDX: one control draws both window types and
   differs only by its type, so this one script serves /asd and /ground.
   Everything is drawn in vatSys's own coordinate frame: pixels relative
   to the window centre (ClientSize / 2, integer division).

   The plugin decides what is painted (which maps, symbols, label cells,
   colours); this file projects it and draws it the way Render does.
   ============================================================ */

const GROUND = /^\/ground/i.test(location.pathname);
const TYPE = GROUND ? 'ground' : 'asd';

const cv = document.getElementById('cv');
const ctx = cv.getContext('2d');
const viewEl = document.getElementById('view');

/* ---------- Window state (one ASDControlDX instance) ---------- */

let W = 0, H = 0;          // client size, whole pixels
let CX = 0, CY = 0;        // ClientSize / 2 (integer division): the drawing origin
let SCALE = 1;             // device pixels per client pixel

const VIEW_SCALE = 1000;   // RenderParams.ViewScale
// MMI.MinimumASDRange / MaximumASDRange
const MIN_RANGE = GROUND ? 0.1 : 10, MAX_RANGE = GROUND ? 30 : 9999;
const DEF_RANGE = 999;     // ASDControlDX.DefRange

let centre = { lat: 0, lon: 0 };   // RenderParams.ScreenCentre
let zoom = 1;                      // RenderParams.Zoom
let rotation = 0;                  // RenderParams.Rotation (radians)
let range = -1;                    // ASDControlDX.range: NM across the window width
let position = null;               // displayPosition

let colours = { Dx: {}, Gdi: {}, Custom: {} };
let fonts = {
  Family: 'Terminus (TTF)', BaselineShift: 0.288,
  LabelFontSize: 18, LabelFontWidth: 9, LabelFontHeight: 16,
  MapFontSize: 16, MapFontWidth: 8, MapFontHeight: 15,
  SmallFontSize: 16, SmallFontWidth: 8, SmallFontHeight: 15
};
let settings = {};
let overlays = {};
let dynamicBelow = [], dynamicAbove = [];
let layerState = {};       // map key -> { visible, layer }
let aircraft = [];
let symTable = {};

// FlashShareTimer: the flash phase flips every 600 ms, timeshare every fourth tick
let flashOn = false, timeshareOn = false, flashCount = 0;

const D2R = Math.PI / 180;
const FONT_STACK = "'Terminus','Consolas','Courier New',monospace";

/* ---------- Colours ---------- */

function css(c) {
  if (!c) return 'rgb(139,0,139)'; // GetColourDX falls back to DarkMagenta
  return c.length > 3 ? 'rgba(' + c[0] + ',' + c[1] + ',' + c[2] + ',' + (c[3] / 255) + ')' : 'rgb(' + c[0] + ',' + c[1] + ',' + c[2] + ')';
}
function dx(id) { return css(colours.Dx[id]); }
function backgroundColour() { return dx(GROUND ? 'GroundBackground' : 'ASDBackground'); }

// SelectMapBrush
function mapBrush(layer) {
  if (layer.CustomColour) {
    let c = colours.Custom[layer.CustomColour];
    return c ? css(c) : 'rgb(240,248,255)'; // GetCustomColorDX falls back to AliceBlue
  }
  switch (layer.Type) {
    case 'System2': case 'Flex': return dx('SecondaryMap');
    case 'Local_Private': case 'Global_Private': return dx('UserMap');
    case 'Supervisor': case 'TDA': case 'REST_NTZ_DAIW': return dx('PRDArea');
    case 'Ground_RWY': return dx('GroundRunway');
    case 'Ground_TWY': return dx('GroundTaxiway');
    case 'Ground_APR': return dx('GroundApron');
    case 'Ground_BLD': return dx('GroundBuilding');
    case 'Ground_INF': return dx('GroundOther');
    case 'Filled': return dx('Infill');
    default: return dx('PrimaryMap');
  }
}

/* ============================================================
   Projection: ConvertLLToScreen / ConvertScreenToLL / CalculateZoom /
   SetZoom / CalculateRange. Stereographic about the screen centre.
   ============================================================ */

let pj = null;
function proj() {
  if (!pj || pj.lat !== centre.lat || pj.lon !== centre.lon || pj.zoom !== zoom || pj.rot !== rotation) {
    let la = centre.lat * D2R;
    pj = {
      lat: centre.lat, lon: centre.lon, zoom: zoom, rot: rotation,
      sin0: Math.sin(la), cos0: Math.cos(la), lon0: centre.lon * D2R,
      k: VIEW_SCALE * zoom, cr: Math.cos(rotation), sr: Math.sin(rotation)
    };
  }
  return pj;
}

// Lat/lon -> pixels relative to the window centre (single precision, as Vector2 is)
function ll2s(lat, lon) {
  let p = proj(), la = lat * D2R, dl = lon * D2R - p.lon0;
  let sl = Math.sin(la), cl = Math.cos(la), cd = Math.cos(dl);
  let k = 2 / (1 + p.sin0 * sl + p.cos0 * cl * cd);
  let x = Math.fround(k * cl * Math.sin(dl) * p.k);
  let y = Math.fround(k * (p.cos0 * sl - p.sin0 * cl * cd) * p.k * -1);
  return [Math.fround(x * p.cr - y * p.sr), Math.fround(x * p.sr + y * p.cr)];
}

// Client pixel -> lat/lon. (px, py) are relative to the client area's top-left.
function s2ll(px, py) {
  let p = proj(), dx0 = px - CX, dy0 = py - CY;
  let x = (dx0 * p.cr + dy0 * p.sr) / p.k, y = (-dx0 * p.sr + dy0 * p.cr) / (p.k * -1);
  let rho = Math.sqrt(x * x + y * y);
  if (rho === 0) return [centre.lat, centre.lon];
  let c = 2 * Math.atan(rho / 2), sc = Math.sin(c), cc = Math.cos(c);
  let lat = Math.asin(Math.max(-1, Math.min(1, cc * p.sin0 + y * sc * p.cos0 / rho)));
  let lon = p.lon0 + Math.atan2(x * sc, rho * p.cos0 * cc - y * p.sin0 * sc);
  return checkExtents(lat / D2R, lon / D2R);
}

// Conversions.CheckLLExtents
function checkExtents(lat, lon) {
  if (Math.abs(lat) > 90) lat = Math.asin(Math.sin(lat * D2R)) / D2R;
  return [lat, ((lon % 360) + 540) % 360 - 180];
}

// Conversions.CalculateLLFromBearingRange (1 NM = 1 arc minute)
function fromBearingRange(lat, lon, distNm, bearingDeg) {
  let la = lat * D2R, b = bearingDeg * D2R, d = distNm * Math.PI / 10800;
  let la2 = Math.asin(Math.sin(la) * Math.cos(d) + Math.cos(la) * Math.sin(d) * Math.cos(b));
  let dlo = Math.atan2(Math.sin(b) * Math.sin(d) * Math.cos(la), Math.cos(d) - Math.sin(la) * Math.sin(la2));
  return checkExtents(la2 / D2R, lon + dlo / D2R);
}

// Conversions.CalculateDistance (NM)
function distanceNm(a, b) {
  let la1 = a[0] * D2R, la2 = b[0] * D2R;
  let s1 = Math.sin((la1 - la2) / 2), s2 = Math.sin((a[1] - b[1]) * D2R / 2);
  return 2 * Math.asin(Math.sqrt(s1 * s1 + Math.cos(la1) * Math.cos(la2) * s2 * s2)) * 10800 / Math.PI;
}

// Conversions.CalculateTrack (degrees true)
function trackDeg(a, b) {
  let la1 = a[0] * D2R, la2 = b[0] * D2R, lo1 = -a[1] * D2R, lo2 = -b[1] * D2R;
  let t = Math.atan2(Math.sin(lo1 - lo2) * Math.cos(la2),
    Math.cos(la1) * Math.sin(la2) - Math.sin(la1) * Math.cos(la2) * Math.cos(lo1 - lo2)) % (Math.PI * 2);
  if (t < 0) t += Math.PI * 2;
  return t / D2R;
}

// CalculateStereographicNorthCorrection: the angle of true north at a point
function northCorrection(lat, lon) {
  if (Math.abs(centre.lat) === 90) return 0;
  let p = proj(), la = lat * D2R, dl = lon * D2R - p.lon0;
  let sl = Math.sin(la), cl = Math.cos(la), cd = Math.cos(dl);
  let y = 2 / (1 + p.sin0 * sl + p.cos0 * cl * cd) * (p.cos0 * sl - p.sin0 * cl * cd);
  return Math.PI / 2 - Math.acos((-2 * Math.tan(centre.lat * D2R) - y) / (2 / (p.cos0 * Math.sin(dl))));
}

// CalculateRange: the distance between the middle of the left and right edges
function calculateRange() {
  let y = Math.trunc(H / 2);
  return distanceNm(s2ll(0, y), s2ll(W, y));
}

// SetZoom
function setZoom(nRange, update) {
  if (!(W > 0)) return;
  if (!update && nRange >= 10 && Math.round(nRange) === Math.round(range)) return;
  nRange = Math.min(Math.max(MIN_RANGE, nRange), MAX_RANGE);
  let half = fromBearingRange(centre.lat, centre.lon, nRange / 2, 90);
  // CalculateZoom
  let p = proj(), la = half[0] * D2R, dl = half[1] * D2R - p.lon0;
  let k = 2 / (1 + p.sin0 * Math.sin(la) + p.cos0 * Math.cos(la) * Math.cos(dl));
  let val = (W / 2) / (k * Math.cos(la) * Math.sin(dl) * VIEW_SCALE);
  if (!isFinite(val)) return;
  zoom = Math.min(Math.max(0.5, val), 16000);
  range = calculateRange();
  backgroundDirty = true;
}

// SetDisplayCenter
function setCentre(lat, lon) {
  let c = checkExtents(lat, lon);
  if (!isFinite(c[0]) || !isFinite(c[1])) return;
  centre = { lat: c[0], lon: c[1] };
  backgroundDirty = true;
}

// Move the screen centre so that the lat/lon sits under client point (px, py)
// (used by the page's own pan and pinch gestures)
function anchorAt(ll, px, py) {
  for (let i = 0; i < 3; i++) {
    let p = ll2s(ll[0], ll[1]);
    let c = s2ll(CX + (p[0] + CX - px), CY + (p[1] + CY - py));
    if (!isFinite(c[0]) || !isFinite(c[1])) return;
    setCentre(Math.max(-89.9, Math.min(89.9, c[0])), c[1]);
  }
}

// RenderParams.ClipRectangle: the client area grown by 50 px, centre-relative
function inClip(p) {
  return p[0] >= -CX - 50 && p[0] <= -CX + W + 50 && p[1] >= -CY - 50 && p[1] <= -CY + H + 50;
}
function clipIntersects(x, y, w, h) {
  return x < -CX + W + 50 && -CX - 50 < x + w && y < -CY + H + 50 && -CY - 50 < y + h;
}

// The System.Drawing.Rectangle test PaintMap uses to skip off-window segments
function segmentVisible(a, b) {
  let rx = Math.trunc(Math.min(a[0], b[0])) + CX, ry = Math.trunc(Math.min(a[1], b[1])) + CY;
  let rw = Math.trunc(Math.abs(b[0] - a[0])), rh = Math.trunc(Math.abs(b[1] - a[1]));
  return rx < W && 0 < rx + rw && ry < H && 0 < ry + rh;
}

/* ============================================================
   Drawing primitives matching Direct2D's behaviour
   ============================================================ */

// An aliased 1px Direct2D stroke lights the pixel whose centre lies in
// [v - 0.5, v + 0.5): for a whole-number coordinate that is the pixel before it.
function px(v) { return Math.ceil(v) - 1; }

// dc.DrawRectangle in AntialiasMode.Aliased
function aliasedRect(g, x, y, w, h, colour) {
  let l = px(x), r = px(x + w), t = px(y), b = px(y + h);
  g.fillStyle = colour;
  g.fillRect(l, t, r - l + 1, 1);
  g.fillRect(l, b, r - l + 1, 1);
  g.fillRect(l, t, 1, b - t + 1);
  g.fillRect(r, t, 1, b - t + 1);
}
// dc.DrawLine in AntialiasMode.Aliased, horizontal or vertical
function aliasedHLine(g, x0, x1, y, colour) {
  let a = Math.ceil(x0 - 0.5), b = Math.ceil(x1 - 0.5);
  g.fillStyle = colour; g.fillRect(a, px(y), b - a, 1);
}
function aliasedVLine(g, x, y0, y1, colour) {
  let a = Math.ceil(y0 - 0.5), b = Math.ceil(y1 - 0.5);
  g.fillStyle = colour; g.fillRect(px(x), a, 1, b - a);
}
// DrawTextBorder
function textBorder(g, x, y, w, h, colour, border) {
  if (!border) return;
  if (border === 15) { aliasedRect(g, x, y, w, h, colour); return; }
  if (border & 1) aliasedVLine(g, x, y, y + h, colour);
  if (border & 2) aliasedVLine(g, x + w, y, y + h, colour);
  if (border & 4) aliasedHLine(g, x, x + w, y, colour);
  if (border & 8) aliasedHLine(g, x, x + w, y + h, colour);
}

function line(g, a, b, colour, width, dash) {
  g.strokeStyle = colour; g.lineWidth = width || 1; g.lineCap = 'butt';
  g.setLineDash(dash || []);
  g.beginPath(); g.moveTo(a[0], a[1]); g.lineTo(b[0], b[1]); g.stroke();
  if (dash) g.setLineDash([]);
}

/* ---------- Aliased text ----------
   The tracks/tools layer is drawn with TextAntialiasMode.Aliased. At the sizes
   vatSys uses, DirectWrite then takes the glyphs from the bitmap strikes
   embedded in the Terminus TTF (12 to 28 px), not from its outlines. A browser
   only ever draws the outlines, so the strikes are read from the font file
   here and blitted pixel for pixel. */

let bitmapFont = null;       // { glyphOf(code), strikes: { ppem: strike } }
let glyphCache = new Map();
function clearGlyphs() { glyphCache.clear(); }

function fontCss(size) { return 'bold ' + size + 'px ' + FONT_STACK; }

// Parses the cmap and the EBLC / EBDT embedded bitmap tables of a TrueType font
function parseBitmapFont(buffer) {
  let d = new DataView(buffer), tables = {};
  let count = d.getUint16(4);
  for (let i = 0; i < count; i++) {
    let o = 12 + i * 16;
    tables[String.fromCharCode(d.getUint8(o), d.getUint8(o + 1), d.getUint8(o + 2), d.getUint8(o + 3))] = d.getUint32(o + 8);
  }
  if (tables.cmap === undefined || tables.EBLC === undefined || tables.EBDT === undefined) return null;

  // Unicode BMP character map (platform 3, encoding 1, format 4)
  let glyphOf = null, c = tables.cmap, subtables = d.getUint16(c + 2);
  for (let i = 0; i < subtables; i++) {
    let off = c + d.getUint32(c + 8 + i * 8);
    if (d.getUint16(c + 4 + i * 8) !== 3 || d.getUint16(c + 6 + i * 8) !== 1 || d.getUint16(off) !== 4) continue;
    let segX2 = d.getUint16(off + 6), endO = off + 14, startO = endO + segX2 + 2, deltaO = startO + segX2, rangeO = deltaO + segX2;
    glyphOf = code => {
      for (let seg = 0; seg < segX2; seg += 2) {
        if (code > d.getUint16(endO + seg)) continue;
        let first = d.getUint16(startO + seg);
        if (code < first) return 0;
        let delta = d.getInt16(deltaO + seg), ro = d.getUint16(rangeO + seg);
        if (ro === 0) return (code + delta) & 0xFFFF;
        let g = d.getUint16(rangeO + seg + ro + (code - first) * 2);
        return g === 0 ? 0 : (g + delta) & 0xFFFF;
      }
      return 0;
    };
  }
  if (!glyphOf) return null;

  let E = tables.EBLC, D = tables.EBDT, strikes = {};
  let sizes = d.getUint32(E + 4);
  for (let i = 0; i < sizes; i++) {
    let s = E + 8 + i * 48;
    if (d.getUint8(s + 46) !== 1) continue; // 1 bit per pixel only
    let arr = E + d.getUint32(s), n = d.getUint32(s + 8), subs = [];
    for (let k = 0; k < n; k++) {
      let sub = arr + d.getUint32(arr + k * 8 + 4);
      subs.push({ first: d.getUint16(arr + k * 8), last: d.getUint16(arr + k * 8 + 2), sub: sub,
        indexFormat: d.getUint16(sub), imageFormat: d.getUint16(sub + 2), data: D + d.getUint32(sub + 4) });
    }
    strikes[d.getUint8(s + 45)] = { ascender: d.getInt8(s + 16), descender: d.getInt8(s + 17), subs: subs };
  }

  // One glyph of a strike: { h, w, bx, by, adv, rows: [Uint8Array of 0/1] }, or null
  function bitmap(strike, gid, depth) {
    for (let x of strike.subs) {
      if (gid < x.first || gid > x.last) continue;
      let idx = gid - x.first, off, len, m = null;
      if (x.indexFormat === 1) { off = d.getUint32(x.sub + 8 + idx * 4); len = d.getUint32(x.sub + 12 + idx * 4) - off; }
      else if (x.indexFormat === 3) { off = d.getUint16(x.sub + 8 + idx * 2); len = d.getUint16(x.sub + 10 + idx * 2) - off; }
      else if (x.indexFormat === 2) {
        len = d.getUint32(x.sub + 8); off = idx * len;
        m = { h: d.getUint8(x.sub + 12), w: d.getUint8(x.sub + 13), bx: d.getInt8(x.sub + 14), by: d.getInt8(x.sub + 15), adv: d.getUint8(x.sub + 16) };
      } else return null;
      if (len === 0) return null;

      let p = x.data + off, f = x.imageFormat;
      let small = () => { m = { h: d.getUint8(p), w: d.getUint8(p + 1), bx: d.getInt8(p + 2), by: d.getInt8(p + 3), adv: d.getUint8(p + 4) }; };
      let rows = [];
      if (f === 8 || f === 9) {
        // Composite: other glyphs of the strike placed inside this one's box
        if (depth > 4) return null;
        small(); p += f === 8 ? 6 : 8;
        for (let r = 0; r < m.h; r++) rows.push(new Uint8Array(m.w));
        let parts = d.getUint16(p); p += 2;
        for (let k = 0; k < parts; k++, p += 4) {
          let part = bitmap(strike, d.getUint16(p), (depth || 0) + 1);
          if (!part) continue;
          let ox = d.getInt8(p + 2), oy = d.getInt8(p + 3);
          for (let r = 0; r < part.h; r++) for (let q = 0; q < part.w; q++) {
            if (part.rows[r][q] && r + oy >= 0 && r + oy < m.h && q + ox >= 0 && q + ox < m.w) rows[r + oy][q + ox] = 1;
          }
        }
        m.rows = rows;
        return m;
      }

      let bitAligned;
      if (f === 1 || f === 2) { small(); p += 5; bitAligned = f === 2; }
      else if (f === 6 || f === 7) { small(); p += 8; bitAligned = f === 7; }
      else if (f === 5 && m) bitAligned = true;
      else return null;
      let bit = 0;
      for (let r = 0; r < m.h; r++) {
        let row = new Uint8Array(m.w);
        for (let q = 0; q < m.w; q++, bit++) row[q] = (d.getUint8(p + (bit >> 3)) >> (7 - (bit & 7))) & 1;
        if (!bitAligned) bit = (bit + 7) & ~7;
        rows.push(row);
      }
      m.rows = rows;
      return m;
    }
    return null;
  }

  return { glyphOf: glyphOf, strikes: strikes, bitmap: bitmap };
}

function loadBitmapFont() {
  return fetch('/font.ttf').then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.arrayBuffer(); })
    .then(buffer => { bitmapFont = parseBitmapFont(buffer); clearGlyphs(); })
    .catch(() => { bitmapFont = null; });
}

// A glyph as a small canvas in font pixels: { c, up (rows above the baseline),
// left (columns before the origin), w, h }
function glyph(ch, size, colour) {
  let key = size + '|' + colour + '|' + SCALE + '|' + ch;
  let g = glyphCache.get(key);
  if (g) return g;
  g = strikeGlyph(ch, size, colour) || outlineGlyph(ch, size, colour);
  glyphCache.set(key, g);
  return g;
}

// From the font's embedded bitmap strike for this size
function strikeGlyph(ch, size, colour) {
  let strike = bitmapFont && bitmapFont.strikes[size];
  if (!strike) return null;
  let gid = bitmapFont.glyphOf(ch.codePointAt(0));
  if (!gid) return null;
  let m = bitmapFont.bitmap(strike, gid, 0);
  if (!m || m.w === 0 || m.h === 0) return null;
  let c = document.createElement('canvas');
  c.width = m.w; c.height = m.h;
  let k = c.getContext('2d');
  k.fillStyle = colour;
  for (let r = 0; r < m.h; r++) {
    let row = m.rows[r];
    for (let q = 0; q < m.w; q++) {
      if (!row[q]) continue;
      let run = q;
      while (run < m.w && row[run]) run++;
      k.fillRect(q, r, run - q, 1);
      q = run;
    }
  }
  return { c: c, up: m.by, left: -m.bx, w: m.w, h: m.h };
}

// A character the strikes do not have (or a size without a strike): the
// browser's outline rendering, hardened to on/off pixels
function outlineGlyph(ch, size, colour) {
  let up = Math.ceil(size), down = Math.ceil(size * 0.4), wide = Math.ceil(size);
  let c = document.createElement('canvas');
  c.width = Math.max(1, Math.round(wide * SCALE)); c.height = Math.max(1, Math.round((up + down) * SCALE));
  let k = c.getContext('2d', { willReadFrequently: true });
  k.setTransform(SCALE, 0, 0, SCALE, 0, 0);
  k.font = fontCss(size); k.textBaseline = 'alphabetic'; k.textAlign = 'left';
  k.fillStyle = '#fff';
  k.fillText(ch, 0, up);
  let img = k.getImageData(0, 0, c.width, c.height), d = img.data;
  let m = /rgba?\((\d+),(\d+),(\d+)(?:,([\d.]+))?\)/.exec(colour) || [0, 0, 0, 0];
  let r = +m[1], gg = +m[2], b = +m[3], a = m[4] === undefined ? 255 : Math.round(+m[4] * 255);
  for (let i = 0; i < d.length; i += 4) {
    if (d[i + 3] >= 128) { d[i] = r; d[i + 1] = gg; d[i + 2] = b; d[i + 3] = a; }
    else d[i + 3] = 0;
  }
  k.putImageData(img, 0, 0);
  return { c: c, up: up, left: 0, w: wide, h: up + down };
}

function snap(v) { return Math.round(v * SCALE) / SCALE; }

// One glyph with its origin at (x, baseline)
function blit(g, ch, x, baseline, size, colour) {
  if (ch === ' ') return;
  let gl = glyph(ch, size, colour);
  g.drawImage(gl.c, snap(x) - gl.left, snap(baseline) - gl.up, gl.w, gl.h);
}

// Where DirectWrite puts the baseline of one line centred vertically in a box
// (ParagraphAlignment.Center), snapped to a pixel as DrawText does
function centredBaseline(top, height, size) {
  return Math.round(top + height / 2 + fonts.BaselineShift * size);
}

// DrawCharacters: one character per cell of the label font, each in a
// (spacing x MainFontHeight) box one pixel above y
function drawCharacters(g, text, x, y, colour) {
  let size = fonts.LabelFontSize, fw = fonts.LabelFontWidth, fh = fonts.LabelFontHeight;
  let base = centredBaseline(y - 1, fh, size);
  for (let i = 0; i < text.length; i++) blit(g, text[i], x + i * fw, base, size, colour);
}

// dc.DrawText(text, format, rect, brush) on the tracks/tools layer.
// align: 'left' (TextAlignment.Leading) or 'center'.
function drawText(g, text, x, y, w, h, colour, size, align) {
  if (!text) return;
  let advance = size / 2; // Terminus is monospaced at half an em
  let base = centredBaseline(y, h, size);
  let start = align === 'center' ? x + (w - advance * text.length) / 2 : x;
  for (let i = 0; i < text.length; i++) blit(g, text[i], start + i * advance, base, size, colour);
}

/* ============================================================
   Background: the maps (Render's bitTarget pass). Painted into its own
   bitmap and reused until the view, the maps or the colours change, as
   vatSys keeps membitmap.
   ============================================================ */

let bg = document.createElement('canvas');
let bgx = bg.getContext('2d');
let backgroundDirty = true;

function visibleLayers() {
  let list = [];
  for (let key in layerState) {
    let ls = layerState[key];
    if (ls.visible && ls.layer) list.push(ls.layer);
  }
  // Render paints the visible maps in DisplayMaps.Maps order
  list.sort((a, b) => a.Order - b.Order);
  return list;
}

function paintBackground() {
  let g = bgx;
  g.setTransform(SCALE, 0, 0, SCALE, 0, 0);
  g.imageSmoothingEnabled = false;
  g.fillStyle = backgroundColour();
  g.fillRect(0, 0, W, H);
  g.translate(CX, CY);

  // Controlled-sector infill and restricted-area infills go beneath the file
  // maps on the Air window; restricted-area outlines go over them on both
  if (!GROUND) for (let layer of dynamicBelow) paintMap(g, layer);
  for (let layer of visibleLayers()) paintMap(g, layer);
  for (let layer of dynamicAbove) paintMap(g, layer);
  backgroundDirty = false;
}

// PaintMap: infills, lines, runways, symbols, labels
function paintMap(g, layer) {
  let brush = mapBrush(layer);
  let project = pts => { let out = new Array(pts.length); for (let i = 0; i < pts.length; i++) out[i] = ll2s(pts[i][0], pts[i][1]); return out; };

  for (let inf of (layer.Infills || [])) {
    let pts = project(inf.Points);
    if (pts.length === 0) continue;
    if (inf.InfillType === 'Background') g.fillStyle = backgroundColour();
    else if (!GROUND && (inf.Pattern === 'Broken' || inf.Pattern === 'Hash' || inf.Pattern === 'Dashed' || inf.Pattern === 'Dotted')) g.fillStyle = hatchBrush(g);
    else g.fillStyle = brush;
    g.beginPath();
    g.moveTo(pts[0][0], pts[0][1]);
    for (let i = 1; i < pts.length; i++) g.lineTo(pts[i][0], pts[i][1]);
    g.closePath();
    g.fill('evenodd'); // a D2D geometry sink fills Alternate unless told otherwise
  }

  // Lines: one DrawLine per segment (flat caps, no joins), skipped when the
  // segment's bounding box misses the window
  g.lineCap = 'butt'; g.lineJoin = 'miter';
  for (let ln of (layer.Lines || [])) {
    let pattern = ln.Pattern;
    if (pattern === 'None') continue;
    let w = ln.Width;
    let pts = project(ln.Points);
    for (let j = 1; j < pts.length; j++) {
      let a = pts[j - 1], b = pts[j];
      if (!segmentVisible(a, b)) continue;
      switch (pattern) {
        case 'Broken': line(g, a, b, brush, w, [12 * w, 6 * w, 2 * w, 6 * w]); break;
        case 'Hash': line(g, a, b, brush, w, [4 * w, 4 * w]); break;
        case 'Dashed':
          if (layer.Type === 'System') dashedBrushLine(g, a, b, w);
          else line(g, a, b, brush, w, [8 * w, 8 * w]);
          break;
        // "dotStyle" is Direct2D's predefined Dash: 2 on, 2 off, in stroke widths
        case 'Dotted': line(g, a, b, brush, w, [2 * w, 2 * w]); break;
        default: line(g, a, b, brush, w);
      }
    }
  }

  // Runways: 2.5 px between the thresholds, 1 px extended centrelines with
  // 12 px ticks across them
  for (let rw of (layer.Runways || [])) {
    let a = ll2s(rw.P1[0], rw.P1[1]), b = ll2s(rw.P2[0], rw.P2[1]);
    if (segmentVisible(a, b)) line(g, a, b, brush, 2.5);
    for (let cl of (rw.Centrelines || [])) {
      let f = ll2s(cl.From[0], cl.From[1]), t = ll2s(cl.To[0], cl.To[1]);
      if (!segmentVisible(f, t)) continue;
      line(g, f, t, brush, 1);
      let ang = cl.Track * D2R + rotation;
      let ox = Math.fround(6 * Math.cos(ang)), oy = Math.fround(6 * Math.sin(ang));
      for (let tk of (cl.Ticks || [])) {
        let q = ll2s(tk[0], tk[1]);
        line(g, [q[0] + ox, q[1] + oy], [q[0] - ox, q[1] - oy], brush, 1);
      }
    }
  }

  paintMapSymbols(g, layer, brush);
  paintMapLabels(g, layer, brush);
}

// infillBrokenBrush: an 8x8 checkerboard of Infill on ASDBackground, tiled from
// the window centre
let hatch = null, hatchKey = '';
function hatchBrush(g) {
  let key = dx('ASDBackground') + dx('Infill') + SCALE;
  if (!hatch || hatchKey !== key) {
    let c = document.createElement('canvas'); c.width = 8; c.height = 8;
    let k = c.getContext('2d');
    k.fillStyle = dx('ASDBackground'); k.fillRect(0, 0, 8, 8);
    k.fillStyle = dx('Infill'); k.fillRect(0, 0, 4, 4); k.fillRect(4, 4, 4, 4);
    hatch = g.createPattern(c, 'repeat'); hatchKey = key;
  }
  return hatch;
}

// A Dashed line on a System map is a solid stroke painted with dashedBrush: a
// 16 px tile, half PrimaryMap and half ASDBackground, rotated along the line
// and anchored at the window centre.
function dashedBrushLine(g, a, b, w) {
  let vx = a[0] - b[0], vy = a[1] - b[1], len = Math.hypot(vx, vy);
  if (!(len > 0)) return;
  line(g, a, b, dx('ASDBackground'), w);
  let u = (b[0] * vx + b[1] * vy) / len; // brush-space position of b
  g.lineDashOffset = ((u % 16) + 16) % 16;
  line(g, b, a, dx('PrimaryMap'), w, [8, 8]);
  g.lineDashOffset = 0;
}

/* Map symbols: the 14 Map.Symbol.Types as CreateSymbolOpacityBitmaps draws
   them, each cropped to its 16x16 cell. */
const SYM_TRIANGLE = [[0, -4], [4, 3], [-4, 3]];
const SYM_STAR = [[0, 8], [2, 2], [8, 0], [2, -2], [0, -8], [-2, -2], [-8, 0], [-2, 2]];
const SYM_MALTESE = [[-3, 7], [3, 7], [1, 1], [7, 3], [7, -3], [1, -1], [3, -7], [-3, -7], [-1, -1], [-7, -3], [-7, 3], [-1, 1]];
const SYM_HEXAGON = [[3, -5], [-3, -5], [-6, 0], [-3, 5], [3, 5], [6, 0]];

function paintMapSymbols(g, layer, brush) {
  let symbols = layer.Symbols;
  if (!symbols || symbols.length === 0) return;
  let poly = pts => { g.beginPath(); g.moveTo(pts[0][0], pts[0][1]); for (let i = 1; i < pts.length; i++) g.lineTo(pts[i][0], pts[i][1]); g.closePath(); };
  let circle = r => { g.beginPath(); g.arc(0, 0, r, 0, Math.PI * 2); };
  for (let sym of symbols) {
    let p = ll2s(sym.Lat, sym.Lon);
    if (!inClip(p)) continue;
    g.save();
    g.translate(p[0], p[1]);
    g.beginPath(); g.rect(-8, -8, 16, 16); g.clip();
    g.fillStyle = brush; g.strokeStyle = brush; g.lineWidth = 1;
    g.lineJoin = 'miter'; g.miterLimit = 10; g.lineCap = 'butt'; g.setLineDash([]);
    switch (sym.SymbolType) {
      case 'SolidTriangle': poly(SYM_TRIANGLE); g.fill(); g.stroke(); break;
      case 'HollowTriangle': poly(SYM_TRIANGLE); g.stroke(); break;
      case 'SolidTriangleWithBorder': poly(SYM_TRIANGLE); g.fill(); g.strokeRect(-4, -4, 8, 7); break;
      case 'HollowTriangleWithBorder': poly(SYM_TRIANGLE); g.stroke(); g.strokeRect(-4, -4, 8, 7); break;
      case 'SolidSquare': g.fillRect(-4, -4, 8, 8); break;
      case 'HollowSquare': g.strokeRect(-3, -3, 6, 6); break;
      case 'SolidStar': poly(SYM_STAR); g.stroke(); g.fill(); break;
      case 'HollowStar': poly(SYM_STAR); g.stroke(); break;
      case 'MalteseCross': poly(SYM_MALTESE); g.fill(); break;
      case 'Circle': circle(6); g.stroke(); break;
      case 'DashedCircle': g.setLineDash([8, 8]); circle(6); g.stroke(); break;
      case 'DotFillCircle':
        circle(2); g.stroke();
        g.lineWidth = 0.5; g.setLineDash([1, 1]);
        for (let r = 3; r <= 7; r++) { circle(r); g.stroke(); }
        break;
      case 'Reticle':
        g.beginPath();
        g.moveTo(0, 5); g.lineTo(0, 8); g.moveTo(5, 0); g.lineTo(8, 0);
        g.moveTo(0, -5); g.lineTo(0, -8); g.moveTo(-5, 0); g.lineTo(-8, 0);
        g.stroke();
        circle(6); g.stroke();
        break;
      case 'Hexagon': poly(SYM_HEXAGON); g.stroke(); break;
    }
    g.restore();
  }
}

/* Map labels: ASDMapFont in the map's colour, antialiased (the one place
   vatSys antialiases text). The text is laid out centred in a box
   MapFontHeight high and clipped to it; a label that would overlap one already
   drawn for the same map is skipped. */
const LABEL_OFFSET = { N: [0, -14], E: [14, 0], SE: [10, 10], S: [0, 14], SW: [-10, 10], W: [-19, 0], NW: [-15, -15] };

function paintMapLabels(g, layer, brush) {
  let labels = layer.Labels;
  if (!labels || labels.length === 0) return;
  let size = fonts.MapFontSize, cw = fonts.MapFontWidth, fh = fonts.MapFontHeight;
  let placed = [];
  g.font = fontCss(size); g.textAlign = 'left'; g.textBaseline = 'alphabetic';
  for (let lb of labels) {
    let p = ll2s(lb.Lat, lb.Lon);
    if (!inClip(p)) continue;
    let text = lb.Text || '';
    let tw = cw * text.length;
    let ax = lb.Align === 'Center' ? tw / 2 : (lb.Align === 'Right' ? tw : 0);
    let ay = lb.VAlign === 'Top' ? 0 : (lb.VAlign === 'Middle' ? fh / 2 : fh);
    let anchor = p;
    if (lb.Leader) { let o = LABEL_OFFSET[lb.Dir] || [10, -10]; anchor = [p[0] + o[0], p[1] + o[1]]; }
    let x = anchor[0] - ax, y = anchor[1] - ay;

    let clash = false;
    for (let r of placed) {
      if (x < r[0] + r[2] && r[0] < x + tw && y < r[1] + fh && r[1] < y + fh) clash = true;
    }
    if (clash) continue;
    placed.push([x, y, tw]);

    if (lb.Leader) line(g, p, anchor, brush, 1);

    // The text layout is at most 200 px wide (198 of it is blitted)
    g.save();
    g.beginPath(); g.rect(x, y, Math.min(198, tw), fh); g.clip();
    g.fillStyle = brush;
    g.fillText(text, x, y + fh / 2 + fonts.BaselineShift * size);
    g.restore();

    textBorder(g, x - 1, y + 1, tw + 1, fh, brush, lb.Border || 0);
  }
}

/* ============================================================
   Track symbols: Tracks.xml geometry, drawn like vatSys's geometry
   realisations. StrokeWidth 0 fills (Filled figures only, alternate fill);
   otherwise the outline is stroked at that width with mitred joins.
   ============================================================ */

function buildSymTable(defs) {
  symTable = {};
  for (let d of defs) symTable[d.Type + '|' + d.SpecialCode] = d;
}
function getSymbol(type, ssr) {
  return symTable[type + '|' + ssr] || symTable[type + '|-1'] || null;
}

function traceShape(g, sh, fillOnly) {
  if (sh.Kind === 'ellipse') {
    g.beginPath();
    g.ellipse(sh.Center ? sh.Center[0] : 0, sh.Center ? sh.Center[1] : 0, sh.RadiusX, sh.RadiusY, 0, 0, Math.PI * 2);
    return true;
  }
  if (sh.Kind === 'rect') {
    let tl = sh.TopLeft || [0, 0], br = sh.BottomRight || [0, 0];
    g.beginPath(); g.rect(tl[0], tl[1], br[0] - tl[0], br[1] - tl[1]);
    return true;
  }
  if (sh.Kind === 'path' && sh.Figures) {
    g.beginPath();
    let traced = false;
    for (let f of sh.Figures) {
      if (fillOnly && !f.Filled) continue; // Hollow figures take no part in a fill
      g.moveTo(f.Start[0], f.Start[1]);
      for (let s of f.Segs) {
        if (s.T === 'b' && s.B) g.bezierCurveTo(s.B[0][0], s.B[0][1], s.B[1][0], s.B[1][1], s.B[2][0], s.B[2][1]);
        else if (s.P) g.lineTo(s.P[0], s.P[1]);
      }
      if (f.Closed) g.closePath();
      traced = true;
    }
    return traced;
  }
  return false;
}

// PaintTrackGeometryRealisation. p is centre-relative; (lat, lon) is the
// position the north correction is taken at.
function paintSymbol(g, type, ssr, p, headingDeg, colour, lat, lon) {
  if (!inClip(p)) return;
  let def = getSymbol(type, ssr);
  if (!def) return;
  g.save();
  g.translate(p[0], p[1]);
  if (def.Rotate && headingDeg !== -1 && headingDeg != null) {
    let angle = headingDeg * D2R + northCorrection(lat, lon) + rotation;
    if (!isFinite(angle)) { g.restore(); return; }
    g.rotate(angle);
  }
  g.lineJoin = 'miter'; g.miterLimit = 10; g.lineCap = 'butt'; g.setLineDash([]);
  let filled = def.StrokeWidth === 0;
  for (let sh of def.Shapes) {
    if (!traceShape(g, sh, filled)) continue;
    if (filled) { g.fillStyle = colour; g.fill('evenodd'); }
    else { g.strokeStyle = colour; g.lineWidth = def.StrokeWidth; g.stroke(); }
  }
  g.restore();
}

/* ============================================================
   Tracks: PaintGroundTracks / PaintASDTracks
   ============================================================ */

const LABEL_DIR_DEFAULT = 1, LABEL_LEN_DEFAULT = 1;
// A track clickspot's types for the left, middle and right button
const TRACK_CLICKS_GROUND = ['Track_Select', '', 'Label_Move', '', ''];
const TRACK_CLICKS_ASD = ['Track_Select', 'Track_IQL', 'Label_Move', '', ''];
// Label positions moved in this window: track id -> { dir, len }. Until a label
// is moved here it sits where it does in vatSys.
let labelState = {};
let labelMoving = null;      // LabelMovingTrack: { id, dir, len }

function labelDirection(ac) {
  let st = labelState[ac.Id];
  return st ? [st.dir, st.len] : [ac.Label.Dir, ac.Label.Len];
}

function paintTracks(g) {
  if (GROUND) {
    for (let ac of aircraft) { if (paintTrackSymbols(g, ac)) paintLabel(g, ac); }
    return;
  }
  // Grouped by HMI state: every symbol of a state, then that state's labels
  for (let i = 0; i < aircraft.length;) {
    let j = i;
    while (j < aircraft.length && aircraft[j].State === aircraft[i].State) j++;
    for (let k = i; k < j; k++) paintTrackSymbols(g, aircraft[k]);
    for (let k = i; k < j; k++) paintLabel(g, aircraft[k]);
    i = j;
  }
}

function paintTrackSymbols(g, ac) {
  let p = ll2s(ac.Lat, ac.Lon);
  ac._p = p;
  if (GROUND) {
    // A track whose (truncated) position is outside the client area is not painted
    let ix = Math.trunc(p[0]) + CX, iy = Math.trunc(p[1]) + CY;
    if (ix < 0 || iy < 0 || ix >= W || iy >= H) return false;
  }
  let col = css(ac.Col);

  // Short route probe
  if (ac.Srp) {
    for (let op of ac.Srp) {
      if (op.length === 4) line(g, ll2s(op[0], op[1]), ll2s(op[2], op[3]), col, 1);
      else paintSymbol(g, ac.SrpSym, -1, ll2s(op[0], op[1]), op[2], col, op[0], op[1]);
    }
  }

  if (ac.History) {
    for (let h of ac.History) paintSymbol(g, 'HistoryMarker', ac.HistSsr, ll2s(h[0], h[1]), h[2], col, h[0], h[1]);
  }

  // Velocity vector: 1-minute steps (5-minute once the setting is above 5),
  // each a great-circle step along the track, a marker between them
  if (ac.Vv > 0) {
    let step = ac.Vv > 5 ? 5 : 1, nm = ac.Gs / 60 * step;
    let ll = [ac.Lat, ac.Lon], prev = p;
    for (let m = step; m <= ac.Vv; m += step) {
      ll = fromBearingRange(ll[0], ll[1], nm, ac.Trk);
      let np = ll2s(ll[0], ll[1]);
      line(g, prev, np, col, 1);
      if (m < ac.Vv) paintSymbol(g, 'VelocityMarker', ac.HistSsr, np, ac.Trk, col, ll[0], ll[1]);
      prev = np;
    }
  }

  for (let s of ac.Syms) {
    if (s.Ident && !flashOn) continue; // an identing symbol is painted on the flash phase only
    paintSymbol(g, s.Type, s.Ssr, p, s.Hdg, s.Col ? css(s.Col) : col, ac.Lat, ac.Lon);
  }

  // The track's clickspot: 12 x 12 about the position, 5 more each way on the Air window
  if (!ac.NoClick) {
    let half = GROUND ? 6 : 11;
    clickspots.push({
      kind: 'track', area: [p[0] - half + CX, p[1] - half + CY, half * 2, half * 2], ac: ac,
      k: GROUND ? TRACK_CLICKS_GROUND : TRACK_CLICKS_ASD,
    });
  }
  return true;
}

// CalculateLabelLocation: whole pixels, measured from the window centre
function labelLocation(p, dir, len) {
  let reach = len * 35 + 5, ang = dir * 22.5 * D2R;
  return [Math.trunc(p[0] + reach * Math.cos(ang)), Math.trunc(p[1] + reach * Math.sin(ang))];
}

// The layout half of PaintLabel: a box of MaxWidth x rows character cells hung
// off the leader end, each line's cells placed by GetItemRectF.
function paintLabel(g, ac) {
  let lb = ac.Label;
  if (!lb) return;
  let p = ac._p || ll2s(ac.Lat, ac.Lon);
  let dl = labelDirection(ac), dir = dl[0], len = dl[1];
  let fw = fonts.LabelFontWidth, fh = fonts.LabelFontHeight;
  let v = labelLocation(p, dir, len);
  let west = dir > 4 && dir < 12;
  let bw = lb.Width * fw, bh = lb.Rows * fh;
  let bx = v[0] - (west ? bw : 0), by = v[1] - lb.AnchorRow * fh;
  ac._box = [bx, by, bw, bh];
  if (!clipIntersects(bx, by, bw, bh)) return;

  // On the flash phase a blinking label is painted with a transparent track
  // brush: no leader, and only the items with colours of their own remain
  let flashing = lb.Blink && flashOn;
  let shared = !!lb.Shared && timeshareOn;
  let lines = flashing ? (shared ? lb.SharedFlash : lb.Flash) : (shared ? lb.Shared : lb.Lines);
  if (!flashing) line(g, p, v, css(lb.Col), 1);

  lines = lines || [];
  for (let i = 0; i < lines.length; i++) {
    let ln = lines[i], y = by + i * fh;
    for (let cell of ln.Cells) {
      let cw = cell.W * fw;
      let cx = (ln.J && west) ? (bx + bw - cell.X * fw - cw) : (bx + cell.X * fw);
      if (west && cx + cw > bx + bw) cx = bx + bw - cw;
      if (cell.K) clickspots.push({ kind: 'label', area: [cx + CX, y + CY, cw, fh], ac: ac, k: cell.K });
      if (cell.Back) { g.fillStyle = css(cell.Back); g.fillRect(cx, y, cw, fh); }
      for (let run of cell.Runs) {
        let rx = cx + Math.trunc(run.Dx * fw) + run.Px, ry = y + run.Dy * fh;
        if (run.C) drawCharacters(g, run.T, rx, ry, css(run.C));
        if (run.B) textBorder(g, rx, ry, run.T.length * fw, fh, css(run.BC || run.C), run.B);
      }
    }
  }

  // Alert box: as wide as the label (vatSys compares the character count with
  // the widest line's pixels) plus 2
  if (lb.Box) {
    let widest = (shared ? lb.SharedBoxWidth : lb.BoxWidth) * fw;
    aliasedRect(g, bx - 1, by, Math.max(lb.Width, widest) + 2, bh, css(lb.Box));
  }
}

/* ============================================================
   Tools: the graphics Render paints around the tracks
   ============================================================ */

// Page-local window state, as each vatSys window keeps its own
let brls = [];               // { origin:[lat,lon], end:[lat,lon], originId, endId, type }
let activeBrl = -1;
let zooming = null;          // { cx, cy, rect:[x,y,w,h] } in client pixels
let scaleRect = { x: 100, y: 100, w: 100, h: 100 }, scaleMoving = null;
let atcRect = { x: 100, y: 100, w: 100, h: 100 }, atcMoving = null;
let cursorLL = null;
let clickspots = [];         // rebuilt each frame: { kind, area:[x,y,w,h] (client px), value }

function toolsColour(id) { return dx(id); }

// PaintLATCs
function paintLatcs(g) {
  let col = dx('StaticTools'), size = fonts.LabelFontSize, fw = fonts.LabelFontWidth, fh = fonts.LabelFontHeight;
  for (let s of (overlays.Latcs || [])) {
    let a = ll2s(s.A[0], s.A[1]), b = ll2s(s.B[0], s.B[1]);
    line(g, a, b, col, 3);
    for (let end of [[a, s.AText], [b, s.BText]]) {
      if (!end[1]) continue;
      g.fillStyle = col; g.fillRect(end[0][0] - 3, end[0][1] - 3, 6, 6);
      drawText(g, end[1], end[0][0], end[0][1], end[1].length * fw, fh, col, size, 'left');
    }
  }
}

// PaintTOPs
function paintTops(g) {
  let col = dx('StaticTools'), size = fonts.LabelFontSize, fw = fonts.LabelFontWidth, fh = fonts.LabelFontHeight;
  for (let t of (overlays.Tops || [])) {
    let a = ll2s(t.A[0], t.A[1]), b = ll2s(t.B[0], t.B[1]);
    line(g, a, b, col, 1.5);
    g.fillStyle = col;
    for (let q of [a, b]) { g.beginPath(); g.arc(q[0], q[1], 4, 0, Math.PI * 2); g.fill(); }
    let mx = (a[0] + b[0]) / 2, my = (a[1] + b[1]) / 2, rowH = fh + 4;
    for (let i = 0; i < t.Lines.length; i++) drawText(g, t.Lines[i], mx, my + i * rowH, 7 * fw, rowH, col, size, 'center');
  }
}

// PaintRoutes
function paintRoutes(g) {
  let size = fonts.LabelFontSize, fw = fonts.LabelFontWidth, fh = fonts.LabelFontHeight;
  for (let route of (overlays.Routes || [])) {
    for (let op of route) {
      let col = dx(op.C);
      if (op.K === 'line') {
        if (!op.A) continue;
        line(g, ll2s(op.A[0], op.A[1]), ll2s(op.B[0], op.B[1]), col, op.W, op.D === 'dash' ? [8 * op.W, 8 * op.W] : (op.D === 'dot' ? [2 * op.W, 2 * op.W] : null));
      } else if (op.K === 'square') {
        let p = ll2s(op.P[0], op.P[1]);
        g.fillStyle = col; g.fillRect(p[0] - 3, p[1] - 3, 6, 6);
      } else if (op.K === 'leader') {
        let p = ll2s(op.P[0], op.P[1]);
        line(g, p, [p[0] + 20, p[1]], col, 1);
      } else if (op.K === 'label') {
        let p = ll2s(op.P[0], op.P[1]), x = p[0] + 20, y = p[1];
        drawText(g, op.Eto, x, y, op.Eto.length * fw, fh, dx(op.EtoC), size, 'left');
        drawText(g, op.Text, x + op.Eto.length * fw, y, op.Text.length * fw, fh, col, size, 'left');
        if (op.Ext) drawText(g, op.Ext, x, y + fh, op.Ext.length * fw, fh, col, size, 'left');
      }
    }
  }
}

// The rectangle and leader shown while a label is being moved
function paintLabelMove(g) {
  if (!labelMoving) return;
  let ac = aircraft.find(a => a.Id === labelMoving.id);
  if (!ac || !ac.Label) return;
  let p = ac._p || ll2s(ac.Lat, ac.Lon), col = dx('DynamicTools');
  let fw = fonts.LabelFontWidth, fh = fonts.LabelFontHeight;
  let v = labelLocation(p, labelMoving.dir, labelMoving.len);
  let x = v[0], y = v[1] - 2 * fh;
  if (labelMoving.dir > 4 && labelMoving.dir < 12) x -= ac.Label.Width * fw;
  aliasedRect(g, x, y, ac.Label.Width * fw, ac.Label.Rows * fh, col);
  line(g, p, v, col, 1);
}

function trackById(id) { return id ? aircraft.find(a => a.Id === id) : null; }

// BRL.Update
function updateBrl(brl) {
  let o = trackById(brl.originId), e = trackById(brl.endId);
  if (o) brl.origin = [o.Lat, o.Lon];
  if (e) brl.end = [e.Lat, e.Lon];
  brl.dist = distanceNm(brl.origin, brl.end);
  brl.track = trackDeg(brl.origin, brl.end);
  brl.time = null;
  if (brl.type !== 'Geographic') {
    if (o && !e && o.Gs > 0) brl.time = brl.dist / o.Gs;       // hours
    else if (!o && e && e.Gs > 0) brl.time = brl.dist / e.Gs;
  }
}

function pad(n, width) { let s = String(n); while (s.length < width) s = '0' + s; return s; }
function fmt000(v) { let r = Math.round(v); return (r < 0 ? '-' : '') + pad(Math.abs(r), 3); }

// Conversions.ConvertToReadableLatLongDDDMM
function readableLatLon(ll) {
  let la = Math.abs(ll[0]), lad = Math.floor(la), lam = Math.round((la - lad) * 60);
  let lo = Math.abs(ll[1]), lod = Math.floor(lo), lom = Math.round((lo - lod) * 60);
  return pad(lad, 2) + ' ' + pad(lam, 2) + (ll[0] < 0 ? 'S' : 'N') + ' ' + pad(lod, 3) + ' ' + pad(lom, 2) + (ll[1] < 0 ? 'W' : 'E');
}

// PaintBRL
function paintBrl(g, brl, index) {
  let active = activeBrl === index;
  let col = dx(active ? 'DynamicTools' : 'StaticTools');
  let size = fonts.LabelFontSize, fw = fonts.LabelFontWidth, fh = fonts.LabelFontHeight;
  let a = ll2s(brl.origin[0], brl.origin[1]), b = ll2s(brl.end[0], brl.end[1]);
  line(g, a, b, col, 1);
  if (brl.type === 'None') return;

  let geo = brl.type === 'Geographic', ext = brl.type === 'Extended';
  let w = fw * 5 + 4, h = fh * 3 + 16;
  if (geo) { w = fw * 16; h = fh * 2 + 12; }
  else if (ext) w = fw * 8 + 4;

  let trk = brl.track, back = (brl.track + 180) % 360;
  if (position) trk += position.MagneticVariation;
  if (trk < 0) trk += 360;
  if (back < 0) back += 360;

  let x = b[0], y = b[1];
  if (trk >= 90) {
    if (trk < 180) y = b[1] - h;
    else if (trk < 270) { x = b[0] - w; y = b[1] - h; }
    else if (trk < 360) x = b[0] - w;
  }
  if (x + w > W - CX) x -= w;
  if (x < -CX) x = -CX;
  if (y + h > H - CY) y -= h;
  if (y < -CY) y = -CY;

  clickspots.push({ kind: 'brl', area: [x + CX, y + CY, w, h], value: index });

  let rowH = fh + 4, textW = w - 4;
  aliasedRect(g, x, y, w, h, col);
  if (geo) {
    aliasedHLine(g, x, x + w, y + rowH, col);
    drawText(g, readableLatLon(brl.end), x + 2, y, textW, rowH, col, size, 'center');
    drawText(g, pad(Math.round(brl.dist), 4) + ' ' + fmt000(trk), x + 2, y + rowH, textW, rowH, col, size, 'center');
    return;
  }

  let text;
  if (brl.dist >= 100) text = pad(Math.round(brl.dist), 4) + (ext ? ' NM' : '');
  else if (!GROUND || brl.dist > 1.619330453563715) text = brl.dist.toFixed(1) + (ext ? ' NM' : '');
  else text = Math.round(brl.dist * 1852) + (ext ? ' m' : 'm'); // within 2999 m the Ground window shows metres
  drawText(g, text, x + 2, y, textW, rowH, col, size, 'left');
  drawText(g, ext ? fmt000(trk) + '(' + fmt000(back) + ')' : fmt000(trk), x + 2, y + rowH, textW, rowH, col, size, 'left');
  let timeText = '---';
  if (brl.time != null) {
    if (active) timeText = fmt000(brl.time * 60);
    else { let t = new Date(Date.now() + brl.time * 3600000); timeText = pad(t.getUTCHours(), 2) + ':' + pad(t.getUTCMinutes(), 2); }
  }
  drawText(g, timeText, x + 2, y + 2 * rowH, textW, rowH, col, size, 'left');
}

// PaintCompassRose
function paintCompassRose(g) {
  let num = 25;
  if (H < num * 2 || W < num * 2) return;
  let vx = Math.trunc(W / 2), vy = Math.trunc(H / 2);
  let col = dx('SecondaryMap'), fw = fonts.LabelFontWidth, fh = fonts.LabelFontHeight;
  let label = (deg, x, y) => drawCharacters(g, pad(deg, 3) + '°', x, y, col);
  for (let i = 1; i <= 36; i++) {
    let a = i * 10 * D2R, sin = Math.sin(a), cos = Math.cos(a), tan = Math.tan(a);
    let x2 = Math.trunc(Math.round(tan * vy)), y2 = sin < 0 ? vy : -vy;
    let x3 = Math.trunc(x2 - Math.round(tan * num)), y3 = sin < 0 ? y2 - num : y2 + num;
    // The tick's bounding box must intersect the client rectangle
    let rx = Math.trunc(Math.min(x2, x3)) + vx, ry = Math.trunc(Math.min(y2, y3)) + vy;
    let rw = Math.trunc(Math.abs(x3 - x2)), rh = Math.trunc(Math.abs(y3 - y2));
    let drawn = false;
    if (rx < W && 0 < rx + rw && ry < H && 0 < ry + rh) {
      line(g, [x2, y2], [x3, y3], col, 2);
      let deg = i * 10;
      if (sin < 0 && cos < 0) deg = 360 - i * 10;
      else if (sin < 0) deg = 360 - i * 10 + 180;
      else if (cos < 0) deg = i * 10 + 180;
      if (sin < 0) label(deg, Math.trunc(x3 - 1.5 * fw), y3 - fh);
      else label(deg, Math.trunc(x3 - 1.5 * fw), y3);
      drawn = true;
    }
    let ys = Math.trunc(Math.round(vx / tan));
    let xs = cos < 0 ? -vx : vx;
    let ys3 = Math.trunc(ys - Math.round(num / tan)), xs3 = cos < 0 ? xs + num : xs - num;
    if (!drawn) {
      line(g, [xs, ys], [xs3, ys3], col, 2);
      let deg = 360 - i * 10 - 180;
      if (sin < 0 && cos < 0) deg = i * 10;
      else if (cos < 0) deg = 360 - (180 - i * 10);
      else if (sin < 0) deg = 360 - i * 10;
      if (cos < 0) label(deg, xs3, Math.trunc(ys3 - 0.5 * fh));
      else label(deg, Math.trunc(xs3 - 3 * fw), Math.trunc(ys3 - 0.5 * fh));
    }
  }
}

// PaintVisRange
function paintVisRange(g) {
  let vis = overlays.Vis;
  if (!vis || !vis.Centres) return;
  let col = dx('StaticTools'), radius = W / range * vis.Range;
  for (let c of vis.Centres) {
    let p = ll2s(c[0], c[1]);
    g.fillStyle = col; g.beginPath(); g.arc(p[0], p[1], 5, 0, Math.PI * 2); g.fill();
    if (radius > 0 && radius < 1e6) { g.strokeStyle = col; g.lineWidth = 1; g.setLineDash([]); g.beginPath(); g.arc(p[0], p[1], radius, 0, Math.PI * 2); g.stroke(); }
  }
}

function clampToClient(r) {
  if (r.x + r.w > W) r.x -= r.x + r.w - W;
  if (r.y + r.h > H) r.y -= r.y + r.h - H;
  if (r.x < 0) r.x = 0;
  if (r.y < 0) r.y = 0;
}

// PaintScaleMarker: a 6 x 10 NM cross with a tick every mile. Client coordinates.
function paintScaleMarker(g) {
  let moving = !!scaleMoving, r = moving ? scaleMoving : scaleRect;
  let col = dx(moving ? 'DynamicTools' : 'StaticTools');
  let ppm = W / range;
  let w = Math.max(1, Math.round(6 * ppm)), h = Math.max(1, Math.round(10 * ppm));
  r.w = w + 1; r.h = h + 1;
  let ax = r.x, ay = r.y + Math.trunc(h / 2);        // left end of the horizontal bar
  let tx = r.x + Math.trunc(w / 2), ty = r.y;        // top end of the vertical bar
  line(g, [ax, ay], [r.x + w, ay], col, 1);
  line(g, [tx, ty], [tx, r.y + h], col, 1);
  let tick = Math.round(ppm * 0.25);
  if (w > 5) for (let i = 0; i <= 6; i++) { let x = ax + Math.round(ppm * i); line(g, [x, ay - tick], [x, ay + tick], col, 1.5); }
  if (h > 10) for (let j = 0; j <= 10; j++) { let y = ty + Math.round(ppm * j); line(g, [tx - tick, y], [tx + tick, y], col, 1.5); }
  clickspots.push({ kind: 'scale', area: [r.x, r.y, r.w, r.h] });
  if (moving) aliasedRect(g, r.x, r.y, r.w, r.h, col);
  else clampToClient(r);
}

// PaintATCList: sector name (or callsign) then frequencies, in the small font.
// Client coordinates.
function paintAtcList(g) {
  let atc = overlays.Atc;
  if (!atc) return;
  let moving = !!atcMoving, r = moving ? atcMoving : atcRect;
  let col = dx(moving ? 'DynamicTools' : 'StaticTools');
  let size = fonts.SmallFontSize, fw = Math.trunc(fonts.SmallFontWidth), fh = Math.trunc(fonts.SmallFontHeight);
  let right = 0, bottom = 0;
  for (let i = 0; i < atc.Rows.length; i++) {
    let row = atc.Rows[i];
    let x = r.x, y = r.y + Math.trunc(i * fonts.SmallFontHeight), w = fw * atc.NameWidth;
    drawText(g, row.Name, x, y, w, fh, col, size, 'left');
    let fx = x + w + fw, fwid = fw * row.Freq.length;
    if (row.Freq) drawText(g, row.Freq, fx, y, fwid, fh, col, size, 'left');
    clickspots.push({ kind: 'atc', area: [x, y, w, fh] });
    if (fx + fwid > right) right = fx + fwid;
    if (y + fh > bottom) bottom = y + fh;
  }
  r.w = right - r.x; r.h = bottom - r.y;
  if (moving) aliasedRect(g, r.x, r.y, r.w, r.h, col);
  else clampToClient(r);
}

/* ============================================================
   Render
   ============================================================ */

let renderPending = false;
function scheduleRender() {
  if (renderPending) return;
  renderPending = true;
  requestAnimationFrame(() => { renderPending = false; render(); });
}

function render() {
  if (!(W > 0 && H > 0)) return;
  if (backgroundDirty) paintBackground();

  let g = ctx;
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.imageSmoothingEnabled = false;
  g.drawImage(bg, 0, 0);
  g.setTransform(SCALE, 0, 0, SCALE, 0, 0);
  g.translate(CX, CY);
  clickspots = [];

  // Tools beneath the tracks
  try {
    if (!GROUND) { paintLatcs(g); paintTops(g); paintRoutes(g); }
  } catch (e) { console.warn('tools layer', e); }

  try { paintTracks(g); } catch (e) { console.warn('tracks layer', e); }

  // Tools above the tracks
  try {
    paintLabelMove(g);
    for (let i = 0; i < brls.length; i++) { updateBrl(brls[i]); paintBrl(g, brls[i], i); }
  } catch (e) { console.warn('upper tools layer', e); }

  try {
    if (settings.CompassRose && !GROUND) paintCompassRose(g);
    if (settings.VisCenters) paintVisRange(g);
    g.setTransform(SCALE, 0, 0, SCALE, 0, 0); // the rest is in client coordinates
    if (settings.ScaleMarker && !GROUND) paintScaleMarker(g);
    if (settings.AtcList && !GROUND) paintAtcList(g);
    if (zooming) aliasedRect(g, zooming.rect[0], zooming.rect[1], zooming.rect[2], zooming.rect[3], dx('DynamicTools'));
  } catch (e) { console.warn('window tools layer', e); }
}

setInterval(() => {
  flashOn = flashCount % 2 === 0;
  if (++flashCount >= 4) { flashCount = 0; timeshareOn = !timeshareOn; }
  let animated = aircraft.some(a => (a.Label && (a.Label.Blink || a.Label.Shared)) || a.Syms.some(s => s.Ident));
  if (animated) scheduleRender();
}, 600);

/* ---------- Size ---------- */

function resize() {
  let rect = viewEl.getBoundingClientRect();
  let w = Math.floor(rect.width), h = Math.floor(rect.height);
  let dpr = window.devicePixelRatio || 1;
  if (w === W && h === H && dpr === SCALE) return;
  let hadSize = W > 0;
  W = w; H = h; CX = Math.trunc(W / 2); CY = Math.trunc(H / 2);
  if (dpr !== SCALE) { SCALE = dpr; clearGlyphs(); hatch = null; }
  for (let c of [cv, bg]) { c.width = Math.max(1, Math.round(W * SCALE)); c.height = Math.max(1, Math.round(H * SCALE)); }
  cv.style.width = W + 'px'; cv.style.height = H + 'px';
  // UpdateRenderSize: the range across the window is kept
  if (W > 0) setZoom(hadSize && range > 0 ? range : (position ? position.DefaultRange : DEF_RANGE), true);
  backgroundDirty = true;
  render();
}
