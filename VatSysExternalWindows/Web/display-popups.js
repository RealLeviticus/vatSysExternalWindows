'use strict';
/* ============================================================
   Acting on a track: the menus and popups vatSys opens from a click on a
   track or a label item (MMI.OpenAircraftMenu, GenericContextMenu, CFLMenu,
   LevelMenu, DiversionMenu, TimeMenu, HandoffWindow and the label data
   field). The plugin decides what each one contains and carries out the
   choice; this draws them and sends the choice back.
   ============================================================ */

let popup = null;            // the open menu or popup: { el, backdrop, onKey }
let popupOpenedAt = 0;
let actionSeq = 0;

// A menu opened under a held finger sits where the finger is: the click the
// browser makes of lifting it must not choose anything
function justOpened() { return Date.now() - popupOpenedAt < 350; }

function closePopup() {
  if (!popup) return;
  let p = popup;
  popup = null;
  p.el.remove();
  if (p.backdrop) p.backdrop.remove();
  document.removeEventListener('keydown', p.onKey, true);
}

/* ---------- Status line: what an action did that is not visible here ---------- */

const toastEl = document.getElementById('toast');
let toastTimer = 0;
function toast(text, isError) {
  toastEl.textContent = text;
  toastEl.className = 'show' + (isError ? ' error' : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { toastEl.className = ''; }, isError ? 6000 : 4000);
}

/* ---------- Sending an action ---------- */

// ctx: { id, x, y (page pixels, where the click was), touch, cell: [x, y, w, h] (page pixels) }
function sendAction(ctx, fields) {
  let seq = ++actionSeq;
  let body = Object.assign({ Type: TYPE, Id: ctx.id }, fields);
  return fetch('/api/action', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).then(r => {
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return r.json();
  }).then(result => {
    if (result.Error) toast(result.Error, true);
    else if (result.Note) toast(result.Note, false);
    // A reply that was overtaken by a newer click no longer opens anything
    if (result.Ui && seq === actionSeq) showUi(result.Ui, ctx);
    // Show the effect now rather than at the next poll
    pollAircraft();
    pollState();
  }).catch(() => { toast('vatSys did not answer', true); });
}

function choose(ui, ctx, arg, extra) {
  closePopup();
  sendAction(ctx, Object.assign({ Action: ui.Action, Ctx: ui.Ctx || '', Arg: arg }, extra || {}));
}

/* ---------- Showing what an action returned ---------- */

// A menu built here rather than by the plugin (the touch chooser)
function showUiElement(el, ctx) {
  closePopup();
  openPopup(el, ctx);
}

function showUi(ui, ctx) {
  closePopup();
  if (ui.Kind === 'menu') showContextMenu(ui, ctx);
  else if (ui.Kind === 'list') showListWindow(ui, ctx);
  else if (ui.Kind === 'handoff') showHandoffWindow(ui, ctx);
  else if (ui.Kind === 'text') showTextField(ui, ctx);
  else if (ui.Kind === 'confirm') showConfirmWindow(ui, ctx);
}

// Opens el as the popup at the click position (its top-left corner there, as
// vatSys opens a menu or window at the cursor), moved to stay on screen. A
// click anywhere else closes it. Under a finger it is drawn half as large again.
function openPopup(el, ctx, options) {
  options = options || {};
  let backdrop = document.createElement('div');
  backdrop.className = 'popup-backdrop';
  let dismiss = e => { e.preventDefault(); e.stopPropagation(); closePopup(); };
  backdrop.addEventListener('pointerdown', dismiss);
  backdrop.addEventListener('contextmenu', e => e.preventDefault());
  document.body.appendChild(backdrop);

  if (ctx.touch && !options.noScale) el.classList.add('touch');
  el.style.left = '0px'; el.style.top = '0px';
  document.body.appendChild(el);

  let r = el.getBoundingClientRect(), vw = window.innerWidth, vh = window.innerHeight;
  let x = options.x !== undefined ? options.x : ctx.x, y = options.y !== undefined ? options.y : ctx.y;
  if (x + r.width > vw) x = vw - r.width;
  if (y + r.height > vh) y = vh - r.height;
  el.style.left = Math.max(0, Math.round(x)) + 'px';
  el.style.top = Math.max(0, Math.round(y)) + 'px';

  let onKey = e => { if (e.key === 'Escape') { e.preventDefault(); closePopup(); } };
  document.addEventListener('keydown', onKey, true);
  el.addEventListener('contextmenu', e => e.preventDefault());
  popup = { el: el, backdrop: backdrop, onKey: onKey };
  popupOpenedAt = ctx.held ? Date.now() : 0;
}

/* ---------- GenericContextMenu / AircraftContextMenu ---------- */

// items: [{ text, disabled, click }]; title: the disabled first item (the callsign)
function buildContextMenu(title, items, maxVisible) {
  let el = document.createElement('div');
  el.className = 'menu-drop show popup context';
  let add = (text, disabled, click) => {
    let row = document.createElement('div');
    row.className = 'menu-item' + (disabled ? ' disabled' : '');
    row.textContent = text;
    if (!disabled && click) row.addEventListener('click', e => { e.stopPropagation(); if (!justOpened()) click(); });
    el.appendChild(row);
  };
  if (title) add(title, true);
  for (let item of items) add(item.text, item.disabled, item.click);
  // MaximumVisibleItems: the rest is reached by scrolling
  if (maxVisible > 0) el.style.maxHeight = (maxVisible * 22 + 4) + 'px';
  return el;
}

function showContextMenu(ui, ctx) {
  let items = (ui.Items || []).map(item => ({
    text: item.Text, disabled: item.Disabled,
    click: () => choose(ui, ctx, item.Arg != null ? item.Arg : item.Text),
  }));
  openPopup(buildContextMenu(ui.Title, items, ui.MaxVisible), ctx);
}

/* ---------- A small BaseForm: title bar with the close button ---------- */

function buildWindow(title, className) {
  let el = document.createElement('div');
  el.className = 'popup window ' + (className || '');
  let bar = document.createElement('div');
  bar.className = 'pw-title';
  let text = document.createElement('div');
  text.className = 'pw-title-text';
  text.textContent = title || '';
  let close = document.createElement('div');
  close.className = 'tb-btn pw-close';
  close.innerHTML = '<svg width="23" height="23"><path class="ico-stroke2" d="M5 5L18 18M18 5L5 18"/></svg>';
  close.addEventListener('click', e => { e.stopPropagation(); closePopup(); });
  bar.appendChild(text); bar.appendChild(close);
  // MiddleClickClose
  bar.addEventListener('pointerdown', e => { if (e.button === 1) { e.preventDefault(); closePopup(); } });
  let body = document.createElement('div');
  body.className = 'pw-body';
  el.appendChild(bar); el.appendChild(body);
  return { el: el, body: body };
}

function buildButton(text, toggle, checked) {
  let b = document.createElement('div');
  b.className = 'pw-button' + (toggle ? ' toggle' : '') + (checked ? ' checked' : '');
  b.textContent = text;
  return b;
}

/* ---------- CFLMenu / LevelMenu / DiversionMenu / TimeMenu ---------- */

const LIST_ROW = 25, LIST_HEIGHT = 229;

function showListWindow(ui, ctx) {
  let win = buildWindow(ui.Title, 'list');
  let items = ui.Items || [];
  let selected = new Set(ui.Selected || []);
  let toggles = {};

  // The list in its inset panel, and the scroll bar beside it
  let row = document.createElement('div'); row.className = 'pw-list-row';
  let inset = document.createElement('div'); inset.className = 'pw-inset';
  let list = document.createElement('div'); list.className = 'pw-list';
  let bar = document.createElement('div'); bar.className = 'pw-scroll';
  let thumb = document.createElement('div'); thumb.className = 'pw-thumb';
  bar.appendChild(thumb); inset.appendChild(list); row.appendChild(inset); row.appendChild(bar);
  win.body.appendChild(row);

  let rows = items.map((item, index) => {
    let r = document.createElement('div');
    r.className = 'pw-item' + (selected.has(index) ? ' selected' : '');
    r.textContent = item.Text;
    r.addEventListener('click', e => { e.stopPropagation(); pick(index); });
    list.appendChild(r);
    return r;
  });
  let repaint = () => rows.forEach((r, i) => r.classList.toggle('selected', selected.has(i)));
  let checkedToggles = () => Object.keys(toggles).filter(name => toggles[name]);
  let value = index => items[index].Arg != null ? items[index].Arg : items[index].Text;

  function pick(index) {
    if (toggles.blk) {
      // A block: two levels. A third pick starts again from it.
      if (selected.has(index)) selected.delete(index); else selected.add(index);
      if (selected.size > 2) { selected.clear(); selected.add(index); }
      repaint();
      if (selected.size === 2) {
        let pair = [...selected].sort((a, b) => a - b);
        choose(ui, ctx, value(pair[0]), { Arg2: value(pair[1]), Toggles: checkedToggles() });
      }
      return;
    }
    choose(ui, ctx, value(index), { Toggles: checkedToggles() });
  }

  // The scroll bar follows the list; dragging it or clicking in it scrolls the list
  let syncThumb = () => {
    let total = list.scrollHeight, view = list.clientHeight, track = bar.clientHeight;
    let size = Math.max(7, Math.round(track * Math.min(1, view / Math.max(1, total))));
    thumb.style.height = size + 'px';
    thumb.style.top = (total > view ? Math.round((track - size) * list.scrollTop / (total - view)) : 0) + 'px';
  };
  list.addEventListener('scroll', syncThumb);
  let drag = null;
  bar.addEventListener('pointerdown', e => {
    e.preventDefault(); e.stopPropagation();
    bar.setPointerCapture(e.pointerId);
    let scale = bar.getBoundingClientRect().height / bar.clientHeight;
    if (e.target !== thumb) {
      let y = (e.clientY - bar.getBoundingClientRect().top) / scale;
      list.scrollTop += (y < thumb.offsetTop ? -1 : 1) * list.clientHeight;
    }
    drag = { y: e.clientY, top: list.scrollTop, scale: scale };
  });
  bar.addEventListener('pointermove', e => {
    if (!drag) return;
    let travel = bar.clientHeight - thumb.offsetHeight, range = list.scrollHeight - list.clientHeight;
    if (travel > 0) list.scrollTop = drag.top + (e.clientY - drag.y) / drag.scale * range / travel;
  });
  let endDrag = () => { drag = null; };
  bar.addEventListener('pointerup', endDrag);
  bar.addEventListener('pointercancel', endDrag);
  // The wheel moves a row at a time, as the vatSys list does
  win.el.addEventListener('wheel', e => {
    e.preventDefault();
    list.scrollTop = Math.round(list.scrollTop / LIST_ROW) * LIST_ROW + (e.deltaY > 0 ? LIST_ROW : -LIST_ROW);
  }, { passive: false });

  // TimeMenu's text field: a time typed as HHmm
  if (ui.Entry) {
    let input = document.createElement('input');
    input.className = 'pw-entry';
    input.type = 'text'; input.inputMode = 'numeric'; input.autocomplete = 'off';
    input.maxLength = ui.MaxLength || 4;
    input.addEventListener('keydown', e => {
      if (e.key === 'Enter') { e.preventDefault(); choose(ui, ctx, input.value.trim(), { Toggles: checkedToggles() }); }
    });
    input.addEventListener('pointerdown', e => e.stopPropagation());
    win.body.appendChild(input);
  }

  for (let button of (ui.Buttons || [])) {
    let b = buildButton(button.Text, button.Toggle, button.Checked);
    if (button.Toggle) toggles[button.Name] = !!button.Checked;
    b.addEventListener('click', e => {
      e.stopPropagation();
      if (!button.Toggle) { choose(ui, ctx, '#' + button.Name, { Toggles: checkedToggles() }); return; }
      toggles[button.Name] = !toggles[button.Name];
      b.classList.toggle('checked', toggles[button.Name]);
      // BLK switches the list between one level and two, and clears it
      if (button.Name === 'blk') { selected.clear(); repaint(); }
    });
    win.body.appendChild(b);
  }

  openPopup(win.el, ctx);
  list.scrollTop = (ui.Top || 0) * LIST_ROW;
  syncThumb();
}

/* ---------- HandoffWindow ---------- */

const HANDOFF_MAX_BUTTONS = 88;

function showHandoffWindow(ui, ctx) {
  let win = buildWindow(ui.Title, 'handoff');
  let flow = document.createElement('div'); flow.className = 'pw-flow';
  win.body.appendChild(flow);
  let items = ui.Items || [], page = 0;
  // More than fit: two places go to Prev and Next
  let paged = items.length > HANDOFF_MAX_BUTTONS;
  let perPage = paged ? HANDOFF_MAX_BUTTONS - 2 : HANDOFF_MAX_BUTTONS;
  let lastPage = Math.max(0, Math.ceil(items.length / perPage) - 1);

  function fill() {
    flow.textContent = '';
    for (let item of items.slice(page * perPage, (page + 1) * perPage)) {
      let b = buildButton(item.Text, item.Checked, item.Checked);
      if (item.Wide) b.classList.add('wide');
      if (item.Dim) b.classList.add('dim');
      b.addEventListener('click', e => { e.stopPropagation(); choose(ui, ctx, item.Arg); });
      flow.appendChild(b);
    }
    let fixed = (text, enabled, click) => {
      let b = buildButton(text);
      if (!enabled) b.classList.add('off');
      else b.addEventListener('click', e => { e.stopPropagation(); click(); });
      flow.appendChild(b);
    };
    fixed('None', true, () => choose(ui, ctx, '#none'));
    fixed('Other', true, () => choose(ui, ctx, '#other'));
    if (paged) {
      fixed('Prev', page > 0, () => { page--; fill(); });
      fixed('Next', page < lastPage, () => { page++; fill(); });
    }
  }
  fill();
  openPopup(win.el, ctx);
}

/* ---------- The label data field: a TextField over the item ---------- */

function showTextField(ui, ctx) {
  let input = document.createElement('input');
  input.className = 'popup pw-field';
  input.type = 'text'; input.autocomplete = 'off'; input.spellcheck = false;
  input.setAttribute('autocapitalize', 'characters');
  input.maxLength = ui.MaxLength || 11;
  input.value = ui.Value || '';
  let cell = ctx.cell || [ctx.x, ctx.y, 99, 18];
  input.style.width = Math.max(cell[2], 99) + 'px';
  let done = false;
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); done = true; choose(ui, ctx, input.value.toUpperCase()); }
  });
  input.addEventListener('input', () => {
    let at = input.selectionStart;
    input.value = input.value.toUpperCase();
    try { input.setSelectionRange(at, at); } catch (e) { }
  });
  // Leaving the field abandons the entry, as it does in vatSys
  input.addEventListener('blur', () => { if (!done && popup && popup.el === input) closePopup(); });
  input.addEventListener('pointerdown', e => e.stopPropagation());
  openPopup(input, ctx, { x: cell[0], y: cell[1], noScale: true });
  input.focus();
  // The vatSys field opens with the caret at the start and nothing selected
  try { input.setSelectionRange(0, 0); } catch (e) { }
}

/* ---------- GenericConfirmationWindow ---------- */

function showConfirmWindow(ui, ctx) {
  let win = buildWindow(ui.Title, 'confirm');
  let text = document.createElement('div'); text.className = 'pw-text';
  text.textContent = ui.Text || '';
  let buttons = document.createElement('div'); buttons.className = 'pw-buttons';
  let yes = buildButton('Yes'), no = buildButton('No');
  yes.addEventListener('click', e => { e.stopPropagation(); choose(ui, ctx, 'yes'); });
  no.addEventListener('click', e => { e.stopPropagation(); closePopup(); });
  buttons.appendChild(yes); buttons.appendChild(no);
  win.body.appendChild(text); win.body.appendChild(buttons);
  openPopup(win.el, ctx);
}
