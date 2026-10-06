// Deterministic animation helpers: every frame is a pure function of time, set by seek(t). No CSS animations.
const clamp = (x, a = 0, b = 1) => (x < a ? a : x > b ? b : x);
const lerp = (a, b, t) => a + (b - a) * t;
const E = {
  lin: t => clamp(t),
  out: t => 1 - Math.pow(1 - clamp(t), 3),
  out5: t => 1 - Math.pow(1 - clamp(t), 5),
  in: t => Math.pow(clamp(t), 3),
  io: t => { t = clamp(t); return t < .5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2; },
  back: t => { t = clamp(t); const c1 = 1.5, c3 = c1 + 1; return 1 + c3 * Math.pow(t - 1, 3) + c1 * Math.pow(t - 1, 2); },
  spring: t => { t = clamp(t); return 1 - Math.exp(-6 * t) * Math.cos(9 * t); },
};
const P = (t, a, len) => clamp((t - a) / len);
// in at a, out at b (both over len); 0..1..0
const win = (t, a, b, len = .5) => Math.min(E.out(P(t, a, len)), 1 - E.in(P(t, b, len)));

function show(el, op, tx = 0, ty = 0, sc = 1, extra = '') {
  el.style.opacity = op;
  el.style.visibility = op <= .002 ? 'hidden' : 'visible';
  el.style.transform = `translate(${tx}px,${ty}px) scale(${sc}) ${extra}`;
}
const rise = (el, t, a, len = .7, dist = 34) => { const p = E.out(P(t, a, len)); show(el, p, 0, (1 - p) * dist); return p; };
const pop = (el, t, a, len = .55) => { const p = P(t, a, len); show(el, clamp(p * 2), 0, 0, lerp(.86, 1, E.back(p))); return p; };
const slideX = (el, t, a, len = .7, dist = 60) => { const p = E.out(P(t, a, len)); show(el, p, (1 - p) * dist, 0); return p; };
const hideAfter = (el, t, b, len = .4) => { if (t > b) { const k = 1 - E.in(P(t, b, len)); el.style.opacity = Math.min(+el.style.opacity, k); if (k <= 0) el.style.visibility = 'hidden'; } };

function html(s) { const d = document.createElement('div'); d.innerHTML = s.trim(); return d.firstElementChild; }
function refs(root) { const r = {}; root.querySelectorAll('[data-r]').forEach(e => (r[e.dataset.r] = e)); return r; }
function rng(seed) { return () => { seed |= 0; seed = seed + 0x6D2B79F5 | 0; let t = Math.imul(seed ^ seed >>> 15, 1 | seed); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }
const typed = (s, t, a, cps = 18) => s.slice(0, Math.max(0, Math.floor((t - a) * cps)));
const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const ic = (name, extra = '') => `<i class="ic" style="--i:url(/assets/icons/${name}.svg);${extra}"></i>`;
const blink = t => (Math.floor(t * 1.9) % 2 === 0 ? 1 : 0);
const fmt = n => n.toLocaleString('en-US');
// "[[2|green]] [[b1|bold red]]" -> spans with ANSI color classes
function ansi(s) {
  return esc(s).replace(/\[\[(b?)(\d+|i|g)\|(.*?)\]\]/g, (m, b, c, txt) => {
    const cls = c === 'i' ? 'inv' : c === 'g' ? 'gbg' : 'a' + c;
    return `<span class="${cls}${b ? ' ab' : ''}">${txt}</span>`;
  });
}
const hex = c => [parseInt(c.slice(1, 3), 16), parseInt(c.slice(3, 5), 16), parseInt(c.slice(5, 7), 16)];
const mix = (a, b, t) => { const x = hex(a), y = hex(b); return `rgb(${x.map((v, i) => Math.round(lerp(v, y[i], t))).join(',')})`; };

// ---------- shared pieces ----------
function head(num, kick, title, lead) {
  return `<div class="head">
    <div class="kick" data-r="kick"><span class="num">${num}</span><span class="bar"></span><span>${kick}</span></div>
    <h1 data-r="h1" style="margin-top:26px">${title}</h1>
    ${lead ? `<p class="lead" data-r="lead" style="margin-top:24px">${lead}</p>` : ''}
  </div>`;
}
function animHead(r, t, a = .15) { rise(r.kick, t, a); rise(r.h1, t, a + .12, .8, 40); if (r.lead) rise(r.lead, t, a + .3); }
const bullet = (txt, key) => `<div class="bul" data-r="${key}"><span class="ck">${ic('check')}</span><span>${txt}</span></div>`;

// ---------- cursor ----------
const CURSOR_SVG = `<svg viewBox="0 0 24 24" width="34" height="34"><path d="M5 2.5v17.2l4.6-4.3 2.9 6.6 3-1.3-2.9-6.5h6.3z" fill="#fff" stroke="#111" stroke-width="1.3" stroke-linejoin="round"/></svg>`;
// path: [[t, x, y], ...]; clicks: [t, ...]
function cursorAt(path, t) {
  if (t <= path[0][0]) return [path[0][1], path[0][2]];
  for (let i = 1; i < path.length; i++) {
    if (t <= path[i][0]) {
      const [t0, x0, y0] = path[i - 1], [t1, x1, y1] = path[i];
      const k = E.io((t - t0) / (t1 - t0));
      return [lerp(x0, x1, k), lerp(y0, y1, k)];
    }
  }
  const l = path[path.length - 1];
  return [l[1], l[2]];
}
const CUR = { want: null };
function cursor(path, clicks, t, op = 1) { CUR.want = { path, clicks, t, op }; }

// ---------- registry ----------
const SCENES = {};
function scene(id, def) { SCENES[id] = def; }
let TL = null, LIVE = [];
const L = {}; // localized strings of the current language

function setup(timing, strings) {
  Object.assign(L, strings);
  TL = timing;
  const stage = document.getElementById('stage');
  for (const s of TL.scenes) {
    const def = SCENES[s.id];
    const el = document.createElement('div');
    el.className = 'scene';
    el.style.display = 'none';
    stage.appendChild(el);
    const st = def.build(el, L[s.id] || {}, s) || {};
    // shrink headlines that would wrap onto more lines than written (longer translations)
    el.style.display = 'block';
    el.querySelectorAll('h1').forEach(h => {
      const lines = (h.innerHTML.match(/<br>/g) || []).length + 1;
      let fs = parseFloat(getComputedStyle(h).fontSize);
      while (h.getBoundingClientRect().height > lines * fs * 1.04 * 1.2 && fs > 30) { fs -= 2; h.style.fontSize = fs + 'px'; }
    });
    el.style.display = 'none';
    LIVE.push({ ...s, def, el, st });
  }
  const c = html(`<div id="cursor">${CURSOR_SVG}</div>`); stage.appendChild(c);
  stage.appendChild(html('<div id="ripple"></div>'));
  stage.appendChild(html('<div id="vignette"></div>'));
  stage.appendChild(html('<div id="fade"></div>'));
}

const XF = .55; // cross-fade between scenes
function seek(T) {
  CUR.want = null;
  bgRender(T);
  for (const s of LIVE) {
    const t = T - s.start;
    const vis = t > -XF && t < s.dur + XF;
    if (!vis) { s.el.style.display = 'none'; continue; }
    const inP = s.def.noFadeIn ? 1 : E.out(P(t, -XF, XF * 1.6));
    const outP = s.def.noFadeOut ? 0 : E.in(P(t, s.dur - XF * .6, XF * 1.2));
    const op = Math.min(inP, 1 - outP);
    s.el.style.display = op > 0 ? 'block' : 'none';
    s.el.style.opacity = op;
    s.el.style.transform = `scale(${lerp(1.025, 1, inP) * lerp(1, .985, outP)})`;
    s.def.render(s.st, t, s.dur, s.cues, s.ends, T);
  }
  // cursor
  const cEl = document.getElementById('cursor'), rp = document.getElementById('ripple');
  if (CUR.want) {
    const { path, clicks, t, op } = CUR.want;
    const [x, y] = cursorAt(path, t);
    let press = 0, rip = null;
    for (const ct of clicks) { if (t >= ct - .08 && t < ct + .12) press = 1; if (t >= ct && t < ct + .5) rip = (t - ct) / .5; }
    const a = Math.min(op, E.out(P(t, path[0][0] - .4, .4)));
    show(cEl, a, x - 6, y - 3, press ? .86 : 1);
    if (rip !== null) { rp.style.left = x + 'px'; rp.style.top = y + 'px'; show(rp, (1 - rip) * a, 0, 0, .3 + rip * 1.1); }
    else rp.style.opacity = 0;
  } else { cEl.style.opacity = 0; rp.style.opacity = 0; }
  // final fade to black
  document.getElementById('fade').style.opacity = E.io(P(T, TL.total - 1.2, 1.2));
}

// ---------- background ----------
let BG = null;
function bgBuild() {
  const bg = document.getElementById('bg');
  bg.innerHTML = `<div class="grid"></div>
    <div class="blob" style="background:radial-gradient(circle, rgba(70,110,255,.30), transparent 62%)"></div>
    <div class="blob" style="background:radial-gradient(circle, rgba(150,95,255,.22), transparent 62%)"></div>
    <div class="blob" style="background:radial-gradient(circle, rgba(40,190,170,.12), transparent 62%)"></div>`;
  BG = { grid: bg.querySelector('.grid'), blobs: [...bg.querySelectorAll('.blob')] };
}
function bgRender(T) {
  const b = BG.blobs;
  b[0].style.transform = `translate(${-200 + 260 * Math.sin(T * .11)}px, ${-420 + 120 * Math.cos(T * .09)}px)`;
  b[1].style.transform = `translate(${1150 + 220 * Math.cos(T * .08)}px, ${200 + 160 * Math.sin(T * .12)}px)`;
  b[2].style.transform = `translate(${500 + 300 * Math.sin(T * .06 + 1)}px, ${600 + 100 * Math.sin(T * .1)}px)`;
  BG.grid.style.transform = `translate(${(T * 6) % 34}px, ${(T * 3) % 34}px)`;
}
