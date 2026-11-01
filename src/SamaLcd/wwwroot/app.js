'use strict';
// UI of SamaLcd. Talks to MainForm through WebView2 messages:
//   host -> page: {type:'state'|'frame'|'status'|'sensors'|'toast', ...}
//   page -> host: {cmd:'ready'|'set'|'pickFile'|'layout'|'sensors'|'openThemes'|'reloadThemes'|'openUrl', ...}

const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const tr = (key, ...args) => I18n.t(key, ...args);
const CUSTOM = '@custom';
const SENSOR_KEYS = [
  ['cpu.temp', "CPU temperature", '°'], ['cpu.load', "CPU load", '%'], ['cpu.clock', "CPU clock", " MHz"],
  ['cpu.power', "CPU power", " W"], ['gpu.temp', "GPU temperature", '°'], ['gpu.hotspot', "GPU hotspot", '°'],
  ['gpu.load', "GPU load", '%'], ['gpu.clock', "GPU clock", " MHz"], ['gpu.power', "GPU power", " W"],
  ['gpu.mem.load', "GPU memory load", '%'], ['gpu.mem.used', "GPU memory used", " MB"], ['gpu.fan', "GPU fan", " rpm"],
  ['ram.load', "RAM load", '%'], ['ram.used', "RAM used", " GB"], ['ram.free', "RAM free", " GB"],
  ['board.temp', "Board temperature", '°'], ['pump.rpm', "Pump", " rpm"], ['fan.rpm', "CPU fan", " rpm"],
];
const SHORT = { 'cpu.temp': 'CPU', 'gpu.temp': 'GPU', 'cpu.load': 'CPU', 'gpu.load': 'GPU', 'ram.load': 'RAM', 'pump.rpm': "Pump" };
const TEXT_COLORS = ['#FFFFFF', '#8FA3B8', '#00D1FF', '#3B82F6', '#8A5CFF', '#C9B8FF', '#22C55E', '#A3E635', '#FACC15', '#FF8A00', '#FF3B5C', '#E11D48'];
const BG_COLORS = ['#000000', '#05080D', '#0B1220', '#111827', '#1E1B4B', '#3B0764', '#052E16', '#450A0A', '#FFFFFF', '#00D1FF', '#8A5CFF', '#FF3B5C', '#FF8A00', '#22C55E'];
const KIND_NAMES = { sensor: "Value", bar: "Bar", text: "Text", time: "Time", date: "Date" };

const host = window.chrome?.webview
  ? { send: m => window.chrome.webview.postMessage(m), on: f => window.chrome.webview.addEventListener('message', e => f(e.data)) }
  : mockHost();

let state = null;
let layout = null;
let hostLayoutVersion = -1;
let selected = -1;
let aliasValues = {};
let sensorList = [];

// ---------- helpers ----------

function setJoin(el, value) {
  for (const b of el.querySelectorAll('[data-value]')) b.classList.toggle('btn-primary', b.dataset.value === String(value));
}
function onJoin(el, f) {
  el.addEventListener('click', e => {
    const b = e.target.closest('[data-value]');
    if (!b) return;
    setJoin(el, b.dataset.value);
    f(b.dataset.value);
  });
}
function set(key, value) { host.send({ cmd: 'set', key, value }); }

/** A row of colour swatches plus a "custom colour" swatch with the system picker. */
function palette(el, colors, current, pick) {
  const cur = (current || '').toUpperCase();
  el.innerHTML = colors.map(c => `<div class="swatch${c === cur ? ' active' : ''}" style="background:${c}" data-color="${c}" title="${c}"></div>`).join('') +
    `<label class="swatch custom${colors.includes(cur) ? '' : ' active'}" title="${esc(tr("Custom color"))}"><input type="color" value="${cur.slice(0, 7) || '#ffffff'}"></label>` +
    `<span class="font-mono text-xs opacity-60 ml-1">${esc(cur)}</span>`;
  el.onclick = e => {
    const s = e.target.closest('[data-color]');
    if (s) pick(s.dataset.color);
  };
  el.querySelector('input').oninput = e => pick(e.target.value.toUpperCase());
}

function toast(text, kind = 'info') {
  const t = document.createElement('div');
  t.className = `alert alert-${kind} shadow-lg`;
  t.textContent = text;
  $('#toasts').append(t);
  setTimeout(() => t.remove(), 4000);
}

function sensorName(key) { return tr(SENSOR_KEYS.find(k => k[0] === key)?.[1] ?? key); }
function sensorValue(key) {
  if (key in aliasValues) return aliasValues[key];
  return sensorList.find(s => s.id === key)?.value ?? null;
}
function fmt(v, d = 0) { return v == null ? '--' : Number(v).toFixed(d); }

// ---------- tabs ----------

function activateTab(name) {
  for (const t of $$('#tabs .tab')) t.classList.toggle('tab-active', t.dataset.tab === name);
  for (const p of $$('.page')) p.classList.toggle('hidden', p.dataset.page !== name);
  try { localStorage.setItem('tab', name); } catch { }
  if (name === 'sensors' || name === 'data') host.send({ cmd: 'sensors' });
  if (name === 'data' && layout) renderEditor(); // the hidden canvas has no measurable size
}
$('#tabs').addEventListener('click', e => { const t = e.target.closest('.tab'); if (t) activateTab(t.dataset.tab); });

// ---------- state from the host ----------

let stateRevision = 0;
async function applyState(s) {
  const revision = ++stateRevision;
  const languageChanged = I18n.language !== I18n.normalize(s.language);
  await I18n.setLanguage(s.language);
  if (revision !== stateRevision) return;
  $('#language').value = I18n.language;
  state = s;
  window.showSdCleanup(s.pendingCleanup || null);
  $('#cropBackground').disabled = !s.canCrop;
  $('#ffmpegStatus').textContent = s.ffmpegStatus || '';
  const custom = s.theme === CUSTOM;
  $('#variantBadge').textContent = custom ? tr("Custom layout") : s.theme;
  $('#brightness').value = s.brightness;
  $('#brightnessValue').textContent = s.brightness + ' %';
  $('#paused').checked = s.paused;
  $('#autostart').checked = s.autostart;
  $('#onDevice').checked = s.onDevice;
  setJoin($('#rotation'), s.rotation);
  const page = s.bgMode === 'theme' ? 'gallery' : s.bgMode; // "the variant's own background" is a gallery tile
  setJoin($('#bgMode'), page);
  $('#bgGalleryBox').classList.toggle('hidden', page !== 'gallery');
  renderGallery(s);
  setJoin($('#fit'), s.fit);
  $('#bgColorBox').classList.toggle('hidden', s.bgMode !== 'color');
  $('#bgFileBox').classList.toggle('hidden', s.bgMode !== 'file');
  palette($('#bgPalette'), BG_COLORS, s.bgColor, c => set('bgColor', c));
  $('#bgFileName').textContent = s.bgFileName || tr("No file selected");
  $('#bgFileKind').textContent = s.bgFileKind || '';
  $('#cpuHint').classList.toggle('hidden', !s.cpuHint);
  $('#cpuHintData').classList.toggle('hidden', !s.cpuHint);
  $('#deviceInfo').textContent = s.device || tr("Display not connected");

  $('#variants').innerHTML = [...s.themes.map(t => ({ ...t, ...t.thumb })), { name: CUSTOM, title: tr("Custom layout"), ...s.customThumb }].map(t => `
    <div class="variant card bg-base-200 w-36 ${t.name === s.theme ? 'active' : ''}" data-name="${esc(t.name)}">
      <div class="card-body p-3 gap-2 items-center">
        <div class="thumb w-full">${mediaHtml(t.media)}${t.image ? `<img src="${t.image}" alt="">` : `<span class="opacity-50 text-sm">${esc(tr("None"))}</span>`}</div>
        <div class="text-sm font-medium text-center">${t.name === CUSTOM ? '✏️ ' : ''}${esc(t.title)}</div>
      </div>
    </div>`).join('');

  $('#editor').classList.toggle('hidden', !custom);
  if (s.layoutVersion !== hostLayoutVersion) { // the host changed the layout itself (start, reset)
    hostLayoutVersion = s.layoutVersion;
    layout = s.layout;
    if (selected >= layout.items.length) selected = -1;
    renderEditor();
  } else if (languageChanged && layout) renderEditor();
  renderSensors();
  renderMiniStats();
  if (lastStatus) renderStatus(lastStatus);
}

$('#variants').addEventListener('click', e => {
  const v = e.target.closest('.variant');
  if (v && v.dataset.name !== state.theme) set('theme', v.dataset.name);
});

let lastStatus = null;
function renderStatus(m) {
  if ('pendingCleanup' in m) window.showSdCleanup(m.pendingCleanup);
  if (typeof m.paused === 'boolean') $('#paused').checked = m.paused;
  if (m.storage?.length === 6) $('#storageInfo').textContent = tr('Internal: {0} / {1} MiB free; SD: {2} / {3} MiB free', ...( [2,0,5,3].map(i => (m.storage[i]/1024).toFixed(1)) ));
  $('#status').textContent = m.textKey ? tr(m.textKey, ...(m.textArgs || [])) : m.text;
  const c = $('#conn');
  c.className = `badge badge-soft gap-1 ml-2 whitespace-nowrap ${m.paused ? 'badge-warning' : m.connected ? 'badge-success' : 'badge-error'}`;
  c.innerHTML = `<span class="status ${m.paused ? 'status-warning' : m.connected ? 'status-success' : 'status-error'}"></span> ` +
    esc(tr(m.paused ? 'Paused' : m.connected ? 'Display connected' : 'No display'));
  $('#traffic').textContent = m.framesSent > 0 ? tr('Frames sent: {0}, {1:0.0} MB', m.framesSent, m.megabytesSent) : '';
  $('#deviceInfo').textContent = m.device || tr('Display not connected');
}
$('#language').onchange = e => set('language', e.target.value);
$('#retryFfmpeg').onclick = () => host.send({cmd:'installFfmpeg'});

host.on(m => {
  switch (m.type) {
    case 'state': applyState(m); break;
    case 'cropSource': window.openCropEditor(m); break;
    case 'sdCleanup': window.showSdCleanup(m.request); break;
    case 'frame':
      $('#preview').src = m.data;
      $('#canvasImg').src = m.data;
      setMedia($('#previewMedia'), m.media);
      setMedia($('#canvasMedia'), m.media);
      $('#previewEmpty').classList.add('hidden');
      break;
    case 'status': lastStatus = m; renderStatus(m); break;
    case 'sensors':
      aliasValues = m.aliases;
      if (m.list) sensorList = m.list;
      renderSensors();
      renderMiniStats();
      updatePropValue();
      break;
    case 'toast': toast(m.text, m.kind); break;
  }
});

// ---------- image tab ----------

$('#brightness').addEventListener('input', e => {
  $('#brightnessValue').textContent = e.target.value + ' %';
  set('brightness', +e.target.value);
});
$('#paused').onchange = e => set('paused', e.target.checked);
$('#autostart').onchange = e => set('autostart', e.target.checked);
$('#onDevice').onchange = e => set('onDevice', e.target.checked);
onJoin($('#rotation'), v => set('rotation', +v));
onJoin($('#fit'), v => set('fit', v));
onJoin($('#bgMode'), v => {
  if (v === 'gallery' && !state.galleryFile) { // nothing chosen yet: just show the tiles
    for (const [box, mode] of [['#bgGalleryBox', 'gallery'], ['#bgColorBox', 'color'], ['#bgFileBox', 'file']])
      $(box).classList.toggle('hidden', mode !== v);
    set('bgMode', 'theme');
    return;
  }
  set('bgMode', v);
});
$('#openGallery').onclick = () => host.send({ cmd: 'openGallery' });
$('#reloadGallery').onclick = () => host.send({ cmd: 'reloadGallery' });
$('#gallery').addEventListener('click', e => {
  const t = e.target.closest('[data-file]');
  if (t) set('gallery', t.dataset.file);
});
$('#gallery').addEventListener('mouseover', e => e.target.closest('.tile')?.querySelector('video')?.play());

// ---------- background media ----------

const FIT = { cover: 'cover', contain: 'contain', stretch: 'fill' };

/** <img> or <video> for an animated background the page plays itself. */
function mediaHtml(m, still = false) {
  if (!m) return '';
  const style = `object-fit:${FIT[m.fit] || 'cover'}`;
  return m.kind === 'video'
    ? `<video src="${esc(m.url)}" style="${style}" muted loop playsinline ${still ? 'preload="metadata"' : 'autoplay'}></video>`
    : `<img src="${esc(m.url)}" style="${style}" alt="">`;
}

/** Keeps the playing background in place between frames; only a different file restarts it. */
function setMedia(el, m) {
  const key = m ? `${m.url}|${m.fit}` : '';
  if (el.dataset.key === key) return;
  el.dataset.key = key;
  el.innerHTML = mediaHtml(m);
}

function renderGallery(s) {
  const tiles = [{ file: '', name: tr("Theme background") }, ...s.gallery];
  const current = s.bgMode === 'gallery' ? s.galleryFile : s.bgMode === 'theme' ? '' : null;
  $('#gallery').innerHTML = tiles.map(t => `
    <div class="tile variant card bg-base-200 w-32 ${t.file === current ? 'active' : ''}" data-file="${esc(t.file)}" title="${esc(t.name)}">
      <div class="card-body p-2 gap-1 items-center">
        <div class="thumb w-full">${t.media ? mediaHtml(t.media, true) : `<span class="text-xs opacity-60 text-center px-2">${esc(tr("As designed by the theme"))}</span>`}</div>
        <div class="text-xs text-center truncate w-full">${esc(t.name)}</div>
      </div>
    </div>`).join('');
}
$('#pickFile').onclick = () => host.send({ cmd: 'pickFile' });
$('#openThemes').onclick = () => host.send({ cmd: 'openThemes' });
$('#reloadThemes').onclick = () => host.send({ cmd: 'reloadThemes' });
document.addEventListener('click', e => { const u = e.target.closest('[data-url]'); if (u) host.send({ cmd: 'openUrl', url: u.dataset.url }); });

// ---------- sensors ----------

function renderMiniStats() {
  const tiles = [['cpu.temp', 'CPU', '°'], ['gpu.temp', 'GPU', '°'], ['ram.load', 'RAM', '%']];
  $('#miniStats').innerHTML = tiles.map(([k, n, u]) => {
    const v = aliasValues[k];
    return `<div class="bg-base-200 rounded-box px-3 py-2 text-center">
      <div class="text-xs opacity-60">${n}</div>
      <div class="text-xl font-bold tabular-nums">${v == null ? '--' : Math.round(v) + u}</div></div>`;
  }).join('');
}

function renderSensors() {
  const q = $('#sensorFilter').value.trim().toLowerCase();
  const rows = [
    ...SENSOR_KEYS.map(([k, n, u]) => ({ id: k, name: tr(n), hardware: tr("Sensor alias"), type: '', value: aliasValues[k], unit: tr(u), alias: true })),
    ...sensorList,
  ].filter(s => !q || `${s.id} ${s.name} ${s.hardware} ${s.type}`.toLowerCase().includes(q));
  $('#sensorRows').innerHTML = rows.map(s => `<tr class="${s.alias ? 'bg-base-200/50' : ''}">
    <td class="font-mono text-xs">${esc(s.id)}</td>
    <td class="text-right tabular-nums font-medium">${s.value == null ? '<span class="opacity-40">--</span>' : fmt(s.value, Math.abs(s.value) < 100 ? 1 : 0) + esc(s.unit ?? '')}</td>
    <td class="text-xs opacity-70">${esc(s.type)}</td><td class="text-xs opacity-70">${esc(s.hardware)}</td><td class="text-xs">${esc(s.name)}</td></tr>`).join('');
}
$('#sensorFilter').addEventListener('input', renderSensors);
setInterval(() => {
  const tab = $('#tabs .tab-active')?.dataset.tab;
  if (!document.hidden) host.send({ cmd: 'sensors', full: tab === 'sensors' || tab === 'data' });
}, 2000);

// ---------- layout editor ----------

const canvas = $('#canvas');
const cellSize = () => canvas.clientWidth / layout.columns;
const cellH = () => canvas.clientHeight / layout.rows;
let commitTimer = 0;

function commit() {
  clearTimeout(commitTimer);
  commitTimer = setTimeout(() => {
    host.send({ cmd: 'layout', layout });
  }, 120);
}

function describe(it) {
  switch (it.kind) {
    case 'sensor': return (it.label ? it.label + ': ' : '') + sensorName(it.key);
    case 'bar': return tr("Bar:") + ' ' + sensorName(it.key);
    case 'text': return it.text || tr("Text");
    case 'time': return tr("Time") + ' ' + (it.text || 'HH:mm');
    case 'date': return tr("Date") + ' ' + (it.text || 'dd.MM.yyyy');
  }
  return it.kind;
}

function renderEditor() {
  $('#gridSize').value = layout.columns;
  const g = $('#gridLines');
  g.style.backgroundImage = 'linear-gradient(to right, rgba(255,255,255,.10) 1px, transparent 1px), linear-gradient(to bottom, rgba(255,255,255,.10) 1px, transparent 1px)';
  g.style.backgroundSize = `${100 / layout.columns}% ${100 / layout.rows}%`;
  renderItems();
  renderProps();
}

function renderItems() {
  const cw = cellSize(), ch = cellH();
  $('#items').innerHTML = layout.items.map((it, i) => `
    <div class="item${i === selected ? ' selected' : ''}" data-i="${i}"
         style="left:${it.col * cw}px;top:${it.row * ch}px;width:${it.colSpan * cw}px;height:${it.rowSpan * ch}px">
      <span class="tag">${esc(describe(it))}</span><span class="grip"></span>
    </div>`).join('');
}

function select(i) {
  selected = i;
  renderItems();
  renderProps();
}

// drag to move, drag the corner grip to resize; positions snap to the grid
canvas.addEventListener('pointerdown', e => {
  const el = e.target.closest('.item');
  if (!el) { select(-1); return; }
  const i = +el.dataset.i;
  if (i !== selected) select(i);
  const it = layout.items[i];
  const target = canvas.querySelector(`.item[data-i="${i}"]`);
  const resize = e.target.classList.contains('grip');
  const start = { x: e.clientX, y: e.clientY, col: it.col, row: it.row, cs: it.colSpan, rs: it.rowSpan };
  const cw = cellSize(), ch = cellH();
  target.classList.add('dragging');
  canvas.setPointerCapture(e.pointerId);
  let moved = false;
  const move = ev => {
    const dc = Math.round((ev.clientX - start.x) / cw), dr = Math.round((ev.clientY - start.y) / ch);
    if (resize) {
      it.colSpan = Math.max(1, Math.min(layout.columns - it.col, start.cs + dc));
      it.rowSpan = Math.max(1, Math.min(layout.rows - it.row, start.rs + dr));
    } else {
      it.col = Math.max(0, Math.min(layout.columns - it.colSpan, start.col + dc));
      it.row = Math.max(0, Math.min(layout.rows - it.rowSpan, start.row + dr));
    }
    target.style.left = it.col * cw + 'px';
    target.style.top = it.row * ch + 'px';
    target.style.width = it.colSpan * cw + 'px';
    target.style.height = it.rowSpan * ch + 'px';
    if (it.col !== start.col || it.row !== start.row || it.colSpan !== start.cs || it.rowSpan !== start.rs) {
      moved = true;
      commit();
    }
  };
  const up = () => {
    canvas.removeEventListener('pointermove', move);
    canvas.removeEventListener('pointerup', up);
    target.classList.remove('dragging');
    if (moved) renderProps();
  };
  canvas.addEventListener('pointermove', move);
  canvas.addEventListener('pointerup', up);
});

document.addEventListener('keydown', e => {
  if (selected < 0 || $('#editor').classList.contains('hidden') || e.target.closest('input,select,textarea')) return;
  const it = layout.items[selected];
  const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
  if (d) {
    it.col = Math.max(0, Math.min(layout.columns - it.colSpan, it.col + d[0]));
    it.row = Math.max(0, Math.min(layout.rows - it.rowSpan, it.row + d[1]));
    e.preventDefault();
  } else if (e.key === 'Delete') {
    layout.items.splice(selected, 1);
    selected = -1;
  } else return;
  renderItems();
  renderProps();
  commit();
});

$('#gridSize').onchange = e => {
  const n = +e.target.value, k = n / layout.columns;
  for (const it of layout.items) {
    it.colSpan = Math.max(1, Math.round(it.colSpan * k));
    it.rowSpan = Math.max(1, Math.round(it.rowSpan * k));
    it.col = Math.min(n - it.colSpan, Math.round(it.col * k));
    it.row = Math.min(n - it.rowSpan, Math.round(it.row * k));
  }
  layout.columns = layout.rows = n;
  renderEditor();
  commit();
};

$('#resetLayout').onclick = () => {
  if (confirm(tr("Reset to the default layout? Current items will be removed."))) host.send({ cmd: 'resetLayout' });
};

function freeSpot(cs, rs) {
  const n = layout.columns, m = layout.rows;
  const busy = (c, r) => layout.items.some(o => c < o.col + o.colSpan && o.col < c + cs && r < o.row + o.rowSpan && o.row < r + rs);
  const inCircle = (c, r) => {
    for (const [x, y] of [[c, r], [c + cs, r], [c, r + rs], [c + cs, r + rs]]) {
      if (Math.hypot(x / n - .5, y / m - .5) > .5) return false;
    }
    return true;
  };
  for (let r = 0; r <= m - rs; r++) for (let c = 0; c <= n - cs; c++) if (inCircle(c, r) && !busy(c, r)) return [c, r];
  return [Math.floor((n - cs) / 2), Math.floor((m - rs) / 2)];
}

$$('[data-add]').forEach(b => b.onclick = () => {
  const n = layout.columns, kind = b.dataset.add;
  const cs = Math.max(1, Math.round(n * (kind === 'bar' ? .6 : kind === 'sensor' ? .35 : .4)));
  const rs = Math.max(1, Math.round(layout.rows * (kind === 'sensor' ? .2 : .125)));
  const [col, row] = freeSpot(cs, rs);
  const base = { kind, key: 'cpu.temp', text: '', label: '', decimals: 0, unit: '', max: 100, col, row, colSpan: cs, rowSpan: rs,
    color: '#FFFFFF', labelColor: '#8FA3B8', size: 40, bold: true, align: 'center' };
  Object.assign(base, {
    sensor: { label: 'CPU', unit: '°', color: '#00D1FF' },
    bar: { key: 'cpu.load', label: 'CPU {cpu.load:0}%', size: 10, color: '#00D1FF' },
    text: { text: tr("My text") },
    time: { text: 'HH:mm' },
    date: { text: 'dd.MM.yyyy', color: '#8FA3B8', bold: false },
  }[kind]);
  layout.items.push(base);
  select(layout.items.length - 1);
  commit();
});

// ---------- properties of the selected element ----------

function sensorOptions(current) {
  const main = SENSOR_KEYS.map(([k, n]) => `<option value="${k}" ${k === current ? 'selected' : ''}>${esc(tr(n))} — ${k}</option>`).join('');
  const all = sensorList.map(s => `<option value="${esc(s.id)}" ${s.id === current ? 'selected' : ''}>${esc(s.hardware)} · ${esc(s.name)} (${esc(s.type)})</option>`).join('');
  const known = SENSOR_KEYS.some(k => k[0] === current) || sensorList.some(s => s.id === current);
  return `<optgroup label="${esc(tr("Common"))}">${main}</optgroup>` + (all ? `<optgroup label="${esc(tr("All sensors"))}">${all}</optgroup>` : '') +
    (known ? '' : `<option value="${esc(current)}" selected>${esc(current)}</option>`);
}

function field(legend, html, cls = '') { return `<fieldset class="fieldset ${cls}"><legend class="fieldset-legend">${legend}</legend>${html}</fieldset>`; }

function renderProps() {
  const p = $('#props');
  if (selected < 0) {
    p.innerHTML = `<div class="h-full grid place-items-center text-center opacity-60 py-16">
      <div><div class="text-4xl mb-2">👆</div>${esc(tr("Select an item on the display at the left"))}<br>${esc(tr("or add one using the buttons above."))}</div></div>`;
    return;
  }
  const it = layout.items[selected];
  const k = it.kind;
  let html = `<div class="flex items-center gap-2 mb-2">
      <span class="badge badge-primary">${esc(tr(KIND_NAMES[k]))}</span><span class="flex-1"></span>
      <button class="btn btn-sm btn-ghost" id="dupItem">${esc(tr("Duplicate"))}</button>
      <button class="btn btn-sm btn-error btn-soft" id="delItem">${esc(tr("Delete"))}</button></div>
    <div class="grid sm:grid-cols-2 gap-x-4">`;

  if (k === 'sensor' || k === 'bar') {
    html += field(tr("Sensor"), `<select class="select select-sm w-full" data-prop="key">${sensorOptions(it.key)}</select>
      <div class="label">${esc(tr("Now:"))} <span id="liveValue" class="font-mono"></span></div>`, 'sm:col-span-2');
  }
  if (k === 'text') {
    html += field(tr("Content"), `<input class="input input-sm w-full" data-prop="text" value="${esc(it.text)}" placeholder="${esc(tr("Any text"))}">
      <div class="label whitespace-normal">${esc(tr("You can insert values:"))} <kbd class="kbd kbd-xs">{cpu.temp:0}°</kbd> <kbd class="kbd kbd-xs">{gpu.load:0}%</kbd></div>`, 'sm:col-span-2');
  }
  if (k === 'time') {
    html += field(tr("Format"), `<select class="select select-sm w-full" data-prop="text">
      ${['HH:mm', 'HH:mm:ss', 'H:mm', 'hh:mm tt'].map(f => `<option ${f === it.text ? 'selected' : ''}>${f}</option>`).join('')}</select>`);
  }
  if (k === 'date') {
    html += field(tr("Format"), `<select class="select select-sm w-full" data-prop="text">
      ${[['dd.MM.yyyy', '28.10.2026'], ['dd.MM', '28.10'], ['d MMMM', new Intl.DateTimeFormat(I18n.language, {day:'numeric',month:'long'}).format(new Date(2026,9,28))], ['dddd', new Intl.DateTimeFormat(I18n.language, {weekday:'long'}).format(new Date(2026,9,28))], ['ddd, d MMM', new Intl.DateTimeFormat(I18n.language, {weekday:'short',day:'numeric',month:'short'}).format(new Date(2026,9,28))]]
        .map(([f, ex]) => `<option value="${f}" ${f === it.text ? 'selected' : ''}>${ex}</option>`).join('')}</select>`);
  }
  if (k === 'sensor') {
    html += field(tr("Decimal places"), `<div class="join" data-join="decimals">${[0, 1, 2].map(d =>
      `<button class="btn btn-sm join-item ${it.decimals === d ? 'btn-primary' : ''}" data-value="${d}">${d}</button>`).join('')}</div>`);
    html += field(tr("Unit"), `<div class="flex gap-1 flex-wrap"><input class="input input-sm w-20" data-prop="unit" value="${esc(it.unit)}">
      ${['°', '%', tr(" W"), tr(" MHz"), tr(" rpm")].map(u => `<button class="btn btn-xs btn-ghost" data-unit="${u}">${u.trim()}</button>`).join('')}</div>`);
  }
  if (k === 'bar') {
    html += field(tr("Bar maximum"), `<input type="number" class="input input-sm w-28" data-prop="max" data-num value="${it.max}">`);
  }
  html += field(tr("Top label"), `<input class="input input-sm w-full" data-prop="label" value="${esc(it.label)}" placeholder="${esc(tr("No label"))}">`);

  if (k === 'bar') {
    html += field(tr("Thickness"), `<div class="flex items-center gap-3">
      <input type="range" class="range range-xs range-primary flex-1" min="2" max="40" data-prop="size" data-num value="${it.size}">
      <span class="font-mono text-sm w-10 text-right" id="sizeVal">${it.size}</span></div>`);
  } else {
    html += field(tr("Font size"), `<div class="text-sm opacity-70 whitespace-normal">${esc(tr("Sized to the grid cells \u2014 drag the corner to resize"))}</div>`);
  }
  if (k !== 'bar') {
    html += field(tr("Alignment"), `<div class="join" data-join="align">${[['left', tr("Left")], ['center', tr("Center")], ['right', tr("Right")]].map(([v, n]) =>
      `<button class="btn btn-sm join-item ${it.align === v ? 'btn-primary' : ''}" data-value="${v}">${n}</button>`).join('')}</div>`, 'sm:col-span-2');
    html += field(tr("Style"), `<label class="label cursor-pointer gap-2"><input type="checkbox" class="toggle toggle-sm toggle-primary" data-prop="bold" ${it.bold ? 'checked' : ''}> ${esc(tr("Bold"))}</label>`);
  }
  html += field(tr("Color"), '<div class="palette" id="itemColor"></div>', 'sm:col-span-2');
  if (it.label) html += field(tr("Label color"), '<div class="palette" id="labelColor"></div>', 'sm:col-span-2');
  html += field(tr("Position (column, row, width, height in cells)"), ['col', 'row', 'colSpan', 'rowSpan'].map(f =>
    `<input type="number" class="input input-sm w-16" data-prop="${f}" data-num min="${f.endsWith('Span') ? 1 : 0}" value="${it[f]}">`).join(''), 'sm:col-span-2 pos');
  html += '</div>';
  p.innerHTML = html;

  palette($('#itemColor'), TEXT_COLORS, it.color, c => { it.color = c; commit(); renderProps(); });
  if (it.label) palette($('#labelColor'), TEXT_COLORS, it.labelColor, c => { it.labelColor = c; commit(); renderProps(); });
  updatePropValue();

  p.querySelectorAll('[data-prop]').forEach(el => {
    const ev = el.tagName === 'SELECT' || el.type === 'checkbox' ? 'change' : 'input';
    el.addEventListener(ev, () => {
      const prop = el.dataset.prop;
      let v = el.type === 'checkbox' ? el.checked : el.value;
      if ('num' in el.dataset) {
        v = +v;
        if (Number.isNaN(v)) return;
      }
      it[prop] = v;
      if (prop === 'size') $('#sizeVal').textContent = v;
      if (['col', 'row', 'colSpan', 'rowSpan'].includes(prop)) clampItem(it);
      if (prop === 'key' && it.kind === 'sensor') {
        it.unit = tr(SENSOR_KEYS.find(x => x[0] === v)?.[2] ?? it.unit);
        if (!it.label || Object.values(SHORT).includes(it.label)) it.label = SHORT[v] ?? it.label;
      }
      renderItems();
      if (prop === 'key') renderProps();
      commit();
    });
    if (el.dataset.prop === 'label') {
      // the caption takes a grid row of its own: adding one grows the element, erasing it gives the row back
      let had = !!it.label;
      el.addEventListener('input', () => {
        const has = !!it.label;
        if (has === had) return;
        had = has;
        if (has && it.row + it.rowSpan < layout.rows) it.rowSpan++;
        else if (has && it.row > 0) { it.row--; it.rowSpan++; }
        else if (!has && it.rowSpan > 1) it.rowSpan--;
        renderItems();
        $$('#props [data-prop="rowSpan"], #props [data-prop="row"]').forEach(f => f.value = it[f.dataset.prop]);
        commit();
      });
      el.addEventListener('change', renderProps); // shows or hides the caption colour
    }
  });
  p.querySelectorAll('[data-join]').forEach(j => j.addEventListener('click', e => {
    const b = e.target.closest('[data-value]');
    if (!b) return;
    const prop = j.dataset.join;
    it[prop] = prop === 'decimals' ? +b.dataset.value : b.dataset.value;
    setJoin(j, b.dataset.value);
    commit();
  }));
  p.querySelectorAll('[data-unit]').forEach(b => b.onclick = () => { it.unit = b.dataset.unit; commit(); renderProps(); });
  $('#delItem').onclick = () => { layout.items.splice(selected, 1); select(-1); commit(); };
  $('#dupItem').onclick = () => {
    const c = { ...it };
    [c.col, c.row] = freeSpot(c.colSpan, c.rowSpan);
    layout.items.push(c);
    select(layout.items.length - 1);
    commit();
  };
}
function clampItem(it) {
  it.colSpan = Math.max(1, Math.min(layout.columns, it.colSpan | 0));
  it.rowSpan = Math.max(1, Math.min(layout.rows, it.rowSpan | 0));
  it.col = Math.max(0, Math.min(layout.columns - it.colSpan, it.col | 0));
  it.row = Math.max(0, Math.min(layout.rows - it.rowSpan, it.row | 0));
}

function updatePropValue() {
  const el = $('#liveValue');
  if (!el || selected < 0) return;
  const it = layout.items[selected];
  const v = sensorValue(it.key);
  el.textContent = v == null ? tr("No data") : fmt(v, it.decimals ?? 1) + (it.kind === 'sensor' ? it.unit : '');
}

// ---------- window theme ----------

function setUiTheme(dark) {
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
  $('#uiTheme').checked = !dark;
  try { localStorage.setItem('uiTheme', dark ? 'dark' : 'light'); } catch { }
  host.send({ cmd: 'uiTheme', dark });
}
$('#uiTheme').onchange = e => setUiTheme(!e.target.checked);

let savedTheme = 'dark', savedTab = 'image';
try { savedTheme = localStorage.getItem('uiTheme') || 'dark'; savedTab = localStorage.getItem('tab') || 'image'; } catch { }
setUiTheme(savedTheme !== 'light');
activateTab(location.hash.slice(1) || savedTab);
renderMiniStats();
I18n.ready.then(() => host.send({ cmd: 'ready' }));

// ---------- stand-in host for opening the page in a normal browser ----------

function mockHost() {
  let listener = () => { };
  const themes = ['Animation', 'Clock', 'Dashboard'].map(n => ({ name: n, title: n, thumb: null }));
  const s = {
    type: 'state', language: I18n.savedLanguage(), themes, theme: CUSTOM, brightness: 80, rotation: 0, bgMode: 'theme', bgColor: '#05080D', bgFileName: '',
    fit: 'cover', onDevice: true, gallery: [], galleryFile: '', autostart: false, paused: false, cpuHint: true, device: 'COM4: chs_5inch.dev1_rom1.88',
    layoutVersion: 1,
    layout: { columns: 8, rows: 8, background: '#05080D', items: [
      { kind: 'text', key: 'cpu.temp', text: 'SAMA', label: '', decimals: 0, unit: '', max: 100, col: 3, row: 1, colSpan: 2, rowSpan: 1, color: '#8FA3B8', labelColor: '#8FA3B8', size: 24, bold: true, align: 'center' },
      { kind: 'sensor', key: 'cpu.temp', text: '', label: 'CPU', decimals: 0, unit: '°', max: 100, col: 1, row: 2, colSpan: 3, rowSpan: 2, color: '#00D1FF', labelColor: '#8FA3B8', size: 64, bold: true, align: 'center' },
      { kind: 'sensor', key: 'gpu.temp', text: '', label: 'GPU', decimals: 0, unit: '°', max: 100, col: 4, row: 2, colSpan: 3, rowSpan: 2, color: '#C9B8FF', labelColor: '#8FA3B8', size: 64, bold: true, align: 'center' },
      { kind: 'bar', key: 'cpu.load', text: '', label: 'CPU {cpu.load:0}%', decimals: 0, unit: '', max: 100, col: 1, row: 4, colSpan: 6, rowSpan: 1, color: '#00D1FF', labelColor: '#8FA3B8', size: 10, bold: true, align: 'center' },
      { kind: 'time', key: 'cpu.temp', text: 'HH:mm', label: '', decimals: 0, unit: '', max: 100, col: 2, row: 6, colSpan: 4, rowSpan: 1, color: '#FFFFFF', labelColor: '#8FA3B8', size: 40, bold: true, align: 'center' },
    ] },
  };
  const values = { 'cpu.temp': 54, 'gpu.temp': 47.5, 'cpu.load': 23, 'gpu.load': 61, 'ram.load': 38 };
  const draw = () => {
    const c = document.createElement('canvas');
    c.width = c.height = 480;
    const g = c.getContext('2d');
    g.fillStyle = s.layout.background;
    g.fillRect(0, 0, 480, 480);
    const cw = 480 / s.layout.columns;
    for (const it of s.layout.items) {
      const x = (it.col + it.colSpan / 2) * cw, y = (it.row + it.rowSpan / 2) * cw;
      g.fillStyle = it.color;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      const text = it.kind === 'sensor' ? Math.round(values[it.key] ?? 0) + it.unit : it.kind === 'time' ? new Date().toTimeString().slice(0, 5) : it.kind === 'bar' ? '▬▬▬▬▬▬' : it.text;
      g.font = `${it.bold ? 'bold ' : ''}100px Segoe UI`;
      const size = Math.min(it.rowSpan * cw * .85, (it.colSpan * cw - 12) / g.measureText(text || ' ').width * 100);
      g.font = `${it.bold ? 'bold ' : ''}${size}px Segoe UI`;
      g.fillText(text, x, y);
    }
    listener({ type: 'frame', data: c.toDataURL('image/jpeg') });
  };
  return {
    send(m) {
      if (m.cmd === 'ready') { listener(structuredClone(s)); draw(); listener({ type: 'status', textKey: 'Connected: {0}', textArgs: ['COM4'], connected: true, device: s.device }); }
      if (m.cmd === 'sensors') listener({ type: 'sensors', aliases: values, list: [{ id: '/amdcpu/0/load/0', name: 'CPU Total', hardware: 'AMD Ryzen 7 7700X', type: 'Load', value: 23 }] });
      if (m.cmd === 'layout') { s.layout = m.layout; draw(); }
      if (m.cmd === 'set') { s[m.key] = m.value; if (m.key === 'language') I18n.saveLanguage(m.value); if (m.key !== 'brightness') listener(structuredClone(s)); }
    },
    on(f) { listener = f; },
  };
}
