// Milligram viewer: fetches a view from the server, lays it out with ELK, draws it as SVG.
const $ = (sel) => document.querySelector(sel);
const SVG = 'http://www.w3.org/2000/svg';
const FONT = 'Inter, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif';
const MONO = '"JetBrains Mono", "Cascadia Code", Menlo, Consolas, monospace';
const FONTS = {
  title: `600 13px ${FONT}`,
  component: `700 13.5px ${FONT}`,
  member: `11px ${MONO}`,
  stereo: `10.5px ${FONT}`,
  caption: `10.5px ${FONT}`,
};
const MAX_MEMBERS = 14;
const MAX_SIGNATURE = 58;
const PAD = 10;
const BADGE_ROW = 16;
const COMPONENT_HEADER = 30;
const VIS = { public: '+', internal: '~', protected: '#', protectedInternal: '#', privateProtected: '-', private: '-' };
const ICONS = { interface: 'I', abstract: 'α', static: 'S', enum: 'E', record: 'R', delegate: 'D', struct: 's' };
const ROOT_OPTIONS = {
  'elk.algorithm': 'layered',
  'elk.direction': 'DOWN',
  'elk.hierarchyHandling': 'INCLUDE_CHILDREN',
  'elk.edgeRouting': 'ORTHOGONAL',
  'elk.json.shapeCoords': 'ROOT',
  'elk.json.edgeCoords': 'ROOT',
  'elk.partitioning.activate': 'true',
  'elk.spacing.nodeNode': '34',
  'elk.layered.spacing.nodeNodeBetweenLayers': '56',
  'elk.spacing.edgeNode': '18',
  'elk.spacing.edgeEdge': '10',
  'elk.layered.spacing.edgeNodeBetweenLayers': '18',
  'elk.layered.considerModelOrder.strategy': 'NODES_AND_EDGES',
  'elk.layered.nodePlacement.strategy': 'NETWORK_SIMPLEX',
  'elk.padding': '[top=24,left=24,bottom=24,right=24]',
};

const elk = new ELK();
const measureContext = document.createElement('canvas').getContext('2d');

const state = {
  meta: null,
  view: null,
  layout: null,
  context: 'real',
  focus: null,
  arrows: localStorage.getItem('mg.arrows') || 'bundled',
  detail: localStorage.getItem('mg.detail') || 'members',
  selected: null,
  card: null,
  zoom: 1,
  pan: { x: 24, y: 24 },
  token: 0,
  lastJob: '',
};

// ---------------------------------------------------------------- helpers

function el(tag, attrs = {}, parent = null, text = null) {
  const node = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attrs)) if (value !== undefined && value !== null) node.setAttribute(key, value);
  if (text !== null) node.textContent = text;
  if (parent) parent.appendChild(node);
  return node;
}

function html(text) {
  return String(text ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function textWidth(text, font) {
  measureContext.font = font;
  return measureContext.measureText(text).width;
}

function groupBy(items, key) {
  const map = new Map();
  for (const item of items) {
    const k = key(item);
    if (!map.has(k)) map.set(k, []);
    map.get(k).push(item);
  }
  return map;
}

function debounce(fn, ms) {
  let timer;
  return (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), ms); };
}

function ago(iso) {
  if (!iso || iso.startsWith('0001')) return 'never';
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return 'just now';
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)} h ago`;
  return `${Math.round(seconds / 86400)} d ago`;
}

function toast(message, kind = '') {
  if (!message) return;
  const node = document.createElement('div');
  node.className = `toast ${kind}`;
  node.textContent = message;
  $('#toasts').appendChild(node);
  setTimeout(() => node.remove(), kind === 'agent' ? 9000 : 4500);
}

function clip(text, max) {
  return text.length <= max ? text : `${text.slice(0, max - 1)}…`;
}

function average(values) {
  const known = values.filter((v) => v !== null && v !== undefined);
  return known.length ? known.reduce((a, b) => a + b, 0) / known.length : null;
}

// Grades are 1 (worst) to 10 (best); null is unknown.
function gradeHue(grade) { return ((grade - 1) / 9) * 120; }
function fillFor(grades, alpha) {
  const grade = average([grades?.crap, grades?.mutation]);
  if (grade === null) return `hsla(222, 14%, 28%, ${alpha})`;
  return `hsla(${gradeHue(grade)}, 52%, 30%, ${alpha})`;
}
function strokeFor(grades) {
  const grade = average([grades?.crap, grades?.mutation]);
  return grade === null ? '#3a4466' : `hsl(${gradeHue(grade)}, 55%, 48%)`;
}
function dotColor(grade) { return grade === null || grade === undefined ? 'none' : `hsl(${gradeHue(grade)}, 70%, 55%)`; }

// ---------------------------------------------------------------- server

const api = {
  async get(path, params = {}) {
    const query = new URLSearchParams(Object.entries(params).filter(([, v]) => v !== null && v !== undefined));
    const response = await fetch(`${path}?${query}`);
    if (!response.ok) throw new Error(`${path}: ${response.status}`);
    return response.json();
  },
  async post(path, body) {
    const response = await fetch(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Milligram': '1' },
      body: JSON.stringify(body),
    });
    return response.json().catch(() => ({ ok: false, message: `${path}: ${response.status}` }));
  },
};

async function action(op, extra = {}) {
  const result = await api.post('/api/action', { op, context: state.context, focus: state.focus, ...extra });
  if (!result.ok) toast(result.message || 'Failed.', 'error');
  else if (result.message) toast(result.message);
  refreshMeta();
  return result;
}

// ---------------------------------------------------------------- navigation

function readHash() {
  const params = new URLSearchParams(location.hash.slice(1));
  state.context = params.get('c') || 'real';
  state.focus = params.get('f') || null;
}

function writeHash() {
  const params = new URLSearchParams({ c: state.context });
  if (state.focus) params.set('f', state.focus);
  const hash = `#${params}`;
  if (location.hash !== hash) history.pushState(null, '', hash);
}

/** `mail` tells the agent which diagram is now under discussion (user choices only, never agent requests). */
function navigate(context, focus, { mail = false } = {}) {
  saveCamera();
  state.context = context;
  state.focus = focus;
  state.selected = null;
  writeHash();
  if (mail) action('context');
  refresh();
}

function up() {
  const crumbs = state.view?.breadcrumbs ?? [];
  if (crumbs.length > 1) navigate(state.context, crumbs[crumbs.length - 2].id);
}

// ---------------------------------------------------------------- refresh pipeline

async function refresh({ keepCamera = false } = {}) {
  const token = ++state.token;
  let meta, view;
  try {
    [meta, view] = await Promise.all([
      api.get('/api/meta'),
      api.get('/api/view', { context: state.context, focus: state.focus }),
    ]);
  } catch (error) {
    $('#status').textContent = 'Viewer disconnected.';
    return;
  }
  if (token !== state.token) return;
  state.meta = meta;
  state.view = view;
  state.context = view.context.id;
  state.focus = view.focus.id;
  if (state.selected && !view.nodes.some((n) => n.id === state.selected)) state.selected = null;
  renderChrome();
  const layout = await layoutView(view);
  if (token !== state.token) return;
  state.layout = layout;
  draw(layout, keepCamera);
  if (state.card) refreshCard();
}

const scheduleRefresh = debounce(() => refresh({ keepCamera: true }), 250);

async function refreshMeta() {
  try {
    state.meta = await api.get('/api/meta');
    renderContexts();
    renderAgent();
    renderMetrics();
    renderJob();
    renderStatus();
  } catch { /* the next refresh reports it */ }
}

// ---------------------------------------------------------------- layout

function leafSpec(node) {
  if (node.kind === 'foreign') {
    return { shape: 'ellipse', w: Math.max(textWidth(node.label, FONTS.title) + 44, 96), h: 38, lines: [{ text: node.label, cls: 'title', font: FONTS.title, center: true, y: 23 }] };
  }
  const lines = [];
  let y = BADGE_ROW + 4;
  const add = (text, cls, font, { center = true, height = 15, extra = {} } = {}) => {
    y += height;
    lines.push({ text, cls, font, center, y, ...extra });
  };
  const isType = !!node.typeId;
  if (isType) {
    if (node.stereotype && node.stereotype !== 'class') add(`«${node.stereotype}»`, 'stereo', FONTS.stereo, { height: 12 });
    const italic = ['interface', 'enum', 'abstract', 'delegate'].includes(node.stereotype);
    add(node.label, `title${italic ? ' italic' : ''}`, FONTS.title);
    if (node.kind === 'type' && !node.parent && state.detail === 'members' && node.members.length) {
      y += 6;
      for (const member of node.members.slice(0, MAX_MEMBERS)) {
        const cls = `member${member.isStatic ? ' static' : ''}${member.isAbstract ? ' abstract' : ''}`;
        add(`${VIS[member.visibility] ?? '+'} ${clip(member.signature, MAX_SIGNATURE)}`, cls, FONTS.member, { center: false, height: 14 });
      }
      if (node.members.length > MAX_MEMBERS) add(`… ${node.members.length - MAX_MEMBERS} more`, 'caption', FONTS.caption, { center: false, height: 14 });
    }
  } else {
    add(node.label, 'title italic', FONTS.title);
    const contents = node.contents ?? [];
    if (node.kind === 'component' && contents.length) {
      y += 4;
      for (const name of contents.slice(0, 8)) add(name, 'caption', FONTS.caption, { center: false, height: 13 });
      if (contents.length > 8) add(`… ${contents.length - 8} more`, 'caption', FONTS.caption, { center: false, height: 13 });
    }
    add(node.kind === 'external' ? 'outside this view' : `${node.typeCount} type${node.typeCount === 1 ? '' : 's'}`, 'caption', FONTS.caption, { height: 14 });
  }
  const width = Math.max(96, ...lines.map((l) => textWidth(l.text, l.font) + 2 * PAD + (l.center ? 16 : 0)));
  return { shape: node.kind === 'package' ? 'package' : 'rect', w: Math.ceil(width), h: y + PAD, lines };
}

function edgesFor(view, visible, topOf, bundledOnly) {
  const bundles = new Map();
  const detailed = state.arrows === 'detailed' && !bundledOnly;
  for (const edge of view.edges) {
    const fromTop = topOf(edge.from);
    const toTop = topOf(edge.to);
    let from = edge.from;
    let to = edge.to;
    if (!detailed && fromTop === toTop) continue;
    if (!visible.has(from) || !visible.has(to) || !detailed) {
      from = fromTop;
      to = toTop;
    }
    if (from === to || !visible.has(from) || !visible.has(to)) continue;
    const key = `${from}\u0000${to}`;
    let bundle = bundles.get(key);
    if (!bundle) bundles.set(key, (bundle = { id: `e${bundles.size}`, from, to, kinds: new Set(), violating: false, pairs: [], involved: new Set() }));
    bundle.kinds.add(edge.kind);
    bundle.violating ||= edge.violating;
    bundle.pairs.push(...edge.pairs);
    for (const id of [edge.from, edge.to, fromTop, toTop]) bundle.involved.add(id);
  }
  return [...bundles.values()].map((b) => ({ ...b, kind: b.kinds.size === 1 ? [...b.kinds][0] : 'dependency' }));
}

async function layoutView(view) {
  const closed = state.detail === 'boxes';
  const byId = new Map(view.nodes.map((n) => [n.id, n]));
  const tops = view.nodes.filter((n) => !n.parent || !byId.has(n.parent));
  const children = groupBy(view.nodes.filter((n) => n.parent && byId.has(n.parent) && !closed), (n) => n.parent);
  const topOf = (id) => {
    const node = byId.get(id);
    return node?.parent && byId.has(node.parent) ? node.parent : id;
  };
  const visible = new Set(closed ? tops.map((n) => n.id) : view.nodes.map((n) => n.id));
  const maxLevel = view.maxLevel ?? 0;
  const partition = (n) => String(n.level === null || n.level === undefined ? maxLevel + 1 : maxLevel - n.level);
  const specs = new Map();

  const leaf = (n) => {
    const spec = leafSpec(n);
    specs.set(n.id, spec);
    return { id: n.id, width: spec.w, height: spec.h, layoutOptions: { 'elk.partitioning.partition': partition(n) } };
  };
  // Detailed arrows cross into components, so the whole hierarchy is one layered layout.
  // Otherwise each component packs its contents into a compact grid.
  const hierarchical = state.arrows === 'detailed';
  const top = (n) => {
    const kids = children.get(n.id) ?? [];
    if (n.kind !== 'component' || !kids.length) return leaf(n);
    const minWidth = Math.max(textWidth(n.label, FONTS.component) + 110, 170);
    specs.set(n.id, { compound: true });
    const common = {
      'elk.partitioning.partition': partition(n),
      'elk.padding': `[top=${COMPONENT_HEADER + 14},left=16,bottom=16,right=16]`,
      'elk.nodeSize.constraints': 'MINIMUM_SIZE',
      'elk.nodeSize.minimum': `(${Math.ceil(minWidth)},80)`,
    };
    const inner = hierarchical
      ? { 'elk.partitioning.activate': 'true' }
      : { 'elk.algorithm': 'rectpacking', 'elk.aspectRatio': '1.6', 'elk.spacing.nodeNode': '14' };
    return { id: n.id, children: kids.map(leaf), layoutOptions: { ...common, ...inner } };
  };

  const edges = edgesFor(view, visible, topOf, false);
  const layoutEdges = state.arrows === 'hidden' ? edgesFor(view, visible, topOf, true) : edges;
  const graph = {
    id: 'root',
    layoutOptions: { ...ROOT_OPTIONS, 'elk.hierarchyHandling': hierarchical ? 'INCLUDE_CHILDREN' : 'SEPARATE_CHILDREN' },
    children: tops.map(top),
    edges: layoutEdges.map((e) => ({ id: e.id, sources: [e.from], targets: [e.to] })),
  };
  const result = await elk.layout(graph);
  const boxes = new Map();
  const routes = new Map();
  const walk = (node) => {
    for (const edge of node.edges ?? []) routes.set(edge.id, edge.sections ?? []);
    for (const child of node.children ?? []) {
      boxes.set(child.id, { x: child.x, y: child.y, w: child.width, h: child.height });
      walk(child);
    }
  };
  walk(result);
  return {
    view, byId, boxes, routes, specs, topOf, visible, edges,
    width: result.width, height: result.height,
    triangles: state.arrows === 'hidden' ? triangles(view, visible, topOf) : null,
  };
}

/** For hidden arrows: per box, the dependencies coming in (top) and going out (bottom). */
function triangles(view, visible, topOf) {
  const map = new Map();
  const get = (id) => {
    if (!map.has(id)) map.set(id, { incoming: [], outgoing: [] });
    return map.get(id);
  };
  for (const edge of view.edges) {
    const fromTop = topOf(edge.from);
    const toTop = topOf(edge.to);
    const outs = new Set([edge.from]);
    const ins = new Set([edge.to]);
    if (fromTop !== toTop) { outs.add(fromTop); ins.add(toTop); }
    for (const id of outs) if (visible.has(id)) get(id).outgoing.push(...edge.pairs);
    for (const id of ins) if (visible.has(id)) get(id).incoming.push(...edge.pairs);
  }
  return map;
}

// ---------------------------------------------------------------- drawing

function markers() {
  const defs = $('#defs');
  defs.replaceChildren();
  const colors = { n: '#7d869e', v: '#ff5c5c', h: '#ffd166' };
  for (const [key, color] of Object.entries(colors)) {
    const open = el('marker', { id: `open-${key}`, viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 9, markerHeight: 9, orient: 'auto-start-reverse', markerUnits: 'userSpaceOnUse' }, defs);
    el('path', { d: 'M1,1 L9,5 L1,9', fill: 'none', stroke: color, 'stroke-width': 1.5 }, open);
    const hollow = el('marker', { id: `tri-${key}`, viewBox: '0 0 12 12', refX: 11, refY: 6, markerWidth: 13, markerHeight: 13, orient: 'auto-start-reverse', markerUnits: 'userSpaceOnUse' }, defs);
    el('path', { d: 'M1,1 L11,6 L1,11 Z', fill: '#0f1320', stroke: color, 'stroke-width': 1.4 }, hollow);
  }
}

function markerFor(edge, hot) {
  const shape = edge.kind === 'inheritance' || edge.kind === 'implements' ? 'tri' : 'open';
  const color = edge.violating ? 'v' : hot ? 'h' : 'n';
  return `url(#${shape}-${color})`;
}

function roundedPath(points, radius = 6) {
  if (points.length < 2) return '';
  let d = `M${points[0].x},${points[0].y}`;
  for (let i = 1; i < points.length - 1; i++) {
    const [a, b, c] = [points[i - 1], points[i], points[i + 1]];
    const r = Math.min(radius, Math.hypot(b.x - a.x, b.y - a.y) / 2, Math.hypot(c.x - b.x, c.y - b.y) / 2);
    const p1 = towards(b, a, r);
    const p2 = towards(b, c, r);
    d += ` L${p1.x},${p1.y} Q${b.x},${b.y} ${p2.x},${p2.y}`;
  }
  const last = points[points.length - 1];
  return `${d} L${last.x},${last.y}`;
}

function towards(from, to, distance) {
  const length = Math.hypot(to.x - from.x, to.y - from.y) || 1;
  return { x: from.x + ((to.x - from.x) / length) * distance, y: from.y + ((to.y - from.y) / length) * distance };
}

function draw(layout, keepCamera) {
  const viewport = $('#viewport');
  viewport.replaceChildren();
  const layers = Object.fromEntries(['components', 'nodes', 'edges', 'titles', 'tris'].map((name) => [name, el('g', { class: name }, viewport)]));

  for (const node of layout.view.nodes) {
    const box = layout.boxes.get(node.id);
    if (!box) continue;
    const spec = layout.specs.get(node.id);
    if (spec?.compound) drawComponent(node, box, layers.components, layers.titles);
    else drawLeaf(node, box, spec, layers.nodes);
  }
  if (layout.triangles) drawTriangles(layout, layers.tris);
  else for (const edge of layout.edges) drawEdge(edge, layout.routes.get(edge.id) ?? [], layers.edges);

  $('#empty').hidden = layout.view.nodes.length > 0;
  $('#empty').textContent = emptyMessage();
  applySelection();
  if (!keepCamera && !restoreCamera()) fit();
  applyCamera();
}

function emptyMessage() {
  const meta = state.meta;
  if (!meta) return '';
  if (meta.types === 0 && meta.job?.state === 'running') return 'Scanning the source…';
  if (meta.types === 0) return 'No C# types found. Check "src" and "exclude" in milligram.json, then press Regen.';
  return state.view?.context.isProposal ? 'This proposal is empty. Describe it to the agent.' : 'Nothing at this level.';
}

function drawComponent(node, box, parent, titles) {
  const g = el('g', { class: `node component${node.isGroup ? ' group' : ''}`, 'data-id': node.id }, parent);
  el('rect', { class: 'body', x: box.x, y: box.y, width: box.w, height: box.h, rx: 10, fill: fillFor(node.grades, 0.16), stroke: strokeFor(node.grades), 'stroke-width': 1.5 }, g);
  el('line', { x1: box.x, x2: box.x + box.w, y1: box.y + COMPONENT_HEADER, y2: box.y + COMPONENT_HEADER, stroke: strokeFor(node.grades), 'stroke-opacity': 0.4 }, g);
  const title = el('g', { class: 'component-title' }, titles);
  const levelText = levelLabel(node);
  const labelX = box.x + 12 + (levelText ? textWidth(levelText, `10px ${MONO}`) + 8 : 0);
  el('rect', { x: labelX - 4, y: box.y + 6, width: textWidth(node.label, FONTS.component) + 8, height: 19, rx: 4 }, title);
  el('text', { x: labelX, y: box.y + 20 }, title, node.label);
  const badges = el('g', { class: 'node-badges' }, titles);
  if (levelText) el('text', { class: 'badge', x: box.x + 10, y: box.y + 19, fill: '#8b93a7', 'font-size': 10, 'font-family': MONO }, badges, levelText);
  drawDots(node, box.x + box.w - 12, box.y + 15, badges);
}

function drawLeaf(node, box, spec, parent) {
  const g = el('g', { class: `node ${node.kind}`, 'data-id': node.id }, parent);
  const alpha = node.kind === 'type' ? 0.92 : node.kind === 'package' ? 0.6 : 0.35;
  const fill = node.kind === 'external' ? 'rgba(20,24,40,.9)' : fillFor(node.grades, alpha);
  const stroke = node.kind === 'external' ? '#4a5475' : strokeFor(node.grades);
  if (spec.shape === 'ellipse') {
    el('ellipse', { class: 'body', cx: box.x + box.w / 2, cy: box.y + box.h / 2, rx: box.w / 2, ry: box.h / 2, fill: '#1b2138', stroke: '#56607a', 'stroke-width': 1.3 }, g);
  } else if (spec.shape === 'package') {
    const tab = Math.min(56, box.w * 0.4);
    el('path', { d: `M${box.x},${box.y + 8} v-6 a2,2 0 0 1 2,-2 h${tab - 4} a2,2 0 0 1 2,2 v6`, fill, stroke, 'stroke-width': 1.3 }, g);
    el('rect', { class: 'body', x: box.x, y: box.y + 8, width: box.w, height: box.h - 8, rx: 4, fill, stroke, 'stroke-width': 1.3 }, g);
  } else {
    el('rect', { class: 'body', x: box.x, y: box.y, width: box.w, height: box.h, rx: 6, fill, stroke, 'stroke-width': 1.3 }, g);
  }
  const offset = spec.shape === 'package' ? 4 : 0;
  for (const line of spec.lines) {
    const x = line.center ? box.x + box.w / 2 : box.x + PAD;
    el('text', { class: line.cls, x, y: box.y + line.y + offset, 'text-anchor': line.center ? 'middle' : 'start' }, g, line.text);
  }
  if (node.kind !== 'foreign') {
    const levelText = levelLabel(node);
    if (levelText) el('text', { class: 'badge', x: box.x + 6, y: box.y + 13 + offset }, g, levelText);
    let right = box.x + box.w - 10;
    if (node.kind !== 'external') right = drawDots(node, right, box.y + 10 + offset, g);
    const icon = ICONS[node.stereotype];
    if (icon) el('text', { class: 'icon', x: right - 2, y: box.y + 14 + offset, 'text-anchor': 'end' }, g, icon);
  }
}

function levelLabel(node) {
  return node.level === null || node.level === undefined ? '' : `L${node.level}`;
}

/** C and M dots, right to left from x; returns the x left of them. */
function drawDots(node, x, y, parent) {
  const entries = [['M', node.grades?.mutation, 'Mutation'], ['C', node.grades?.crap, 'CRAP']];
  for (const [, grade, name] of entries) {
    const dot = el('circle', { cx: x, cy: y, r: 4.5, fill: dotColor(grade), stroke: grade == null ? '#56607a' : 'none', 'stroke-width': 1 }, parent);
    el('title', {}, dot, `${name}: ${grade == null ? 'unknown' : `${grade}/10`}`);
    x -= 12;
  }
  return x - 2;
}

function drawEdge(edge, sections, parent) {
  const points = sections.flatMap((s, i) => [...(i === 0 ? [s.startPoint] : []), ...(s.bendPoints ?? []), s.endPoint]);
  if (points.length < 2) return;
  const g = el('g', { class: `edge ${edge.kind}${edge.violating ? ' violating' : ''}`, 'data-edge': edge.id }, parent);
  const d = roundedPath(points);
  el('path', { class: 'line', d, 'marker-end': markerFor(edge, false) }, g);
  el('path', { class: 'hit', d }, g);
}

function drawTriangles(layout, parent) {
  for (const [id, lists] of layout.triangles) {
    const box = layout.boxes.get(id);
    if (!box) continue;
    const cx = box.x + box.w / 2;
    if (lists.incoming.length) triangle(cx, box.y - 1, 'in', lists.incoming, id, parent);
    if (lists.outgoing.length) triangle(cx, box.y + box.h + 1, 'out', lists.outgoing, id, parent);
  }
}

function triangle(cx, y, direction, pairs, id, parent) {
  const violating = pairs.some((p) => p.violating);
  const d = direction === 'in' ? `M${cx - 7},${y - 9} L${cx + 7},${y - 9} L${cx},${y} Z` : `M${cx - 7},${y} L${cx + 7},${y} L${cx},${y + 9} Z`;
  const tri = el('path', { class: 'tri', d, fill: violating ? '#ff5c5c' : '#7d869e', 'data-tri': `${id}:${direction}` }, parent);
  tri.pairs = pairs;
  tri.direction = direction;
}

// ---------------------------------------------------------------- selection

function select(id) {
  state.selected = id;
  applySelection();
}

function applySelection() {
  const layout = state.layout;
  if (!layout) return;
  const id = state.selected;
  for (const node of document.querySelectorAll('#viewport .node')) node.classList.toggle('selected', node.dataset.id === id);
  for (const g of document.querySelectorAll('#viewport .edge')) {
    const edge = layout.edges.find((e) => e.id === g.dataset.edge);
    const hot = !!id && edge.involved.has(id);
    g.classList.toggle('hot', hot);
    g.classList.toggle('dim', !!id && !hot);
    g.querySelector('.line').setAttribute('marker-end', markerFor(edge, hot));
  }
  renderSelection();
}

// ---------------------------------------------------------------- camera

function applyCamera() {
  $('#viewport').setAttribute('transform', `translate(${state.pan.x},${state.pan.y}) scale(${state.zoom})`);
  $('#zoom-level').textContent = `${Math.round(state.zoom * 100)}%`;
}

function cameraKey() { return `mg.camera.${state.context}.${state.focus ?? ''}.${state.detail}`; }

function saveCamera() {
  sessionStorage.setItem(cameraKey(), JSON.stringify({ zoom: state.zoom, pan: state.pan }));
}

function restoreCamera() {
  const saved = sessionStorage.getItem(cameraKey());
  if (!saved) return false;
  Object.assign(state, JSON.parse(saved));
  return true;
}

function fit() {
  const layout = state.layout;
  const stage = $('#stage').getBoundingClientRect();
  if (!layout || !layout.width) return;
  state.zoom = Math.max(0.2, Math.min(1, (stage.width - 40) / layout.width, (stage.height - 40) / layout.height));
  state.pan = { x: Math.max(20, (stage.width - layout.width * state.zoom) / 2), y: 20 };
}

function zoomAt(factor, clientX, clientY) {
  const stage = $('#stage').getBoundingClientRect();
  const x = (clientX ?? stage.left + stage.width / 2) - stage.left;
  const y = (clientY ?? stage.top + stage.height / 2) - stage.top;
  const zoom = Math.max(0.1, Math.min(4, state.zoom * factor));
  state.pan = { x: x - ((x - state.pan.x) * zoom) / state.zoom, y: y - ((y - state.pan.y) * zoom) / state.zoom };
  state.zoom = zoom;
  applyCamera();
  saveCamera();
}

function panBy(dx, dy) {
  state.pan = { x: state.pan.x + dx, y: state.pan.y + dy };
  applyCamera();
  saveCamera();
}

// ---------------------------------------------------------------- chrome

function renderChrome() {
  const meta = state.meta;
  const view = state.view;
  document.title = `${meta.title} — Milligram`;
  $('#title').textContent = meta.title;
  $('#subtitle').textContent = `${meta.types} types · ${meta.prefix || '(no prefix)'} · ${meta.root}`;
  $('#policy-error').hidden = !meta.policyError;
  $('#policy-error').textContent = meta.policyError ?? '';
  $('#banner').hidden = !view.context.isProposal;
  $('#banner').textContent = view.context.isProposal ? `PROPOSAL “${view.context.name}” — not instantiated in code` : '';
  $('#up').disabled = view.breadcrumbs.length <= 1;
  const crumbs = $('#crumbs');
  crumbs.replaceChildren();
  view.breadcrumbs.forEach((crumb, i) => {
    if (i > 0) crumbs.appendChild(Object.assign(document.createElement('span'), { className: 'sep', textContent: '›' }));
    const last = i === view.breadcrumbs.length - 1;
    const node = document.createElement(last ? 'span' : 'a');
    node.textContent = crumb.label;
    if (last) node.className = 'here';
    else node.onclick = () => navigate(state.context, crumb.id);
    crumbs.appendChild(node);
  });
  for (const [id, key] of [['#arrows', 'arrows'], ['#detail', 'detail']])
    for (const button of $(id).querySelectorAll('button')) button.classList.toggle('on', button.dataset.v === state[key]);
  renderContexts();
  renderAgent();
  renderMetrics();
  renderJob();
  renderStatus();
}

function renderContexts() {
  const list = $('#contexts');
  list.replaceChildren();
  const contexts = state.meta?.contexts ?? [];
  contexts.forEach((context, i) => {
    if (i === 1) list.appendChild(Object.assign(document.createElement('li'), { className: 'header', textContent: 'Proposals' }));
    const item = document.createElement('li');
    item.className = context.id === state.context ? 'on' : '';
    item.innerHTML = `<span>${html(context.name)}</span>${context.isProposal ? '<span class="tag">proposal</span>' : ''}`;
    item.onclick = () => navigate(context.id, null, { mail: true });
    if (context.isProposal) item.oncontextmenu = (e) => { e.preventDefault(); showMenu(proposalMenu(context), e.clientX, e.clientY); };
    list.appendChild(item);
  });
}

function proposalMenu(context) {
  return [
    { label: 'Rename…', run: async () => {
      const name = prompt('Proposal name', context.name);
      if (name) { await action('rename-proposal', { context: context.id, name }); refresh({ keepCamera: true }); }
    } },
    { label: 'Delete', run: async () => {
      if (!confirm(`Delete proposal “${context.name}”?`)) return;
      await action('delete-proposal', { context: context.id });
      if (state.context === context.id) navigate('real', null, { mail: true });
      else refresh({ keepCamera: true });
    } },
  ];
}

function renderAgent() {
  const agent = state.meta?.agent;
  const box = $('#agent');
  if (!agent) return;
  if (!agent.available) {
    box.innerHTML = `<div><span class="dot bad"></span> Not available — ${html(agent.reason)}</div>`;
  } else if (agent.running) {
    box.innerHTML = `<div><span class="dot ok"></span> Running · <a id="open-terminal">open terminal</a></div>
      <div class="small muted"><code>${html(agent.attach)}</code></div>
      ${agent.pendingMail ? `<div class="small muted">${agent.pendingMail} unread message(s)</div>` : ''}`;
    $('#open-terminal').onclick = () => action('open-terminal');
  } else {
    box.innerHTML = `<div class="row"><span class="dot"></span> Not running <button id="start-agent">Start</button></div>`;
    $('#start-agent').onclick = () => action('start-agent');
  }
}

function renderMetrics() {
  const metrics = state.meta?.metrics;
  if (!metrics) return;
  $('#metrics').innerHTML = `<dl class="kv"><dt>Model</dt><dd>${ago(state.meta.generatedAt)}</dd>
    <dt>CRAP</dt><dd>${ago(metrics.crapAt)}</dd><dt>Mutation</dt><dd>${ago(metrics.mutationAt)}</dd></dl>`;
}

function renderJob() {
  const job = state.meta?.job;
  if (!job) return;
  const section = $('#job-section');
  section.hidden = job.state === 'idle';
  if (section.hidden) return;
  const verb = { queued: 'Queued', running: 'Running…', succeeded: 'Done', failed: 'Failed' }[job.state] ?? job.state;
  $('#job').innerHTML = `<b>${html(job.name)}</b> — ${verb}${job.message ? `: ${html(job.message)}` : ''}${job.queued?.length ? `<div class="muted">Queued: ${job.queued.map(html).join(', ')}</div>` : ''}`;
  const log = $('#job-log');
  log.textContent = (job.log ?? []).slice(-60).join('\n');
  log.scrollTop = log.scrollHeight;
  const key = `${job.name}|${job.startedAt}|${job.state}`;
  if (key !== state.lastJob && (job.state === 'succeeded' || job.state === 'failed') && state.lastJob) {
    toast(`${job.name}: ${job.message ?? job.state}`, job.state === 'failed' ? 'error' : '');
  }
  state.lastJob = key;
}

function renderStatus() {
  const job = state.meta?.job;
  const status = $('#status');
  if (job && (job.state === 'running' || job.queued?.length)) status.innerHTML = `<span class="spin"></span>${html(job.name || job.queued[0])}`;
  else status.textContent = state.meta ? `model ${ago(state.meta.generatedAt)}` : '';
}

function renderSelection() {
  const box = $('#selection');
  const node = state.selected ? state.layout?.byId.get(state.selected) : null;
  if (!node) {
    box.className = 'muted';
    box.textContent = 'Click a box. Double-click to open it.';
    return;
  }
  box.className = '';
  const grade = (g) => (g === null || g === undefined ? 'unknown' : `${g}/10`);
  const rows = [
    ['Kind', node.stereotype ? `${node.kind} (${node.stereotype})` : node.kind],
    node.namespace ? ['Namespace', node.namespace] : null,
    node.target ? ['Path', node.target] : null,
    ['Level', node.level ?? '—'],
    node.kind !== 'foreign' ? ['Types', node.typeCount] : null,
    node.kind !== 'foreign' ? ['CRAP', grade(node.grades?.crap)] : null,
    node.kind !== 'foreign' ? ['Mutation', grade(node.grades?.mutation)] : null,
  ].filter(Boolean);
  box.innerHTML = `<div><b>${html(node.label)}</b></div><dl class="kv">${rows.map(([k, v]) => `<dt>${k}</dt><dd>${html(v)}</dd>`).join('')}</dl>
    <div class="buttons">${node.typeId ? '<button data-do="card">Open card</button>' : ''}${node.drill ? '<button data-do="drill">Open</button>' : ''}</div>`;
  box.querySelector('[data-do="card"]')?.addEventListener('click', () => openCard(node.typeId));
  box.querySelector('[data-do="drill"]')?.addEventListener('click', () => navigate(state.context, node.drill));
}

function selectionInfo() {
  const node = state.selected ? state.layout?.byId.get(state.selected) : null;
  if (!node) return null;
  return { id: node.id, kind: node.kind, label: node.label, target: node.target ?? null, typeId: node.typeId ?? null, namespace: node.namespace ?? null };
}

function renderLegend() {
  const svg = $('#legend');
  const row = (y, cls, label, marker, dash) => {
    el('path', { d: `M10,${y} H70`, stroke: cls === 'v' ? '#ff5c5c' : '#7d869e', 'stroke-width': 1.5, fill: 'none', 'stroke-dasharray': dash, 'marker-end': `url(#${marker}-${cls})` }, svg);
    el('text', { x: 82, y: y + 4, fill: '#8b93a7', 'font-size': 11 }, svg, label);
  };
  row(12, 'n', 'depends on', 'open');
  row(32, 'n', 'inherits', 'tri');
  row(52, 'n', 'implements', 'tri', '6 4');
  row(72, 'v', 'breaks the dependency rule', 'open');
  const grad = el('linearGradient', { id: 'grades' }, $('#defs'));
  for (let g = 1; g <= 10; g += 3) el('stop', { offset: `${((g - 1) / 9) * 100}%`, 'stop-color': `hsl(${gradeHue(g)}, 52%, 38%)` }, grad);
  el('rect', { x: 10, y: 88, width: 60, height: 12, rx: 3, fill: 'url(#grades)' }, svg);
  el('text', { x: 82, y: 98, fill: '#8b93a7', 'font-size': 11 }, svg, 'CRAP + mutation, bad → good');
  el('circle', { cx: 16, cy: 118, r: 4.5, fill: dotColor(8) }, svg);
  el('circle', { cx: 28, cy: 118, r: 4.5, fill: dotColor(3) }, svg);
  el('text', { x: 82, y: 122, fill: '#8b93a7', 'font-size': 11 }, svg, 'C and M dots (hollow = unknown)');
  el('text', { x: 10, y: 142, fill: '#8b93a7', 'font-size': 11, 'font-family': MONO }, svg, 'L0');
  el('text', { x: 82, y: 142, fill: '#8b93a7', 'font-size': 11 }, svg, 'level (0 = innermost, at bottom)');
}

// ---------------------------------------------------------------- type card

async function openCard(typeId) {
  state.card = typeId;
  await refreshCard();
}

function closeCard() {
  state.card = null;
  $('#card').hidden = true;
}

async function refreshCard() {
  const card = await api.get('/api/type', { context: state.context, id: state.card }).catch(() => null);
  if (!card) return closeCard();
  renderCard(card);
}

function crapClass(value) {
  const t = state.meta?.thresholds ?? { crapGood: 5, crapBad: 30 };
  return value <= t.crapGood ? 'good' : value >= t.crapBad ? 'bad' : 'warn';
}

function renderCard(card) {
  const panel = $('#card');
  const stereo = card.stereotype !== 'class' ? `<span class="muted">«${html(card.stereotype)}»</span>` : '';
  const files = [...new Set(card.spans.map((s) => s.file))];
  const crap = card.crap
    ? `CRAP μ ${card.crap.mu.toFixed(1)} · max ${card.crap.max.toFixed(1)} · σ ${card.crap.sigma.toFixed(1)}${card.crap.stale ? ' <span class="stale" title="Code changed since the last coverage run">(stale)</span>' : ''}`
    : '<span class="muted">No CRAP data — run CRAP.</span>';
  const mutation = card.mutation
    ? card.mutation.sites === 0 ? 'Mutation: no mutation sites'
      : `Mutation ${Math.round((card.mutation.score ?? 0) * 100)}% · killed ${card.mutation.killed} · survived ${card.mutation.survived} · uncovered ${card.mutation.uncovered}${card.mutation.stale ? ' <span class="stale" title="Code changed since the last mutation run">(stale)</span>' : ''}`
    : '<span class="muted">No mutation data — right-click the box to mutate.</span>';

  const rows = card.members.map((m, i) => memberRow(m, i, card)).join('');
  const chips = (deps) => deps.length
    ? `<div class="chips">${deps.map((d) => `<span class="chip${d.violating ? ' violating' : ''}${d.isForeign ? ' foreign' : ''}" data-type="${d.isForeign ? '' : html(d.id)}" title="${html(d.kind)} ×${d.count}">${html(d.label)}</span>`).join('')}</div>`
    : '<div class="muted small">none</div>';

  panel.innerHTML = `<header>${stereo}<h3>${html(card.name)}</h3><span class="muted small">${html(card.visibility)}</span>
      <button class="close icon" title="Close (Esc)">×</button></header>
    <div class="body">
      <div class="card-meta"><a data-file="${html(card.spans[0]?.file ?? '')}" data-line="1">${html(card.namespace || '(global namespace)')}</a>
        · Level ${card.level ?? '—'} · ${files.map((f) => `<a data-file="${html(f)}" data-line="${card.spans.find((s) => s.file === f).startLine}">${html(f.split('/').pop())}</a>`).join(', ')}</div>
      <div class="card-summary">${crap}<br>${mutation}</div>
      <table class="members">
        <thead><tr><th></th><th></th><th class="group" colspan="3">— crap —</th><th class="group" colspan="3">— mutation —</th></tr>
        <tr><th></th><th class="name">member</th><th>CRAP</th><th>CC</th><th>Cov</th><th>killed</th><th>survived</th><th>uncovered</th></tr></thead>
        <tbody>${typeRow(card)}${rows}</tbody>
      </table>
      <h4 class="muted small">Depends on</h4>${chips(card.dependsOn)}
      <h4 class="muted small">Used by</h4>${chips(card.usedBy)}
    </div>`;
  panel.hidden = false;
  panel.querySelector('.close').onclick = closeCard;
  panel.querySelectorAll('[data-file]').forEach((a) => (a.onclick = () => openSource(a.dataset.file, +a.dataset.line)));
  panel.querySelectorAll('.chip[data-type]').forEach((c) => c.dataset.type && (c.onclick = () => openCard(c.dataset.type)));
  panel.querySelectorAll('tr.member').forEach((row) => {
    const member = card.members[+row.dataset.index];
    row.onclick = () => openSource(member.file, member.line, member.endLine);
  });
}

function typeRow(card) {
  const mu = card.crap ? `<td class="${crapClass(card.crap.mu)}">${card.crap.mu.toFixed(1)}μ</td>` : '<td></td>';
  const m = card.mutation;
  const mutation = m
    ? m.sites === 0 ? '<td class="none" colspan="3">no mutation sites</td>'
      : `<td>${m.killed}</td><td class="${m.survived ? 'bad' : 'good'}">${m.survived}</td><td class="${m.uncovered ? 'bad' : 'good'}">${m.uncovered}</td>`
    : '<td></td><td></td><td></td>';
  return `<tr class="type-row"><td></td><td class="name">${html(card.name)}</td>${mu}<td></td><td></td>${mutation}</tr>`;
}

function memberRow(member, index, card) {
  const crap = member.crap;
  const stale = (flag) => (flag ? ' stale' : '');
  const crapCells = crap && crap.crap !== null && crap.crap !== undefined
    ? `<td class="${crapClass(crap.crap)}${stale(member.crapStale)}">${crap.crap.toFixed(1)}</td><td class="${stale(member.crapStale)}">${crap.complexity}</td><td class="${crap.coverage >= 0.999 ? 'good' : crap.coverage < 0.5 ? 'bad' : 'warn'}${stale(member.crapStale)}">${Math.round(crap.coverage * 100)}%</td>`
    : `<td></td><td>${member.complexity ?? ''}</td><td></td>`;
  const m = member.mutation;
  let mutation = '<td></td><td></td><td></td>';
  if (m) {
    const sites = m.killed + m.timeout + m.survived + m.uncovered;
    mutation = sites === 0
      ? '<td class="none" colspan="3">---no mutation sites---</td>'
      : `<td class="${stale(member.mutationStale)}">${m.killed + m.timeout}</td><td class="${m.survived ? 'bad' : 'good'}${stale(member.mutationStale)}">${m.survived}</td><td class="${m.uncovered ? 'bad' : 'good'}${stale(member.mutationStale)}">${m.uncovered}</td>`;
  }
  const sign = VIS[member.visibility] ?? '+';
  return `<tr class="member" data-index="${index}"><td class="muted">${sign}</td><td class="name" title="${html(member.id)}">${html(member.signature)}</td>${crapCells}${mutation}</tr>`;
}

// ---------------------------------------------------------------- source

const KEYWORDS = new Set(('abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach get goto if implicit in init int interface internal is lock long namespace new null object operator out override params partial private protected public readonly record ref required return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile when where while with yield file global not and or nameof value').split(' '));
const TOKENS = /(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|("""[\s\S]*?"""|@"(?:[^"]|"")*"|\$?@?"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)+')|(\b\d[\d_]*(?:\.\d+)?[fFdDmMuUlL]*\b)|(\b[A-Za-z_][A-Za-z0-9_]*\b)/g;

function highlight(text) {
  let out = '';
  let last = 0;
  for (const match of text.matchAll(TOKENS)) {
    out += html(text.slice(last, match.index));
    const [token, comment, string, number, word] = match;
    const cls = comment ? 'c' : string ? 's' : number ? 'n' : KEYWORDS.has(word) ? 'k' : /^[A-Z]/.test(word) ? 't' : null;
    out += cls ? token.split('\n').map((part) => `<span class="tok-${cls}">${html(part)}</span>`).join('\n') : html(token);
    last = match.index + token.length;
  }
  return out + html(text.slice(last));
}

async function openSource(file, line = 1, endLine = line) {
  if (!file) return;
  let source;
  try { source = await api.get('/api/source', { file }); } catch { return toast(`Cannot open ${file}.`, 'error'); }
  const panel = $('#source');
  const lines = highlight(source.text).split('\n');
  panel.innerHTML = `<header><h3 class="mono">${html(source.file)}</h3><button id="open-editor">Open in editor</button><button class="close icon" title="Close (Esc)">×</button></header>
    <div class="body">${lines.map((l, i) => `<div class="line${i + 1 >= line && i + 1 <= endLine ? ' hl' : ''}" data-n="${i + 1}"><span class="n">${i + 1}</span><span>${l || ' '}</span></div>`).join('')}</div>`;
  panel.hidden = false;
  panel.querySelector('.close').onclick = () => (panel.hidden = true);
  $('#open-editor').onclick = async () => {
    const result = await api.post('/api/open', { file: source.file, line });
    if (!result.ok) toast(result.message, 'error');
  };
  const target = panel.querySelector(`.line[data-n="${line}"]`);
  target?.scrollIntoView({ block: line > 1 ? 'center' : 'start' });
}

// ---------------------------------------------------------------- menus and tips

function showMenu(items, x, y) {
  const menu = $('#menu');
  menu.replaceChildren();
  for (const item of items.filter(Boolean)) {
    if (item === '-') { menu.appendChild(document.createElement('hr')); continue; }
    const entry = document.createElement('div');
    entry.textContent = item.label;
    if (item.disabled) entry.className = 'disabled';
    entry.onclick = () => { hideMenu(); item.run(); };
    menu.appendChild(entry);
  }
  menu.hidden = false;
  const rect = menu.getBoundingClientRect();
  menu.style.left = `${Math.min(x, innerWidth - rect.width - 8)}px`;
  menu.style.top = `${Math.min(y, innerHeight - rect.height - 8)}px`;
}

function hideMenu() { $('#menu').hidden = true; }

function nodeMenu(node) {
  const metrics = node.kind !== 'foreign';
  const proposal = state.view.context.isProposal;
  return [
    node.typeId && { label: 'Open card', run: () => openCard(node.typeId) },
    node.drill && { label: 'Open', run: () => navigate(state.context, node.drill) },
    metrics && '-',
    metrics && { label: 'Refresh CRAP', run: () => action('refresh-crap', { node: node.id }) },
    metrics && { label: 'Refresh mutation', run: () => action('refresh-mutate', { node: node.id }) },
    metrics && { label: 'Refresh all mutation', run: () => action('refresh-mutate-all', { node: node.id }) },
    metrics && { label: proposal ? 'Omit from proposal' : 'Omit from diagram', disabled: !node.target, run: () => action('omit', { node: node.id }) },
    '-',
    { label: 'Ask the agent about this…', run: () => askAbout(node) },
  ];
}

function askAbout(node) {
  select(node.id);
  const ask = $('#ask');
  ask.focus();
  ask.placeholder = `About ${node.label}: …`;
}

function showTip(lines, x, y) {
  const tip = $('#tip');
  tip.innerHTML = lines.join('\n');
  tip.hidden = false;
  const rect = tip.getBoundingClientRect();
  tip.style.left = `${Math.min(x + 14, innerWidth - rect.width - 8)}px`;
  tip.style.top = `${Math.min(y + 14, innerHeight - rect.height - 8)}px`;
}

function hideTip() { $('#tip').hidden = true; }

function pairLines(pairs, heading) {
  const arrow = { inheritance: '─▷', implements: '┄▷', association: '──', dependency: '→' };
  const lines = [`<span class="h">${html(heading)}</span>`];
  for (const p of pairs.slice(0, 40)) {
    const text = `${html(p.from)} ${arrow[p.kind] ?? '→'} ${html(p.to)}${p.count > 1 ? ` ×${p.count}` : ''}`;
    lines.push(p.violating ? `<span class="v">${text}</span>` : text);
  }
  if (pairs.length > 40) lines.push(`<span class="h">… ${pairs.length - 40} more</span>`);
  return lines;
}

// ---------------------------------------------------------------- events

function nodeFrom(target) {
  const g = target.closest?.('.node');
  return g ? state.layout?.byId.get(g.dataset.id) : null;
}

function wireCanvas() {
  const svg = $('#canvas');
  let drag = null;
  svg.addEventListener('pointerdown', (e) => {
    if (e.button !== 0 || e.target.closest('.node, .tri, .edge')) return;
    drag = { x: e.clientX, y: e.clientY, moved: false };
    svg.setPointerCapture(e.pointerId);
  });
  svg.addEventListener('pointermove', (e) => {
    if (drag) {
      const dx = e.clientX - drag.x;
      const dy = e.clientY - drag.y;
      if (Math.abs(dx) + Math.abs(dy) > 2) { drag.moved = true; svg.classList.add('panning'); }
      drag.x = e.clientX;
      drag.y = e.clientY;
      if (drag.moved) panBy(dx, dy);
      return;
    }
    const edge = e.target.closest('.edge');
    const tri = e.target.closest('.tri');
    if (edge) {
      const data = state.layout.edges.find((x) => x.id === edge.dataset.edge);
      const from = state.layout.byId.get(data.from)?.label;
      const to = state.layout.byId.get(data.to)?.label;
      showTip(pairLines(data.pairs, `${from} → ${to}`), e.clientX, e.clientY);
    } else if (tri) {
      showTip(pairLines(tri.pairs, tri.direction === 'in' ? 'Incoming' : 'Outgoing'), e.clientX, e.clientY);
    } else hideTip();
  });
  svg.addEventListener('pointerup', (e) => {
    if (drag && !drag.moved && !e.target.closest('.node')) select(null);
    drag = null;
    svg.classList.remove('panning');
  });
  svg.addEventListener('pointerleave', hideTip);
  svg.addEventListener('click', (e) => {
    const node = nodeFrom(e.target);
    if (node) select(node.id);
  });
  svg.addEventListener('dblclick', (e) => {
    const node = nodeFrom(e.target);
    if (!node) return;
    if (node.typeId && node.kind === 'type') openCard(node.typeId);
    else if (node.drill) navigate(state.context, node.drill);
    else if (node.typeId) openCard(node.typeId);
  });
  svg.addEventListener('contextmenu', (e) => {
    const node = nodeFrom(e.target);
    if (!node) return;
    e.preventDefault();
    select(node.id);
    showMenu(nodeMenu(node), e.clientX, e.clientY);
  });
  svg.addEventListener('wheel', (e) => {
    e.preventDefault();
    if (e.ctrlKey || e.metaKey) zoomAt(Math.exp(-e.deltaY * 0.0022), e.clientX, e.clientY);
    else if (e.shiftKey) panBy(-(e.deltaY || e.deltaX), 0);
    else panBy(-e.deltaX, -e.deltaY);
  }, { passive: false });
}

function wireKeys() {
  document.addEventListener('keydown', (e) => {
    const typing = e.target.matches('textarea, input');
    if (typing) {
      if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && e.target.id === 'ask') { e.preventDefault(); send(); }
      if (e.key === 'Escape') e.target.blur();
      return;
    }
    if (e.ctrlKey || e.metaKey) {
      if (e.key === '=' || e.key === '+') { e.preventDefault(); zoomAt(1.1); }
      else if (e.key === '-') { e.preventDefault(); zoomAt(1 / 1.1); }
      else if (e.key === '0') { e.preventDefault(); state.zoom = 1; applyCamera(); saveCamera(); }
      return;
    }
    const pan = { ArrowLeft: [60, 0], ArrowRight: [-60, 0], ArrowUp: [0, 60], ArrowDown: [0, -60] }[e.key];
    if (pan) { e.preventDefault(); panBy(...pan); return; }
    if (e.key === 'Escape') {
      if (!$('#menu').hidden) hideMenu();
      else if (!$('#source').hidden) $('#source').hidden = true;
      else if (!$('#card').hidden) closeCard();
      else up();
    } else if (e.key === 'r' || e.key === 'R') refresh({ keepCamera: true });
    else if (e.key === 'f' || e.key === 'F') { fit(); applyCamera(); saveCamera(); }
  });
  document.addEventListener('click', (e) => { if (!e.target.closest('#menu')) hideMenu(); });
}

async function send() {
  const ask = $('#ask');
  const text = ask.value.trim();
  if (!text) return;
  const result = await action('message', { text, selection: selectionInfo() });
  if (result.ok) ask.value = '';
}

function wireInspector() {
  $('#up').onclick = up;
  $('#send').onclick = send;
  $('#regen').onclick = () => action('regen');
  $('#run-crap').onclick = () => action('refresh-crap');
  $('#run-mutate').onclick = () => action('refresh-mutate');
  $('#new-proposal').onclick = async () => {
    const result = await action('new-proposal');
    if (result.ok && result.data?.proposalId) navigate(result.data.proposalId, null);
  };
  for (const [id, key] of [['#arrows', 'arrows'], ['#detail', 'detail']]) {
    $(id).addEventListener('click', (e) => {
      const value = e.target.dataset?.v;
      if (!value || value === state[key]) return;
      state[key] = value;
      localStorage.setItem(`mg.${key}`, value);
      refresh({ keepCamera: key === 'arrows' });
    });
  }
  window.addEventListener('popstate', () => { readHash(); refresh(); });
  window.addEventListener('resize', debounce(() => applyCamera(), 100));
}

function connectEvents() {
  const source = new EventSource('/api/events');
  let lost = false;
  source.onopen = () => { if (lost) refresh({ keepCamera: true }); lost = false; };
  source.onerror = () => { lost = true; $('#status').textContent = 'Reconnecting…'; };
  source.onmessage = (e) => {
    const { type, payload } = JSON.parse(e.data);
    if (type === 'model' || type === 'metrics' || type === 'policy') scheduleRefresh();
    else if (type === 'job' && state.meta) { state.meta.job = payload; renderJob(); renderStatus(); }
    else if (type === 'mail') onMail(payload);
    else if (type === 'agent') refreshMeta();
  };
}

function onMail(message) {
  const data = message.data ?? {};
  if (message.op === 'display' && data.context) {
    navigate(data.context, data.focus ?? null);
    toast(`Showing ${data.context === 'real' ? 'the real diagram' : data.context}.`, 'agent');
  } else if (message.op === 'notify' && data.text) toast(data.text, 'agent');
  else if (message.op === 'reload') refresh({ keepCamera: true });
}

// ---------------------------------------------------------------- start

markers();
renderLegend();
readHash();
wireCanvas();
wireKeys();
wireInspector();
connectEvents();
refresh();
setInterval(refreshMeta, 5000);
