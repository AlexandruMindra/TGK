// Scenes 1-6: cold open, logo, sessions, split view, terminal, files.

/* ===================== 1. cold open ===================== */
scene('open', {
  noFadeIn: true,
  plan: (d, c) => ({ k: c[2] - .5, n: [c[0] + .05, c[0] + 1.25, c[1] + .1] }),
  build(el, S) {
    const kchip = (t) => `<div style="display:flex;gap:10px;align-items:center;padding:12px 18px;border-radius:12px;background:#171a22;border:1px solid #2c3140;font:500 22px JBM;color:#dfe4ee;box-shadow:0 18px 40px rgba(0,0,0,.5)">${ic('key', 'width:20px;height:20px;color:#e6c07b')}${t}</div>`;
    const items = [
      [140, 110, -8, kchip('id_rsa')], [1560, 150, 6, kchip('id_rsa_old')], [260, 820, 4, kchip('prod.pem')], [1440, 860, -5, kchip('key (2).ppk')],
      [820, 930, 3, kchip('bastion_NEW.pem')], [1660, 600, -4, kchip('deploy_key')],
      [90, 430, -7, `<div style="width:280px;height:200px;background:#f6d365;color:#4a3a06;padding:22px;font:600 25px/1.35 Inter;box-shadow:0 20px 40px rgba(0,0,0,.5)">${S.note}</div>`],
      [1590, 300, 7, `<div style="width:250px;height:170px;background:#f9a8c9;color:#4a0d27;padding:22px;font:600 24px/1.35 Inter;box-shadow:0 20px 40px rgba(0,0,0,.5)">${S.note2}</div>`],
      [1180, 640, 4, `<div style="width:420px;border-radius:10px;overflow:hidden;background:#fff;color:#222;font:500 16px Inter;box-shadow:0 20px 40px rgba(0,0,0,.5)">
        <div style="background:#1e7145;color:#fff;padding:9px 14px;font-weight:700">${S.sheet}</div>
        ${[['host', 'user', 'password'], ['10.0.3.47', 'root', 'hunter2'], ['db-prod', 'postgres', 'Summer24!'], ['bastion', 'admin', '??? ask Dan'], ['web-01', 'deploy', '(see Slack)']]
          .map((r, i) => `<div style="display:flex;border-top:1px solid #ddd;${i ? '' : 'font-weight:700;background:#eef3ef'}">${r.map(x => `<span style="flex:1;padding:7px 12px;border-right:1px solid #ddd">${x}</span>`).join('')}</div>`).join('')}</div>`],
      [520, 640, -2, `<div style="max-width:460px;padding:18px 22px;border-radius:20px 20px 20px 4px;background:#2b3245;color:#e8ecf5;font:500 24px/1.35 Inter;box-shadow:0 20px 40px rgba(0,0,0,.5)">${S.chat}</div>`],
      [1240, 70, 2, `<div style="padding:16px 20px;border-radius:12px;background:#0f1015;border:1px solid #2c3140;font:500 19px/1.5 JBM;color:#dfe4ee;box-shadow:0 20px 40px rgba(0,0,0,.5)">$ ssh root@10.0.3.47<br><span style="color:#ff7e84">Permission denied (publickey).</span></div>`],
      [560, 90, -4, `<div style="padding:16px 20px;border-radius:12px;background:#1b1e27;border:1px solid #2c3140;font:500 18px/1.5 JBM;color:#c9cfdb;box-shadow:0 20px 40px rgba(0,0,0,.5)">${ic('file', 'width:18px;height:18px;vertical-align:-3px')} hosts.txt<br><span style="color:#8b93a3">10.0.3.47 # prod?<br>10.0.3.52 # old, don't use</span></div>`],
    ];
    el.innerHTML = items.map((it, i) => `<div class="abs" data-r="it${i}" style="left:${it[0]}px;top:${it[1]}px">${it[3]}</div>`).join('') +
      S.c.map((x, i) => `<div class="abs" data-r="c${i}" style="left:${300 + i * 480}px;width:480px;top:330px;text-align:center">
        <div data-r="n${i}" style="font:800 190px/1 Inter;letter-spacing:-.04em;color:#fff;text-shadow:0 10px 60px rgba(0,0,0,.9)">${x[0]}</div>
        <div style="font:600 30px Inter;color:#c4ccdb;margin-top:8px;text-shadow:0 4px 20px #000">${x[1]}</div></div>`).join('') +
      `<div class="abs" data-r="line" style="left:0;right:0;top:539px;height:2px;background:linear-gradient(90deg,transparent,#7fb0ff,#b48bff,transparent)"></div>
       <div class="abs" data-r="better" style="left:0;right:0;top:470px;text-align:center;font:760 104px Inter;letter-spacing:-.02em">${S.better.replace(/(\S+)(\s\S+\.)$/, '<em>$1</em>$2')}</div>`;
    const r = refs(el);
    r.items = items;
    return r;
  },
  render(r, t, d, c, e) {
    const p = this.plan(d, c);
    r.items.forEach((it, i) => {
      const el = r['it' + i];
      const a = i < 6 ? p.n[0] + .15 + i * .32 : p.n[2] - .3 + (i - 6) * .42;
      const k = E.in(P(t, p.k, .5));
      const ap = E.back(P(t, a, .5));
      const rot = it[2] + 2 * Math.sin(t * .9 + i);
      const dx = (960 - it[0] - 120) * k, dy = (540 - it[1] - 60) * k + 6 * Math.sin(t * 1.1 + i * 2);
      el.style.opacity = clamp(P(t, a, .25)) * (1 - k) * .92;
      el.style.transform = `translate(${dx}px,${dy}px) rotate(${rot * (1 - k)}deg) scale(${lerp(.6, 1, ap) * lerp(1, .15, k)})`;
    });
    S_OPEN_N.forEach((n, i) => {
      const a = p.n[i], el = r['c' + i];
      const k = E.in(P(t, p.k - .1, .45));
      const q = E.out(P(t, a, .6));
      show(el, q * (1 - k), 0, (1 - q) * 40, lerp(1, .5, k));
      r['n' + i].textContent = Math.round(n * E.out(P(t, a, .9)));
    });
    const lp = P(t, p.k + .35, .5);
    r.line.style.opacity = win(t, p.k + .35, p.k + 1.1, .3);
    r.line.style.transform = `scaleX(${E.out(lp)})`;
    const b = E.out(P(t, c[2] - .05, 1.1));
    show(r.better, b, 0, 0, lerp(.94, 1, b));
    r.better.style.letterSpacing = `${lerp(.12, -.02, b)}em`;
  },
  sfx(d, c) { const p = this.plan(d, c); return [{ t: p.k, type: 'riser', dur: .9 }, { t: p.k + .5, type: 'impact' }, ...p.n.map(t => ({ t, type: 'tick' }))]; },
});
const S_OPEN_N = [40, 12, 3];

/* ===================== 2. logo ===================== */
scene('logo', {
  build(el, S) {
    el.innerHTML = `
      <div class="abs" data-r="ring" style="left:760px;top:230px;width:400px;height:400px;border-radius:50%;border:2px solid rgba(120,170,255,.6)"></div>
      <div class="abs" data-r="glow" style="left:460px;top:-70px;width:1000px;height:1000px;border-radius:50%;background:radial-gradient(circle,rgba(82,139,255,.35),transparent 60%)"></div>
      <div class="abs" data-r="icon" style="left:860px;top:330px;width:200px;height:200px;border-radius:46px;background:linear-gradient(135deg,#72adff,#3d74e3);
           box-shadow:0 30px 90px rgba(60,120,255,.5), inset 0 2px 0 rgba(255,255,255,.35)">
        <svg viewBox="0 0 100 100" width="200" height="200"><path data-r="chev" d="M28 31 L47 50 L28 69" fill="none" stroke="#fff" stroke-width="9" stroke-linecap="round" stroke-linejoin="round" stroke-dasharray="60"/>
        <path data-r="und" d="M55 69 H75" stroke="#fff" stroke-width="9" stroke-linecap="round"/></svg></div>
      <div class="abs" data-r="word" style="left:867px;top:322px;font:820 200px/1 Inter;letter-spacing:-.045em;color:#fff">TGK</div>
      <div class="abs" data-r="tag" style="left:0;right:0;top:600px;text-align:center;font:680 58px Inter;letter-spacing:-.02em">${S.tag}</div>
      <div class="abs" data-r="pills" style="left:0;right:0;top:712px;display:flex;justify-content:center;gap:16px">
        ${S.pills.map((x, i) => `<span class="chip" data-r="p${i}" style="font-size:22px;padding:11px 22px">${x}</span>`).join('')}</div>
      <div class="abs" data-r="os" style="left:0;right:0;top:800px;text-align:center;font:500 24px Inter;color:#8b93a3;letter-spacing:.12em">${S.os}</div>`;
    return refs(el);
  },
  render(r, t, d, c) {
    const ip = P(t, .1, .9);
    show(r.icon, clamp(ip * 3), 0, 0, lerp(.4, 1, E.back(ip)), `rotate(${lerp(-12, 0, E.out(ip))}deg)`);
    r.chev.style.strokeDashoffset = 60 * (1 - E.io(P(t, .45, .55)));
    r.und.style.opacity = t < 1 ? 0 : (t < c[0] + .3 ? blink(t - 1) : 1);
    const rp = P(t, .35, 1.3);
    show(r.ring, (1 - rp) * (rp > 0 ? 1 : 0), 0, 0, lerp(.4, 2.3, E.out(rp)));
    const m = E.io(P(t, c[0] - .1, .8));
    r.icon.style.left = lerp(860, 623, m) + 'px';
    r.icon.style.top = lerp(330, 300, m) + 'px';
    r.word.style.clipPath = `inset(0 ${100 - 100 * E.out(P(t, c[0] + .15, .8))}% 0 0)`;
    r.word.style.transform = `translateX(${lerp(-40, 0, E.out(P(t, c[0] + .15, .8)))}px)`;
    r.glow.style.opacity = .5 + .5 * E.out(P(t, c[0], 1)) + .08 * Math.sin(t * 2);
    rise(r.tag, t, c[1] - .1, .8);
    [0, 1, 2, 3].forEach(i => pop(r['p' + i], t, c[1] + 1 + i * .18));
    rise(r.os, t, c[1] + 2, .8, 20);
  },
  sfx(d, c) { return [{ t: .15, type: 'whoosh' }, { t: c[0] - .05, type: 'impact' }, ...[0, 1, 2, 3].map(i => ({ t: c[1] + 1 + i * .18, type: 'pop' }))]; },
});

/* ===================== 3. sessions & tabs ===================== */
const TERM = {
  'staging-web': ['[[b2|deploy@staging-web]]:[[b4|~]]$ docker compose ps', 'NAME            IMAGE             STATUS',
    'app-web-1       app:1.4.3-rc1     Up 2 hours ([[2|healthy]])', 'app-worker-1    app:1.4.3-rc1     Up 2 hours', 'app-redis-1     redis:7-alpine    Up 3 days', '[[b2|deploy@staging-web]]:[[b4|~]]$ '],
  homelab: ['[[b2|pi@homelab]]:[[b4|~ $]] uptime', ' 09:41:52 up 23 days,  4:12,  1 user,  load average: 0.21, 0.18, 0.12', '[[b2|pi@homelab]]:[[b4|~ $]] vcgencmd measure_temp',
    'temp=47.2\'C', '[[b2|pi@homelab]]:[[b4|~ $]] '],
  motd: ['Welcome to Ubuntu 24.04.1 LTS (GNU/Linux 6.8.0-45-generic x86_64)', '', '  System load:  0.08               Processes:             142',
    '  Usage of /:   37.2% of 77.35GB   Users logged in:       1', '  Memory usage: 41%                IPv4 address for eth0: 10.0.1.11', '',
    'Last login: Mon Oct  6 09:12:44 2026 from 10.0.0.5'],
  nginx: ['[[b2|●]] nginx.service - A high performance web server and a reverse proxy server', '     Loaded: loaded (/usr/lib/systemd/system/nginx.service; [[b2|enabled]])',
    '     Active: [[b2|active (running)]] since Mon 2026-10-06 08:01:13 UTC; 1h 11min ago', '   Main PID: 1123 (nginx)', '      Tasks: 5 (limit: 9440)', '     Memory: 7.9M (peak: 9.1M)',
    '     CGroup: /system.slice/nginx.service', '             ├─1123 "nginx: master process /usr/sbin/nginx"', '             └─1124 "nginx: worker process"'],
};
const termHTML = (lines, curOn = true) => ansi(lines.join('\n')) + (curOn ? '<span class="cur"></span>' : '');

scene('sessions', {
  X: 800, Y: 214, S: .84,
  plan(d, c) {
    return { tabHL: c[1] + .5, tabNew: c[2] - .35, type: c[2] + .2, enter: c[2] + 1.55, conn: c[2] + 2.1, motd: c[2] + 2.5, cmd: c[2] + 3.2, out: c[2] + 4.6 };
  },
  build(el, S) {
    el.innerHTML = `<div class="abs" style="left:120px;top:250px;width:640px">${head('01', S.kick, S.title)}
      <div style="margin-top:46px">${S.b.map((b, i) => bullet(b, 'b' + i)).join('')}</div></div>
      <div class="abs" data-r="wrap" style="left:${this.X}px;top:${this.Y}px">${appShell({ sel: '', tabs: '', main: `<div class="term" data-r="term"></div>${homeView()}` })}</div>
      <div class="abs keycap" data-r="kEnter" style="left:1240px;top:930px"><b>↵ Enter</b></div>`;
    const r = refs(el);
    r.app = el.querySelector('.app');
    r.app.style.transform = `scale(${this.S})`;
    r.home.style.display = 'none';
    r.sugg.innerHTML = [['web-01', 'deploy@web-01.prod.example.com', 'Production'], ['staging-web', 'deploy@staging.example.com', 'Staging']]
      .map(([n, a, g], i) => `<div class="it${i ? '' : ' sel'}"><i style="width:8px;height:8px;border-radius:50%;background:${HOSTC[n]}"></i><b>${n.replace('web', '<mark>web</mark>')}</b><span>${a.replace('web', '<mark>web</mark>')}</span><span class="g">${g}</span></div>`).join('');
    r.cache = {};
    applyScheme(r.term, TGKDARK);
    return r;
  },
  set(r, k, v, f) { if (r.cache[k] !== v) { r.cache[k] = v; f(v); } },
  render(r, t, d, c) {
    animHead(r, t);
    for (let i = 0; i < 5; i++) rise(r['b' + i], t, .9 + i * .16 + (c[2] > 6 ? 0 : 0), .6, 20);
    const p = this.plan(d, c);
    const ap = E.out(P(t, .2, 1));
    show(r.wrap, ap, (1 - ap) * 140, 0);
    // state
    let tabs, active, view, lines = [], cur = true;
    const base = [['staging-web', 'g'], ['homelab', 'g']];
    if (t < p.tabHL) { tabs = base; active = 0; view = 'term'; lines = TERM['staging-web']; }
    else if (t < p.tabNew) { tabs = base; active = 1; view = 'term'; lines = TERM.homelab; }
    else if (t < p.enter) { tabs = [...base, ['New tab', '']]; active = 2; view = 'home'; }
    else {
      tabs = [...base, ['web-01', t < p.motd ? 'y' : 'g']]; active = 2; view = 'term';
      if (t < p.motd) lines = ['Connecting to deploy@web-01.prod.example.com:22 …'];
      else {
        const n = Math.floor((t - p.motd) * 22);
        lines = TERM.motd.slice(0, n);
        if (n >= TERM.motd.length) {
          const cmd = typed('systemctl status nginx', t, p.cmd, 17);
          lines = [...lines, '[[b2|deploy@web-01]]:[[b4|~]]$ ' + cmd];
          if (t > p.out) lines = [...lines, ...TERM.nginx.slice(0, Math.floor((t - p.out) * 30)), ...(t > p.out + .4 ? ['[[b2|deploy@web-01]]:[[b4|~]]$ '] : [])];
        }
      }
    }
    const tabsHTML = tabs.map(([n, dot], i) => tab(n, { act: i === active, dot })).join('');
    this.set(r, 'tabs', tabsHTML, v => { r.tabs.innerHTML = v + `<div class="plus">${ic('plus', 'width:15px;height:15px')}</div>`; });
    r.home.style.display = view === 'home' ? 'block' : 'none';
    r.term.style.display = view === 'term' ? 'block' : 'none';
    if (view === 'term') this.set(r, 'term', termHTML(lines, blink(t) === 1) + lines.length, v => { r.term.innerHTML = termHTML(lines, blink(t) === 1); });
    const q = typed('web', t, p.type, 6);
    r.typed.textContent = q; r.ph.style.display = q ? 'none' : 'inline';
    r.caret.style.opacity = blink(t);
    r.sugg.style.display = q ? 'block' : 'none';
    r.statusL.innerHTML = view === 'term' && active === 2 && t > p.motd ? '<i class="sd"></i>deploy@web-01.prod.example.com:22 · Connected' : '8 saved hosts';
    show(r.kEnter, win(t, p.enter - .25, p.enter + .7, .25), 0, 0, 1);
    // cursor: tab clicks (stage coords)
    const tx = i => this.X + (260 + 210 * i + 100) * this.S, ty = this.Y + 22 * this.S;
    cursor([[p.tabHL - .9, 1300, 760], [p.tabHL - .05, tx(1), ty], [p.tabNew - .7, tx(1), ty], [p.tabNew - .05, this.X + (260 + 630 + 17) * this.S, ty], [p.tabNew + .9, 1350, 560]],
      [p.tabHL, p.tabNew], t, 1 - E.in(P(t, p.tabNew + .9, .4)));
  },
  sfx(d, c) {
    const p = this.plan(d, c);
    return [{ t: p.tabHL, type: 'click' }, { t: p.tabNew, type: 'click' }, { t: p.type, type: 'type', n: 3, cps: 6 }, { t: p.enter, type: 'key' },
      { t: p.cmd, type: 'type', n: 22, cps: 17 }, { t: p.cmd + 1.4, type: 'key' }];
  },
});

/* ===================== 4. split view ===================== */
const LAYOUTS = {
  one: [[0, 0, 1, 1], [1, 0, 0, 1], [1, 0, 0, 1], [1, 0, 0, 1], [1, 0, 0, 1], [1, 0, 0, 1]],
  cols: [[0, 0, .5, 1], [.5, 0, .5, 1], [1, 0, 0, 1], [1, 0, 0, 1], [1, 0, 0, 1], [1, 0, 0, 1]],
  rows: [[0, 0, 1, .5], [0, .5, 1, .5], [1, .5, 0, .5], [1, .5, 0, .5], [1, 0, 0, .5], [1, .5, 0, .5]],
  g4: [[0, 0, .5, .5], [.5, 0, .5, .5], [0, .5, .5, .5], [.5, .5, .5, .5], [1, 0, 0, .5], [1, .5, 0, .5]],
  g6: [[0, 0, 1 / 3, .5], [1 / 3, 0, 1 / 3, .5], [0, .5, 1 / 3, .5], [1 / 3, .5, 1 / 3, .5], [2 / 3, 0, 1 / 3, .5], [2 / 3, .5, 1 / 3, .5]],
};
const PANES = ['web-01', 'db-primary', 'staging-web', 'api-gateway', 'homelab', 'vps'];
function paneText(i, t) {
  const R = rng(i * 77 + Math.floor(t * 2));
  if (i === 0) { // htop
    const bars = [0, 1, 2, 3].map(k => {
      const v = clamp((32 + 26 * Math.sin(t * 1.7 + k * 1.3) + 12 * Math.sin(t * 3.3 + k)) / 100, .04, .97), n = Math.round(v * 22), s = Math.round(n * .3);
      return `  [[6|${k + 1}]]  [[[2|${'|'.repeat(n - s)}]][[1|${'|'.repeat(s)}]]${' '.repeat(22 - n)} [[8|${(v * 100).toFixed(1).padStart(5)}%]]]`;
    });
    const mem = Math.round(12 + 2 * Math.sin(t));
    const procs = [['1123', 'www-data', '6.2', '0.2', 'nginx: worker process'], ['2231', 'postgres', '4.8', '3.1', 'postgres: checkpointer'], ['3310', 'deploy', '3.9', '6.4', 'node /srv/app/server.js'],
      ['3312', 'deploy', '2.7', '6.1', 'node /srv/app/worker.js'], ['911', 'root', '0.7', '0.4', '/usr/lib/systemd/systemd-journald'], ['1', 'root', '0.0', '0.1', '/sbin/init']];
    return [...bars, `  [[6|Mem]][[[2|${'|'.repeat(mem)}]]${' '.repeat(22 - mem)}[[8|3.12G/7.75G]]]   Tasks: [[b6|87]], 312 thr`, `  [[6|Swp]][${' '.repeat(22)}[[8|0K/2.00G]]]   Load average: [[b6|0.84]] 0.71 0.66`, '',
      '[[g|    PID USER       CPU% MEM%  Command                         ]]',
      ...procs.map(p => `${p[0].padStart(7)} ${p[1].padEnd(9)} ${(+p[2] + R() * 3).toFixed(1).padStart(5)} ${p[3].padStart(4)}  ${p[4]}`)];
  }
  if (i === 1) {
    const n = Math.floor(t * 2.6) + 14, out = ['[[b2|postgres@db-primary]]:~$ tail -f /var/log/postgresql/postgresql-16-main.log'];
    for (let k = n - 13; k < n; k++) {
      const r2 = rng(k * 31), s = (13 * 60 + 41 + k * .4);
      const ts = `09:${String(Math.floor(s / 60) % 60).padStart(2, '0')}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
      const kind = r2();
      out.push(kind < .55 ? `[[8|${ts}]] [[6|LOG]]:  duration: ${(r2() * 9).toFixed(3)} ms  SELECT * FROM orders WHERE id = $1`
        : kind < .85 ? `[[8|${ts}]] [[6|LOG]]:  connection authorized: user=app database=shop`
          : `[[8|${ts}]] [[3|LOG]]:  checkpoint complete: wrote ${Math.floor(r2() * 900)} buffers`);
    }
    return out;
  }
  if (i === 2) {
    const steps = ['Pulling image app:1.4.3', 'Running migrations (3)', 'Warming cache', 'Rolling restart 1/3', 'Rolling restart 2/3', 'Rolling restart 3/3', 'Health check'];
    const k = Math.floor(t / 1.1) % (steps.length + 3);
    const out = ['[[b2|deploy@staging-web]]:~$ ./deploy.sh v1.4.3'];
    steps.forEach((s, j) => { if (j < k) out.push(`  [[b2|✓]] ${s}`); else if (j === k) out.push(`  [[b3|▶]] ${s} [[8|${'.'.repeat(1 + Math.floor(t * 3) % 3)}]]`); });
    if (k >= steps.length) out.push('', '  [[b2|Deployed v1.4.3 to staging in 41 s]]');
    return out;
  }
  if (i === 3) {
    const st = (a, b, ph) => ((t + ph) % 6 < a ? '[[3|ContainerCreating]]' : '[[2|Running]]          ');
    return ['[[b2|deploy@api-gateway]]:~$ kubectl get pods -n api -w', 'NAME                      READY  STATUS             AGE',
      `api-7d9f6c5b8-2xkqp       1/1    [[2|Running]]            3d`, `api-7d9f6c5b8-8jz4m       1/1    [[2|Running]]            3d`,
      `api-7d9f6c5b8-tq9wd       1/1    ${st(1.4, 0, 0)} 12s`, `gateway-5c8d7f9d4-lm2pn   2/2    [[2|Running]]            9d`, `gateway-5c8d7f9d4-x7r4v   2/2    ${st(1, 0, 3)} 4s`];
  }
  if (i === 4) {
    const c = k => (8 + 7 * Math.abs(Math.sin(t * 1.3 + k))).toFixed(2);
    return ['[[b2|pi@homelab]]:~ $ docker stats --no-stream', 'NAME            CPU %    MEM USAGE', `home-assistant  ${c(0)}%   412MiB`, `pihole          ${c(1)}%   88MiB`, `grafana         ${c(2)}%   156MiB`, `jellyfin        ${c(3)}%   690MiB`];
  }
  const n = Math.floor(t * 2) + 1, out = ['[[b2|root@vps]]:~# ping 1.1.1.1', 'PING 1.1.1.1 (1.1.1.1) 56(84) bytes of data.'];
  for (let k = Math.max(1, n - 10); k <= n; k++) out.push(`64 bytes from 1.1.1.1: icmp_seq=${k} ttl=58 time=${(3 + rng(k)() * 1.8).toFixed(2)} ms`);
  return out;
}
scene('split', {
  X: 320, Y: 236,
  plan(d, c, e) {
    return { cols: c[0] + 2.2, rows: c[1] + .2, g4: c[1] + 1.4, g6: c[1] + 2.6, f1: c[2] + .6, f2: c[2] + 1.4, max: c[2] + 2.6, unmax: Math.min(d - 1.2, c[2] + 4.6) };
  },
  build(el, S) {
    const tabs = PANES.map((n, i) => `<div class="tab${i ? '' : ' act'}" style="width:150px"><i class="d"></i><span class="tn">${n}</span></div>`).join('');
    el.innerHTML = `<div class="abs" style="left:120px;top:56px">${head('02', S.kick, S.title).replace('<h1', '<h1 class="sm"')}</div>
      <div class="abs" data-r="lay" style="left:1210px;top:150px;display:flex;gap:10px"></div>
      <div class="abs" data-r="wrap" style="left:${this.X}px;top:${this.Y}px">${appShell({ side: false, tabs, right: `<span data-r="lbtn" style="display:flex;gap:10px;color:#9aa3b2">${ic('layout-single')}${ic('layout-columns')}${ic('layout-rows')}${ic('layout-grid')}${ic('layout-grid6')}</span>`, status: '6 sessions' })}</div>
      <div class="abs keycap" data-r="key" style="left:1440px;top:960px"></div>`;
    const r = refs(el);
    r.panes = PANES.map((n, i) => {
      const p = html(`<div class="abs" style="border-radius:8px;overflow:hidden;background:#0f1015;border:1px solid #2a2d36">
        <div style="height:26px;display:flex;align-items:center;gap:8px;padding:0 10px;background:#15171d;font-size:12px;color:#c9cfdb;border-bottom:1px solid #22252e"><i style="width:7px;height:7px;border-radius:50%;background:${HOSTC[n]}"></i><b style="font-weight:600">${n}</b><span style="margin-left:auto;color:#6f7788">${ADDR[n].split('@')[0]}</span></div>
        <div class="term" style="top:26px;font-size:12px;line-height:15px;padding:6px 8px"></div></div>`);
      applyScheme(p.querySelector('.term'), TGKDARK);
      r.main.appendChild(p);
      return { el: p, term: p.querySelector('.term'), last: '' };
    });
    r.S = S;
    return r;
  },
  render(r, t, d, c, e) {
    animHead(r, t);
    const p = this.plan(d, c, e);
    show(r.wrap, E.out(P(t, .1, .9)), 0, (1 - E.out(P(t, .1, .9))) * 60);
    const seq = [['one', -99], ['cols', p.cols], ['rows', p.rows], ['g4', p.g4], ['g6', p.g6]];
    let A = 'one', B = 'one', k = 0, li = -1;
    for (let i = 0; i < seq.length; i++) if (t >= seq[i][1]) { A = i ? seq[i - 1][0] : 'one'; B = seq[i][0]; k = E.io(P(t, seq[i][1], .55)); li = i - 1; }
    const W = 1268, H = 724, ox = 6, oy = 6;
    const maxK = E.io(P(t, p.max, .45)) * (1 - E.io(P(t, p.unmax, .45)));
    const focus = t < p.f1 ? 0 : t < p.f2 ? 1 : 3;
    r.panes.forEach((pn, i) => {
      const a = LAYOUTS[A][i], b = LAYOUTS[B][i];
      let [x, y, w, h] = a.map((v, j) => lerp(v, b[j], k));
      if (i === 3) { x = lerp(x, 0, maxK); y = lerp(y, 0, maxK); w = lerp(w, 1, maxK); h = lerp(h, 1, maxK); }
      const vis = w * W > 8;
      pn.el.style.display = vis ? 'block' : 'none';
      if (!vis) return;
      const g = 6;
      Object.assign(pn.el.style, { left: ox + x * W + 'px', top: oy + y * H + 'px', width: Math.max(0, w * W - g) + 'px', height: Math.max(0, h * H - g) + 'px',
        zIndex: i === 3 ? 2 : 1, opacity: i === 3 ? 1 : 1 - maxK * .9 });
      const fs = 12 + 5 * clamp((w * W - 430) / 830);
      pn.term.style.fontSize = fs + 'px'; pn.term.style.lineHeight = Math.round(fs * 1.25) + 'px';
      const f = B !== 'one' && i === focus && t > c[2];
      pn.el.style.boxShadow = f ? '0 0 0 2px #528bff' : 'none';
      const txt = termHTML(paneText(i, t), i === focus && blink(t));
      if (txt !== pn.last) { pn.term.innerHTML = txt; pn.last = txt; }
    });
    // layout chip + key hint
    const labels = r.S.lay;
    const curLab = maxK > .5 ? labels[4] : li >= 0 ? labels[[0, 1, 2, 3][li]] : '';
    r.lay.innerHTML = curLab ? `<span class="chip on">${ic(['layout-columns', 'layout-rows', 'layout-grid', 'layout-grid6', 'maximize'][maxK > .5 ? 4 : li], 'width:18px;height:18px')}${curLab}</span>` : '';
    r.lay.style.opacity = curLab ? 1 : 0;
    const keys = [[p.cols, 'Ctrl+Shift+E', 0], [p.rows, 'Ctrl+Shift+O', 1], [p.f1, 'Alt+→', 2], [p.f2, 'Alt+↓', 2], [p.max, 'Ctrl+Shift+X', 3]];
    let kk = null;
    for (const x of keys) if (t >= x[0] - .1 && t < x[0] + 1) kk = x;
    if (kk) { r.key.innerHTML = `<b>${kk[1]}</b>${r.S.keys[kk[2]]}`; show(r.key, win(t, kk[0] - .1, kk[0] + .8, .2), 0, 0, 1); } else r.key.style.opacity = 0;
  },
  sfx(d, c, e) { const p = this.plan(d, c, e); return [p.cols, p.rows, p.g4, p.g6, p.max, p.unmax].map(t => ({ t, type: 'swish' })).concat([p.f1, p.f2].map(t => ({ t, type: 'key' }))); },
});

/* ===================== 5. terminal & schemes ===================== */
const SHOW = [
  ['[[b2|deploy@web-01]]:[[b4|~/app]] [[5|(main)]]$ ', 'git log --oneline --graph -4'],
  '[[3|*]] [[3|1824a19]] [[3|(]][[b6|HEAD -> ]][[b2|main]][[3|, ]][[b1|origin/main]][[3|)]] Updates: ask GitHub on every start',
  '[[3|*]] [[3|f23cb72]] Color schemes: no Green CRT', '[[3|*]] [[3|86d4ccb]] Color schemes: Teletype (ink on paper)', '[[3|*]] [[3|eedb159]] Color schemes: Amber CRT, Commodore 64, MS-DOS',
  ['[[b2|deploy@web-01]]:[[b4|~/app]] [[5|(main)]]$ ', 'ls'],
  '[[b4|bin]]  [[b4|config]]  [[b6|current]]  [[b2|deploy.sh]]  docker-compose.yml  [[b4|logs]]  README.md',
  ['[[b2|deploy@web-01]]:[[b4|~/app]] [[5|(main)]]$ ', 'cat config/app.yml'],
  '[[4|server]]:', '  [[4|port]]: [[3|8080]]', '  [[4|workers]]: [[3|4]]', '  [[4|tls]]: [[5|true]]', '[[4|database]]:', '  [[4|host]]: [[2|"db-primary.internal"]]', '  [[4|pool]]: [[3|20]]   [[8|# tuned for 4 vCPU]]',
  ['[[b2|deploy@web-01]]:[[b4|~/app]] [[5|(main)]]$ ', './healthcheck.sh'],
  '  [[b2|✓]] api        [[2|200 OK]]     [[8|12 ms]]', '  [[b2|✓]] database   [[2|healthy]]    [[8|3 ms]]', '  [[b3|!]] cache      [[3|degraded]]   [[8|81 ms]]', '  [[b1|✗]] mailer     [[1|timeout]]    [[8|5000 ms]]',
  'wide: 日本語テキスト ✓ λ π é ñ  [[b5|bold]] [[8|dim]] <u>underline</u>',
];
scene('terminal', {
  plan(d, c, e) {
    const s0 = c[1] + .1, s1 = d - .7, n = SCHEMES.length - 1;
    return { s0, step: (s1 - s0) / n, type0: .9, rate: Math.max(.25, (c[1] - 1.4) / 13) };
  },
  build(el, S) {
    el.innerHTML = `<div class="abs" style="left:120px;top:150px;width:620px">${head('03', S.kick, S.title)}
       <div data-r="chips" style="margin-top:40px;display:flex;flex-wrap:wrap;gap:12px">${S.chips.map((x, i) => `<span class="chip" data-r="ch${i}">${x}</span>`).join('')}</div>
       <div data-r="sch" style="margin-top:54px">
         <div style="font:600 17px Inter;letter-spacing:.16em;text-transform:uppercase;color:#8b93a3">${S.scheme} · <span data-r="cnt"></span> / ${SCHEMES.length} ${S.of}</div>
         <div data-r="sname" style="font:760 54px Inter;margin-top:10px;letter-spacing:-.02em"></div>
         <div data-r="sw" style="display:flex;gap:6px;margin-top:16px">${Array.from({ length: 16 }, (_, i) => `<i style="width:30px;height:30px;border-radius:7px;background:var(--c${i})"></i>`).join('')}</div></div></div>
      <div class="abs" data-r="win" style="left:790px;top:130px;width:1010px;height:820px;border-radius:14px;overflow:hidden;border:1px solid #2b2f3a;box-shadow:0 50px 140px rgba(0,0,0,.65)">
        <div style="height:40px;background:#0d0e12;display:flex;align-items:flex-end;padding-left:10px">${tab('web-01', { act: true })}
          <div data-r="hdr" style="margin-left:auto;align-self:center;padding-right:16px;font:500 13px Inter;color:#9aa3b2"></div></div>
        <div class="term" data-r="term" style="top:40px;font-size:19px;line-height:25px;padding:18px 22px"></div>
        <div data-r="scan" class="abs" style="inset:40px 0 0 0;background:repeating-linear-gradient(0deg,rgba(0,0,0,.22) 0 2px,transparent 2px 4px);pointer-events:none"></div>
        <div class="abs" data-r="tc" style="left:18px;right:18px;bottom:22px;height:22px;border-radius:4px;background:linear-gradient(90deg,#ff006a,#ff8a00,#ffe600,#2bd96b,#00c2ff,#7a5cff,#ff00d4)"></div></div>`;
    const r = refs(el);
    // the window's .app-like tab needs the app styles
    r.win.classList.add('app'); Object.assign(r.win.style, { width: '1010px', height: '820px', position: 'absolute' });
    r.lastHTML = '';
    return r;
  },
  render(r, t, d, c, e) {
    animHead(r, t);
    const p = this.plan(d, c, e);
    show(r.win, E.out(P(t, .2, .9)), (1 - E.out(P(t, .2, .9))) * 100, 0);
    const per = (e[0] - c[0]) / r.chips.children.length;
    for (let i = 0; i < 7; i++) { pop(r['ch' + i], t, c[0] + i * per * .9); r['ch' + i].classList.toggle('on', t > c[0] + i * per * .9 && t < c[0] + i * per * .9 + 1.4); }
    // content: commands typed, outputs appear
    let tt = p.type0, out = [];
    for (const ln of SHOW) {
      if (Array.isArray(ln)) {
        const cmd = typed(ln[1], t, tt, 28);
        if (t < tt) break;
        out.push(ln[0] + cmd);
        tt += ln[1].length / 28 + .25;
      } else { if (t < tt) break; out.push(ln); tt += .07; }
    }
    const html = ansi(out.join('\n')).replace('&lt;u&gt;', '<u>').replace('&lt;/u&gt;', '</u>') + (blink(t) ? '<span class="cur"></span>' : '');
    if (html !== r.lastHTML) { r.term.innerHTML = html; r.lastHTML = html; }
    r.tc.style.opacity = E.out(P(t, tt, .4));
    // scheme cycle
    const f = (t - p.s0) / p.step;
    let a = 0, b = 0, k = 0;
    if (f > 0) { a = Math.min(SCHEMES.length - 1, Math.floor(f)); b = Math.min(SCHEMES.length - 1, a + 1); k = E.io(clamp((f - a) / .3)); if (a === b) k = 0; }
    applyScheme(r.win, a, b, k);
    applyScheme(r.sw, a, b, k);
    const cur = k > .5 ? b : a, retro = SCHEMES[cur][5] === 'retro';
    r.term.style.fontFamily = retro ? 'VT323' : 'DejaVu';
    r.term.style.fontSize = retro ? '26px' : '19px'; r.term.style.lineHeight = '25px';
    r.term.style.textShadow = /Amber|Synthwave|Commodore/.test(SCHEMES[cur][0]) ? '0 0 6px currentColor' : 'none';
    r.scan.style.opacity = /Amber|Commodore|MS-DOS/.test(SCHEMES[cur][0]) ? .9 : 0;
    r.sname.textContent = SCHEMES[cur][0];
    r.cnt.textContent = cur + 1;
    r.hdr.textContent = SCHEMES[cur][0];
    show(r.sch, E.out(P(t, p.s0 - .6, .6)), 0, (1 - E.out(P(t, p.s0 - .6, .6))) * 20);
    const flip = f > 0 && f < SCHEMES.length ? 1 - clamp(Math.abs(f - Math.round(f)) * 6) : 0;
    r.sname.style.transform = `translateY(${-flip * 6}px)`;
  },
  sfx(d, c, e) {
    const p = this.plan(d, c, e), ev = [];
    for (let i = 1; i < SCHEMES.length; i++) ev.push({ t: p.s0 + i * p.step, type: 'tick' });
    let tt = p.type0;
    for (const ln of SHOW) if (Array.isArray(ln)) { ev.push({ t: tt, type: 'type', n: ln[1].length, cps: 28 }); tt += ln[1].length / 28 + .25; } else tt += .07;
    return ev;
  },
});

/* ===================== 6. files ===================== */
const FROWS = [['.cache', '—', 'Oct 5 13:25', 'drwxr-xr-x', 1], ['config', '—', 'Oct 5 13:29', 'drwxr-xr-x', 1], ['current', '—', 'Oct 5 13:25', 'lrwxr-xr-x', 1, 'releases/v1.4.2'],
  ['logs', '—', 'Oct 6 09:12', 'drwxr-xr-x', 1], ['releases', '—', 'Oct 5 13:25', 'drwxr-xr-x', 1], ['.env', '214 B', 'Oct 2 18:40', '-rw-------'], ['deploy.sh', '2.1 KB', 'Oct 5 13:25', '-rwxr-x---'],
  ['nginx.conf', '1.4 KB', 'Oct 5 13:29', 'lrw-r--r--', 0, 'config/nginx.conf'], ['README.md', '7 KB', 'Sep 28 10:02', '-rw-r--r--']];
const NEWROW = ['release-v1.4.3.tar.gz', '48.2 MB', 'Oct 6 09:44', '-rw-r--r--'];
const BROWS = [['2026-10-01', '—', '', '', 1], ['2026-10-04', '—', '', '', 1], ['db-2026-10-06.sql.gz', '812 MB'], ['release-v1.4.2.tar.gz', '47.9 MB']];
const NGINX = ['user www-data;', 'worker_processes auto;', 'pid /run/nginx.pid;', '', 'events {', '    worker_connections 1024;', '}', '', 'http {', '    sendfile on;', '    keepalive_timeout 65;',
  '    include /etc/nginx/conf.d/*.conf;', '    include /etc/nginx/sites-enabled/*;', '}'];
const ngx = s => esc(s).replace(/(\d+)/g, '<i class="nn">$1</i>').replace(/^(\s*)([a-z_]+)/, '$1<i class="nk">$2</i>').replace(/([{}])/g, '<i class="np">$1</i>');
scene('files', {
  X: 320, Y: 236,
  plan(d, c) {
    const k = clamp((c[2] - c[1]) / 7, .6, 1.2);
    return { up: c[1] + .2, drop: c[1] + 1.3 * k, upEnd: c[1] + 3 * k, ed: c[1] + 3.1 * k, edit: c[1] + 4.1 * k, save: c[1] + 5.2 * k, edEnd: c[2] - .25,
      split: c[2] + .05, grab: c[2] + 1, rel: c[2] + 2, dlg: c[2] + 2.15, copy: c[2] + 3.5, xEnd: c[2] + 5.4 };
  },
  build(el, S) {
    el.innerHTML = `<div class="abs" style="left:120px;top:56px">${head('04', S.kick, S.title).replace('<h1', '<h1 class="sm"')}</div>
      <div class="abs" style="left:1240px;top:150px;display:flex;gap:10px">${S.chips.map((x, i) => `<span class="chip" data-r="ch${i}">${x}</span>`).join('')}</div>
      <div class="abs" data-r="wrap" style="left:${this.X}px;top:${this.Y}px">${appShell({ sel: 'web-01', tabs: tab('web-01 · Files', { act: true }) + tab('db-primary') })}</div>
      <div class="abs" data-r="ghost" style="padding:10px 16px;border-radius:10px;background:#26324d;border:1px solid #528bff;font:600 15px Inter;display:flex;gap:10px;align-items:center;box-shadow:0 20px 40px rgba(0,0,0,.5);white-space:nowrap">${ic('file', 'width:16px;height:16px')}release-v1.4.3.tar.gz <span style="color:#9aa3b2;font-weight:500">48.2 MB</span></div>
      <div class="abs keycap" data-r="kSave" style="left:1380px;top:960px"><b>Ctrl+S</b></div>`;
    const r = refs(el);
    r.main.innerHTML = `<div class="pane" data-r="pA" style="left:0;width:1020px">${filesToolbar('/var/www/app')}${fileHead()}<div data-r="rowsA"></div>
        <div data-r="dropov" class="abs" style="inset:44px 6px 6px 6px;border:2px dashed #528bff;border-radius:10px;background:rgba(82,139,255,.08);display:grid;place-items:center;font:600 18px Inter;color:#9fc0ff">${S.drop}</div></div>
      <div class="pane" data-r="pE" style="left:470px;width:550px;border-left:1px solid #2a2d36;background:#121318">
        <div class="phead"><span class="tag"><i class="dt" style="background:#e5534b"></i>nginx.conf</span><span class="p">/var/www/app/config/nginx.conf</span><span style="margin-left:auto;color:#8b93a3">Ctrl+S</span></div>
        <div class="editor" data-r="ed"></div></div>
      <div class="pane" data-r="pB" style="left:510px;width:510px;border-left:1px solid #2a2d36">
        <div class="phead"><span class="tag"><i class="dt" style="background:#e5534b"></i>db-primary</span><span class="p">/backups/web-01</span></div>
        <div style="position:absolute;left:0;right:0;top:-8px">${fileHead(true).replace('top:44px', '')}</div><div data-r="rowsB"></div></div>
      <div class="xfer" data-r="xfer"></div>
      <div class="toast" data-r="toast" style="right:24px;top:56px">${ic('check', 'width:16px;height:16px;color:#56d364')}Saved to web-01 · permissions kept</div>
      <div class="dlg" data-r="dlg" style="left:250px;top:150px;width:520px;padding:24px">
        <h3>Copy 1 item to db-primary</h3><div class="s">release-v1.4.3.tar.gz → /backups/web-01</div>
        <div class="radio on"><span class="o"></span><div><b>Through this computer</b><span>The hosts never need to reach each other.</span></div></div>
        <div class="radio"><span class="o"></span><div><b>Host to host over SSH</b><span>web-01 connects to db-primary with credentials from the vault.</span></div></div>
        <div style="display:flex;justify-content:flex-end;gap:10px;margin-top:22px"><span class="btn2">Cancel</span><span class="btn2 pri" data-r="copyBtn">Copy</span></div></div>`;
    Object.assign(r, refs(el));
    r.cache = {};
    return r;
  },
  render(r, t, d, c) {
    animHead(r, t);
    const p = this.plan(d, c), S = this.S;
    [0, 1, 2].forEach(i => { pop(r['ch' + i], t, [.8, p.ed, p.split][i]); r['ch' + i].classList.toggle('on', [t > p.up && t < p.ed, t > p.ed && t < p.split, t > p.split][i]); });
    show(r.wrap, E.out(P(t, .1, .9)), 0, (1 - E.out(P(t, .1, .9))) * 60);
    const toStage = (x, y) => [this.X + 260 + x, this.Y + 40 + y];
    const added = t > p.upEnd, inB = t > p.xEnd;
    const split = E.io(P(t, p.split, .5));
    // pane A rows
    const rowsA = added ? [...FROWS, NEWROW] : FROWS;
    const selA = t > p.ed - .4 && t < p.split ? 7 : t > p.grab - .3 && t < p.xEnd ? 9 : -1;
    const keyA = `${rowsA.length}|${selA}|${split > .5}`;
    if (r.cache.a !== keyA) { r.cache.a = keyA; r.rowsA.innerHTML = fileRows(rowsA, { sel: selA, compact: split > .5 }); }
    for (let i = 0; i < rowsA.length; i++) { const el = r.rowsA.children[i]; if (el) el.style.opacity = clamp((t - .5 - i * .06) / .3); }
    r.pA.style.width = lerp(1020, 505, split) + 'px';
    r.pA.classList.toggle('compact', split > .5);
    r.dropov.style.opacity = win(t, p.drop - .6, p.drop + .05, .2);
    // ghost (upload, then pane to pane drag)
    let gx = null, gy = null;
    if (t > p.up && t < p.drop + .05) { const k = E.io(P(t, p.up, p.drop - p.up)); gx = lerp(1700, 980, k); gy = lerp(150, 560, k); }
    if (t > p.grab && t < p.rel + .05) { const k = E.io(P(t, p.grab, p.rel - p.grab)); [gx, gy] = [lerp(this.X + 300, this.X + 900, k), lerp(this.Y + 40 + 74 + 9 * 30 + 6, this.Y + 260, k)]; }
    if (gx !== null) show(r.ghost, 1, gx + 16, gy + 18, 1); else r.ghost.style.opacity = 0;
    // editor
    const edK = E.io(P(t, p.ed, .45)) * (1 - E.io(P(t, p.edEnd, .4)));
    r.pE.style.display = edK > 0 ? 'block' : 'none';
    r.pE.style.transform = `translateX(${(1 - edK) * 560}px)`;
    if (edK > 0) {
      const del = clamp((t - p.edit) * 12, 0, 4), add = typed('4096', t, p.edit + .45, 9);
      const lines = NGINX.map((s, i) => i === 5 ? `    worker_connections ${'1024'.slice(0, 4 - Math.floor(del))}${add}${t > p.edit - .3 && t < p.save + .3 && blink(t) ? '|' : ''};` : s);
      const h = lines.map((s, i) => `<div class="row${i === 5 && t > p.edit - .4 ? ' hl' : ''}"><span class="ln">${i + 1}</span>${ngx(s)}</div>`).join('');
      if (r.cache.e !== h) { r.cache.e = h; r.ed.innerHTML = h; }
    }
    show(r.toast, win(t, p.save + .15, p.save + 1.5, .25), 0, 0, 1);
    show(r.kSave, win(t, p.save - .15, p.save + .7, .2), 0, 0, 1);
    // pane B
    r.pB.style.display = split > 0 ? 'block' : 'none';
    r.pB.style.transform = `translateX(${(1 - split) * 520}px)`;
    const rowsB = inB ? [...BROWS, NEWROW] : BROWS;
    const keyB = `${rowsB.length}|${t > p.rel - .3 && t < p.dlg + .2}`;
    if (r.cache.b !== keyB) { r.cache.b = keyB; r.rowsB.innerHTML = fileRows(rowsB, { compact: true, y0: 66, drop: t > p.rel - .3 && t < p.dlg + .2 ? 0 : -1, sel: inB ? 4 : -1 }); }
    // dialog
    const dk = win(t, p.dlg, p.copy + .1, .3);
    show(r.dlg, dk, 0, (1 - dk) * 20, lerp(.96, 1, dk));
    // transfers
    let xf = '';
    if (t > p.drop && t < p.ed + 1.2) {
      const k = clamp((t - p.drop - .1) / (p.upEnd - p.drop - .1));
      xf = `<div class="h">Transfers</div><div class="row">${ic('upload', 'width:16px;height:16px;color:#82c4ff')}<div style="flex:1"><b>release-v1.4.3.tar.gz</b> <span style="color:#8b93a3">to /var/www/app</span>
        <div class="bar"><div style="width:${k * 100}%"></div></div></div><span style="color:#9aa3b2;font-size:12px;width:200px;text-align:right">${k < 1 ? `${(36 + 4 * Math.sin(t * 5)).toFixed(1)} MB/s · ${Math.ceil((1 - k) * 2)} s left` : 'Done · 48.2 MB'}</span></div>`;
    }
    if (t > p.copy + .1) {
      const k = clamp((t - p.copy - .2) / (p.xEnd - p.copy - .3));
      xf = `<div class="h">Transfers</div><div class="row">${ic('route', 'width:16px;height:16px;color:#82c4ff')}<div style="flex:1"><b>release-v1.4.3.tar.gz</b> <span style="color:#8b93a3">web-01 → db-primary · through this computer</span>
        <div class="bar"><div style="width:${k * 100}%"></div></div></div><span style="color:#9aa3b2;font-size:12px;width:200px;text-align:right">${k < 1 ? `${(52 + 5 * Math.sin(t * 4)).toFixed(1)} MB/s` : 'Done · 48.2 MB'}</span></div>`;
    }
    if (r.cache.x !== xf) { r.cache.x = xf; r.xfer.innerHTML = xf; }
    r.xfer.style.display = xf ? 'block' : 'none';
    // cursor
    const [rx, ry] = toStage(200, 74 + 7 * 30 + 15);
    const [cx, cy] = toStage(300, 74 + 9 * 30 + 15);
    cursor([[p.up - .1, 1700, 160], [p.drop, 1250, 580], [p.ed - .7, 1250, 580], [p.ed - .2, rx, ry], [p.edit - .2, rx + 620, ry - 120], [p.grab - .5, rx + 700, ry - 100], [p.grab, cx, cy],
      [p.rel, this.X + 900, this.Y + 262], [p.copy - .7, this.X + 900, this.Y + 262], [p.copy, this.X + 260 + 250 + 470, this.Y + 40 + 150 + 268], [p.copy + 1.2, 1500, 700]],
      [p.ed - .25, p.ed - .1, p.copy], t, (t < p.drop + .1 || t > p.ed - .9) ? 1 - E.in(P(t, p.copy + .8, .4)) : 1);
  },
  sfx(d, c) {
    const p = this.plan(d, c);
    return [{ t: p.drop, type: 'pop' }, { t: p.upEnd, type: 'ding' }, { t: p.ed - .25, type: 'click' }, { t: p.ed - .1, type: 'click' }, { t: p.edit, type: 'type', n: 8, cps: 9 },
      { t: p.save, type: 'key' }, { t: p.save + .15, type: 'ding' }, { t: p.grab, type: 'click' }, { t: p.rel, type: 'pop' }, { t: p.copy, type: 'click' }, { t: p.xEnd, type: 'ding' }];
  },
});
