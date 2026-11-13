import { t } from '../i18n.js';
import { h } from './dom.js';

// Analiz raporu görünümü (biçim: src/core/analyze.js). Rapordaki tüm dizgeler çözülen koddan gelir ve güvenilmezdir:
// yalnızca textContent ile basılır. Boş bölümler gösterilmez.

const MAX_ROWS = 80;
const kb = (n) => `${((Number(n) || 0) / 1024).toFixed(1)} KB`;
const arr = (v) => (Array.isArray(v) ? v : []);
const str = (v) => (v == null ? '' : String(v));
const section = (title, ...body) => h('section', { class: 'rep-section' }, h('h3', {}, title), ...body);
const LEVELS = ['none', 'low', 'medium', 'high', 'critical'];

function table(headers, rows, classes = []) {
  return h('div', { class: 'table-wrap' }, h('table', { class: 'rep-table' },
    h('thead', {}, h('tr', {}, ...headers.map((x) => h('th', { scope: 'col' }, x)))),
    h('tbody', {}, ...rows.slice(0, MAX_ROWS).map((r) => h('tr', {}, ...r.map((c, i) => h('td', { class: classes[i] ?? '' }, str(c))))))),
  rows.length > MAX_ROWS ? h('p', { class: 'rep-muted small' }, t('report.more', { n: rows.length - MAX_ROWS })) : null);
}

function protection(p) {
  if (!p || typeof p !== 'object') return null;
  return h('section', { class: 'rep-section rep-protect', role: 'note' },
    h('h3', {}, t('report.protection')),
    h('p', { class: 'protect-title' }, [str(p.product), str(p.version)].filter(Boolean).join(' ')),
    h('p', {}, t('report.protection.body')),
    arr(p.notes).length ? h('ul', { class: 'rep-list' }, ...arr(p.notes).map((n) => h('li', {}, str(n)))) : null);
}

function threat(th) {
  if (!th || typeof th !== 'object') return null;
  const classes = arr(th.classes);
  const level = LEVELS.includes(th.level) ? th.level : 'none';
  if (!classes.length && !(Number(th.score) > 0)) return null;
  return section(t('report.threat'),
    h('div', { class: `risk risk-${level}` }, h('b', {}, str(Number(th.score) || 0)), h('span', {}, t(`report.risk.${level}`))),
    classes.length ? table([t('report.threat.class'), t('report.threat.evidence')], classes.map((c) => [str(c.title || c.id), arr(c.evidence).map(str).join(' · ')]), ['', 'rep-muted mono']) : null);
}

const KINDS = ['obfuscator', 'protector', 'packer', 'malware', 'technique'];
function detection(list) {
  const items = arr(list).filter((x) => x && typeof x === 'object');
  if (!items.length) return null;
  return section(t('report.detect'), h('div', { class: 'detect' }, ...items.map((f) => h('div', { class: 'detect-row' },
    h('span', { class: 'detect-name' }, [str(f.name), str(f.version)].filter(Boolean).join(' '),
      KINDS.includes(f.kind) ? h('small', {}, t(`report.detect.kind.${f.kind}`)) : null,
      h('small', {}, `${Math.round((Number(f.confidence) || 0) * 100)}%`),
      f.decodable === false ? h('small', { class: 'detect-only' }, t('report.detect.reportOnly')) : null),
    h('span', { class: 'detect-ev' }, arr(f.evidence).map(str).join(' · '))))));
}

function behaviour(b) {
  if (!b || typeof b !== 'object') return null;
  const groups = [
    ['network', (x) => [x.api, x.target]],
    ['commands', (x) => [x.api, x.command]],
    ['files', (x) => [x.api, x.path]],
    ['registry', (x) => [x.api, x.target]],
    ['other', (x) => [x.api, arr(x.args).map((a) => (typeof a === 'string' ? a : JSON.stringify(a))).join(', ')]],
  ].filter(([k]) => arr(b[k]).length);
  const loads = Number(b.loads) || 0;
  if (!groups.length && !loads) return null;
  return section(t('report.behaviour'),
    h('p', { class: 'rep-note' }, t('report.behaviour.note')),
    loads ? h('dl', { class: 'kv' }, h('dt', {}, t('report.behaviour.loads')), h('dd', {}, str(loads))) : null,
    ...groups.map(([k, row]) => h('div', { class: 'ioc-group' },
      h('h4', {}, `${t(`report.behaviour.${k}`)} (${arr(b[k]).length})`),
      table([t('report.behaviour.api'), t('report.behaviour.target')], arr(b[k]).map(row), ['mono', 'mono']))));
}

// Assembly.Load(byte[]) ile yüklenmek istenen derlemeler: indirilebilir ya da yeni sekmede çözülebilir
function layers(list, onLayer) {
  const items = arr(list).filter((x) => x && typeof x === 'object');
  if (!items.length) return null;
  const rows = items.map((x) => h('tr', {},
    h('td', {}, `#${str(x.index)}`),
    h('td', {}, kb(x.size)),
    h('td', { class: 'mono' }, x.net ? `${str(x.name)} · ${str(x.types)} / ${str(x.methods)}` : t('report.layers.notNet')),
    h('td', {}, onLayer ? h('span', { class: 'rep-actions' },
      h('button', { type: 'button', class: 'tiny', onclick: () => onLayer(x.index, 'download') }, t('report.layers.download')),
      x.net ? h('button', { type: 'button', class: 'tiny', onclick: () => onLayer(x.index, 'open') }, t('report.layers.open')) : null) : '')));
  return section(t('report.layers'),
    h('p', { class: 'rep-note' }, t('report.layers.note')),
    h('div', { class: 'table-wrap' }, h('table', { class: 'rep-table' },
      h('thead', {}, h('tr', {}, ...['#', t('report.layers.size'), t('report.layers.what'), ''].map((x) => h('th', { scope: 'col' }, x)))),
      h('tbody', {}, ...rows))));
}

function info(i) {
  if (!i || typeof i !== 'object') return null;
  const dl = h('dl', { class: 'kv' });
  const row = (label, value) => dl.append(h('dt', {}, label), h('dd', {}, value));
  row(t('report.info.name'), str(i.assembly || i.name));
  row(t('report.metric.kind'), str(i.kind));
  row(t('report.metric.version'), str(i.framework || i.runtime));
  row(t('report.info.machine'), `${str(i.machine)}${i.ilOnly ? ' · IL only' : ' · mixed-mode'}`);
  row(t('report.info.types'), `${str(i.types)} / ${str(i.methods)}`);
  return section(t('report.info'), dl);
}

function iocs(data) {
  if (!data || typeof data !== 'object') return null;
  const groups = ['urls', 'domains', 'ips', 'emails', 'discordWebhooks', 'telegram'].filter((k) => arr(data[k]).length);
  if (!groups.length) return null;
  return section(t('report.iocs'), ...groups.map((k) => h('div', { class: 'ioc-group' },
    h('h4', {}, `${t(`report.ioc.${k}`)} (${data[k].length})`),
    h('ul', { class: 'ioc-list' }, ...data[k].slice(0, MAX_ROWS).map((v) => h('li', {}, str(v)))))));
}

function metrics(m, extra) {
  const dl = h('dl', { class: 'kv' });
  const row = (label, value) => dl.append(h('dt', {}, label), h('dd', {}, value));
  if (m) {
    row(t('report.metric.size'), `${kb(m.inBytes)} → ${kb(m.outBytes)}`);
    for (const [k, label] of [['strings', 'strings'], ['constants', 'constants'], ['flowMethods', 'flow'], ['proxyCalls', 'proxies'], ['renamed', 'renamed'], ['resources', 'resources']])
      if (Number(m[k]) > 0) row(t(`report.metric.${label}`), str(m[k]));
    row(t('report.metric.rounds'), str(m.rounds ?? 0));
    row(t('report.metric.rollbacks'), str(m.rollbacks ?? 0));
  }
  if (extra.verified != null) row(t('report.metric.verified'), t(extra.verified ? 'report.verified.yes' : 'report.verified.no'));
  if (extra.ms != null) row(t('report.metric.time'), `${(extra.ms / 1000).toFixed(2)} s`);
  return section(t('report.metrics'), dl);
}

function passTimes(events) {
  const by = new Map();
  for (const e of arr(events)) {
    const p = by.get(e.id) ?? { id: e.id, ms: 0, ok: 0, fail: 0 };
    p.ms += Number(e.ms) || 0;
    if (e.state === 'ok') p.ok++;
    if (e.state === 'fail') p.fail++;
    by.set(e.id, p);
  }
  const list = [...by.values()].sort((a, b) => b.ms - a.ms);
  if (!list.length) return null;
  const max = Math.max(1, ...list.map((p) => p.ms));
  return section(t('report.passes'), h('div', { class: 'bars' }, ...list.map((p) => {
    const bar = h('i');
    bar.style.width = `${Math.max(2, (p.ms / max) * 100)}%`;
    return h('div', { class: 'bar' },
      h('span', {}, p.id),
      h('span', {}, bar),
      h('span', {}, `${p.ms} ms`),
      h('span', { class: p.fail ? 'bar-fail' : 'bar-ok' }, p.fail ? t('report.pass.rolled', { n: p.fail }) : p.ok ? t('report.pass.changed', { n: p.ok }) : '–'));
  })));
}

/**
 * @param {HTMLElement} el
 * @param {object|null} report
 * @param {{ verified?: boolean, ms?: number, events?: object[], onExport?: () => void, onLayer?: (i: number, action: 'download'|'open') => void }} [extra]
 */
export function renderReport(el, report, extra = {}) {
  el.textContent = '';
  if (!report) {
    el.append(h('p', { class: 'placeholder' }, t('report.empty')));
    return;
  }
  const rep = h('div', { class: 'rep' });
  if (report.summary) rep.append(h('p', { class: 'rep-summary' }, str(report.summary)));
  for (const node of [
    protection(report.protection),
    threat(report.threat),
    detection(report.detection),
    info(report.info),
    behaviour(report.behaviour),
    layers(report.layers, extra.onLayer),
    iocs(report.iocs),
    metrics(report.metrics, extra),
    passTimes(extra.events),
  ]) if (node) rep.append(node);
  if (extra.onExport) rep.append(h('button', { type: 'button', class: 'ghost small rep-export', onclick: extra.onExport }, t('action.export')));
  el.append(rep);
}
