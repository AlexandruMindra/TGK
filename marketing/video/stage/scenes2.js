// Scenes 7-14: hosts & keys, agents, security, sync, platforms, benefits, community, outro.

/* ===================== 7. hosts, keys, inheritance ===================== */
scene('hosts', {
  build(el, S) {
    const PK = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHk7Qe2vT9mWb0xKp3sLr8dYc4fNq1aZu6jG5hVt0wE deploy@tgk';
    el.innerHTML = `<div class="abs" style="left:120px;top:250px;width:600px">${head('05', S.kick, S.title)}</div>
    <!-- A: keys -->
    <div class="panel" data-r="A" style="left:800px;top:150px;width:1000px;height:780px;padding:36px 40px">
      <div class="pt">${S.a}</div>
      <div style="display:flex;gap:12px;margin-top:26px"><span class="chip on" data-r="ed">Ed25519</span><span class="chip">RSA 4096</span>
        <span class="btn2 pri" data-r="gen" style="margin-left:auto;height:46px;font-size:17px;padding:0 24px">${ic('key', 'width:16px;height:16px')} Generate key</span></div>
      <div data-r="keybox" style="margin-top:26px;border-radius:12px;background:#0e0f14;border:1px solid #2a2d36;padding:18px 20px;font:500 17px/1.6 JBM;color:#c9cfdb;min-height:130px;word-break:break-all">
        <div style="color:#8b93a3;font-family:Inter;font-size:14px;margin-bottom:6px">SHA256:<span data-r="fp"></span></div><span data-r="pk"></span></div>
      <div style="display:flex;gap:12px;margin-top:16px"><span class="btn2" data-r="copyk">${ic('copy', 'width:15px;height:15px')} Copy as authorized_keys line</span><span class="toast" data-r="copied" style="position:static;padding:8px 14px">${ic('check', 'width:14px;height:14px;color:#56d364')} Copied</span></div>
      <div style="margin-top:34px;font:700 14px Inter;letter-spacing:.12em;color:#8b93a3;text-transform:uppercase">Identities</div>
      ${[['deploy', 'Private key · Ed25519', '5 hosts'], ['postgres', 'Password', '1 host'], ['pi', 'Private key · RSA 4096', '1 host']].map(([n, k, h], i) =>
      `<div data-r="id${i}" style="display:flex;align-items:center;gap:16px;margin-top:12px;padding:14px 18px;border-radius:12px;background:#1b1e27;border:1px solid #2c3140">
          <span class="iconbox" style="width:40px;height:40px;background:#26324d;color:#82c4ff">${ic(i === 1 ? 'lock' : 'key', 'width:20px;height:20px')}</span>
          <b style="font-size:19px">${n}</b><span style="color:#9aa3b2;font-size:16px">${k}</span><span style="margin-left:auto;color:#8b93a3;font-size:15px">${h}</span></div>`).join('')}
      <div data-r="found" style="margin-top:22px;display:flex;gap:10px;align-items:center;flex-wrap:wrap"><span style="color:#8b93a3;font-size:15px">${S.found}:</span>
        ${['~/.ssh/id_ed25519', '~/.ssh/config', 'PuTTY sessions', 'WinSCP sessions'].map(x => `<span class="chip" style="font-size:15px;padding:6px 12px;font-family:JBM">${x}</span>`).join('')}</div>
    </div>
    <!-- B: inheritance -->
    <div class="panel" data-r="B" style="left:800px;top:150px;width:1000px;height:780px;padding:36px 40px">
      <div class="pt">${S.b}</div>
      ${S.layers.map((n, i) => `<div data-r="L${i}" style="position:absolute;left:40px;right:40px;top:${100 + i * 92}px;height:76px;border-radius:14px;display:flex;align-items:center;gap:18px;padding:0 22px;
        background:${['#191c24', '#1c2030', '#1f2540', '#243055'][i]};border:1px solid ${['#2a2e3a', '#2f3650', '#34407a', '#4a66c0'][i]}">
        <span style="font:700 14px JBM;color:#8fb4ff;width:24px">${i + 1}</span><b style="font-size:20px;width:300px">${n}</b>
        <span style="color:#c9cfdb;font:500 16px JBM">${['Keep-alive: off · DejaVu Sans Mono 14', 'Font size: 15', 'Jump host: bastion · Keep-alive: 15 s', 'Color scheme: Dracula'][i]}</span></div>`).join('')}
      <div data-r="arrow" style="position:absolute;left:22px;top:150px;width:2px;height:300px;background:linear-gradient(#528bff,#9b7bff)"></div>
      <div data-r="eff" style="position:absolute;left:40px;right:40px;top:490px;border-radius:16px;padding:20px 24px;background:linear-gradient(180deg,rgba(82,139,255,.16),rgba(82,139,255,.05));border:1px solid rgba(82,139,255,.55)">
        <div style="font:700 15px Inter;letter-spacing:.12em;text-transform:uppercase;color:#cfe0ff">${S.eff}</div>
        ${[['Jump host', 'bastion', 0], ['Keep-alive', '15 s', 0], ['Font', 'DejaVu Sans Mono 15', 1], ['Color scheme', 'Dracula', 2], ['Auto-reconnect', 'off', 3]].map(([k, v, f], i) =>
      `<div data-r="e${i}" style="display:flex;align-items:center;margin-top:12px;font-size:19px"><span style="width:220px;color:#9aa3b2">${k}</span><b style="width:300px">${v}</b><span class="chip" style="font-size:14px;padding:5px 12px">${S.from[f]}</span></div>`).join('')}</div>
    </div>
    <!-- C: route -->
    <div class="panel" data-r="C" style="left:800px;top:150px;width:1000px;height:780px;padding:36px 40px">
      <div class="pt">${S.c}</div>
      ${[[S.you, 'monitor', 60], ['bastion', 'shield', 380], ['web-01', 'server', 700]].map(([n, icn, x], i) =>
      `<div class="node" data-r="n${i}" style="left:${x}px;top:130px;width:220px;text-align:center"><span class="iconbox" style="margin:0 auto 12px;background:${['#26324d', '#3a2d55', '#4a2326'][i]};color:${['#82c4ff', '#c79bff', '#ff8b84'][i]}">${ic(icn)}</span><b>${n}</b><span>${['', 'deploy@bastion', 'deploy@web-01'][i] || '&nbsp;'}</span></div>`).join('')}
      <svg class="wires" data-r="wires"><path d="M280 210 H380" stroke="#3a4152" stroke-width="3"/><path d="M600 210 H700" stroke="#3a4152" stroke-width="3"/>
        <circle data-r="pk1" r="7" fill="#82c4ff"/><circle data-r="pk2" r="7" fill="#c79bff"/></svg>
      <div data-r="hops" style="position:absolute;left:0;right:0;top:350px;text-align:center;color:#9aa3b2;font-size:18px">${S.hops}</div>
      ${[['L', 'localhost:5432 → db-primary:5432', 'Local'], ['R', 'web-01:9000 → localhost:3000', 'Remote'], ['D', 'SOCKS5 on localhost:1080', 'Dynamic'], ['$', 'tmux attach || tmux new', 'Startup command']].map(([k, v, n], i) =>
      `<div data-r="t${i}" style="position:absolute;left:40px;right:40px;top:${420 + i * 78}px;height:64px;border-radius:12px;background:#1b1e27;border:1px solid #2c3140;display:flex;align-items:center;gap:18px;padding:0 20px">
          <span style="width:40px;height:40px;border-radius:10px;display:grid;place-items:center;background:#26324d;color:#82c4ff;font:700 18px JBM">${k}</span><b style="font-size:19px;width:240px">${n}</b><span style="font:500 18px JBM;color:#c9cfdb">${v}</span>
          <span style="margin-left:auto;width:10px;height:10px;border-radius:50%;background:#3fb950"></span></div>`).join('')}
    </div>`;
    const r = refs(el);
    r.PK = PK;
    return r;
  },
  render(r, t, d, c) {
    animHead(r, t);
    const b0 = c[1] - 1.2, c0 = c[2] + .5;
    const A = win(t, .3, b0 - .25, .45), B = win(t, b0, c0 - .25, .45), C = win(t, c0, 999, .45);
    show(r.A, A, 0, (1 - A) * 40); show(r.B, B, 0, (1 - B) * 40); show(r.C, C, 0, (1 - C) * 40);
    // A
    const g = .9 + (c[0] > 0 ? 0 : 0);
    r.gen.style.boxShadow = t > g + .7 && t < g + 1 ? '0 0 0 4px rgba(82,139,255,.4)' : 'none';
    r.fp.textContent = t > g + 1 ? 'Xq3v9Lr0dKp2mW8sTj7bYc4fNq1aZu6jG5hVt0wEk9Q'.slice(0, Math.floor((t - g - 1) * 80)) : '';
    r.pk.textContent = typed(r.PK, t, g + 1.2, 70);
    pop(r.copied, t, g + 2.9);
    r.copyk.style.boxShadow = t > g + 2.6 && t < g + 3.2 ? '0 0 0 3px rgba(82,139,255,.5)' : 'none';
    [0, 1, 2].forEach(i => rise(r['id' + i], t, 1.8 + i * .25, .5, 16));
    rise(r.found, t, 2.8, .6, 14);
    // B
    [0, 1, 2, 3].forEach(i => slideX(r['L' + i], t, b0 + .1 + i * .45, .5, 40));
    r.arrow.style.transform = `scaleY(${E.out(P(t, b0 + .2, 1.8))})`; r.arrow.style.transformOrigin = 'top';
    rise(r.eff, t, b0 + 2.1, .6, 20);
    [0, 1, 2, 3, 4].forEach(i => rise(r['e' + i], t, b0 + 2.4 + i * .2, .4, 10));
    // C
    [0, 1, 2].forEach(i => pop(r['n' + i], t, c0 + .1 + i * .35));
    const loop = (t - c0 - 1) % 1.6, on = t > c0 + 1;
    r.pk1.setAttribute('cx', 280 + 100 * clamp(loop / .6)); r.pk1.setAttribute('cy', 210); r.pk1.style.opacity = on && loop < .6 ? 1 : 0;
    r.pk2.setAttribute('cx', 600 + 100 * clamp((loop - .7) / .6)); r.pk2.setAttribute('cy', 210); r.pk2.style.opacity = on && loop > .7 && loop < 1.3 ? 1 : 0;
    rise(r.hops, t, c0 + 1.2, .5, 10);
    [0, 1, 2, 3].forEach(i => slideX(r['t' + i], t, c0 + 1.4 + i * .3, .5, 40));
    cursor([[g - .1, 1400, 500], [g + .7, 1690, 285], [g + 2.2, 1690, 285], [g + 2.6, 1020, 527], [g + 3.6, 1100, 640]], [g + .75, g + 2.65], t, 1 - E.in(P(t, g + 3.6, .3)));
  },
  sfx(d, c) { return [{ t: 1.65, type: 'click' }, { t: 2.1, type: 'type', n: 30, cps: 30 }, { t: 3.55, type: 'click' }, { t: 3.8, type: 'pop' },
    ...[0, 1, 2, 3].map(i => ({ t: c[1] - 1.1 + i * .45, type: 'tick' })), ...[0, 1, 2].map(i => ({ t: c[2] + .6 + i * .35, type: 'pop' }))]; },
});

/* ===================== 8. agents (MCP) ===================== */
const CC = [ // [kind, text]
  ['call', 'list_hosts'], ['res', '6 hosts · web-01 (Ask) · db-primary (Read only) · …'],
  ['call', 'run_command  web-01  systemctl status nginx'], ['err', '✗ failed (Result: exit-code) since 09:41:07'],
  ['call', 'run_command  web-01  nginx -t'], ['err', 'unexpected "}" in /etc/nginx/sites-enabled/app.conf:43'],
  ['call', 'read_file  web-01  /etc/nginx/sites-enabled/app.conf'], ['res', '58 lines'],
  ['call', 'edit_file  web-01  /etc/nginx/sites-enabled/app.conf'], ['wait', '⏳ waiting for your approval in TGK…'],
  ['res', '✓ approved · 1 line changed'],
  ['call', 'run_command  web-01  sudo systemctl reload nginx'], ['res', '✓ approved · sudo password supplied by TGK'],
  ['call', 'run_command  web-01  systemctl is-active nginx'], ['ok', 'active'],
];
scene('agents', {
  plan(d, c, e) {
    const t0 = c[1] + .2, tp = t0 + 1.4, gap = Math.max(.3, (c[3] - .2 - tp) / 10);
    const at = []; for (let i = 0; i < 10; i++) at.push(tp + i * gap);
    const allow = c[3] + 2.7;
    at.push(allow + .2, allow + .7, allow + 1.1, allow + 1.5, allow + 1.9);
    return { t0, at, dlg: c[3] - .05, allow, ans: allow + 2.4 };
  },
  build(el, S) {
    el.innerHTML = `<div class="abs" style="left:120px;top:56px">${head('06', S.kick, S.title).replace('<h1', '<h1 class="sm"')}</div>
      <div class="abs" data-r="cc" style="left:110px;top:240px;width:880px;height:600px;border-radius:14px;overflow:hidden;background:#0f0f12;border:1px solid #2b2f3a;box-shadow:0 40px 120px rgba(0,0,0,.6)">
        <div style="height:40px;display:flex;align-items:center;gap:8px;padding:0 16px;background:#18181d;border-bottom:1px solid #26262e;font:500 14px Inter;color:#9aa3b2">
          <i style="width:12px;height:12px;border-radius:50%;background:#ff5f57"></i><i style="width:12px;height:12px;border-radius:50%;background:#febc2e"></i><i style="width:12px;height:12px;border-radius:50%;background:#28c840"></i>
          <span style="margin-left:14px">claude — ~/infra</span></div>
        <div data-r="ccb" style="padding:18px 22px;font:500 16.5px/1.55 JBM;color:#e6e3dc;white-space:pre-wrap"></div></div>
      <div class="abs" data-r="tg" style="left:1030px;top:240px;width:780px;height:600px">
        <div class="app" style="width:780px;height:600px;position:absolute">
          <div class="top"><div class="brand collapsed">${ic('sidebar', 'width:16px;height:16px;color:#aab2c0')}</div><div class="tabs">${tab('web-01')}${tab('Agent log', { act: true, dot: '' })}</div></div>
          <div class="main full" style="padding:16px 18px">
            <div style="display:flex;align-items:center;gap:10px;font-size:14px"><span class="chip" style="font-size:14px;padding:6px 12px;background:rgba(63,185,80,.12);border-color:rgba(63,185,80,.4)">${ic('agent', 'width:15px;height:15px;color:#56d364')}Claude Code · connected</span>
              <span style="color:#8b93a3">Agent access: <b style="color:#e6e8ee">Ask</b></span></div>
            <div data-r="log" style="margin-top:14px;font:500 13.5px/1.5 JBM"></div></div>
          <div class="status"><span><i class="sd"></i>1 agent connected</span><span>Synced</span></div></div>
        <div class="dlg" data-r="dlg" style="left:40px;top:70px;width:700px;padding:26px 28px">
          <div style="display:flex;align-items:center;gap:12px"><span class="iconbox" style="width:42px;height:42px;background:#3a2d55;color:#c79bff">${ic('agent', 'width:22px;height:22px')}</span>
            <div><h3>Agent request</h3><div class="s" style="margin-top:3px">Claude Code wants to edit a file on <b style="color:#e6e8ee">web-01</b></div></div></div>
          <div style="margin-top:18px;font:500 13px JBM;color:#9aa3b2">/etc/nginx/sites-enabled/app.conf</div>
          <div style="margin-top:8px;border-radius:10px;background:#0e0f14;border:1px solid #2a2d36;padding:10px 0;font:500 15px/1.65 JBM;white-space:pre"><div style="padding:0 14px;color:#8b93a3"> 41      location /api/ {</div><div style="padding:0 14px;background:rgba(229,83,75,.16);color:#ff9b94">-42          proxy_pass http://app_upstream</div><div style="padding:0 14px;background:rgba(63,185,80,.16);color:#8ee59f">+42          proxy_pass http://app_upstream;</div><div style="padding:0 14px;color:#8b93a3"> 43          proxy_set_header Host $host;</div></div>
          <div style="margin-top:14px;font-size:13.5px;color:#d29922;display:flex;gap:8px;align-items:center">${ic('alert', 'width:15px;height:15px')}Access mode Ask: changes need your approval</div>
          <div style="display:flex;gap:10px;margin-top:20px"><span class="btn2 danger">Deny</span><span class="btn2" style="margin-left:auto">Allow for this session</span><span class="btn2 pri" data-r="allow">Allow</span></div></div></div>
      <div class="abs" data-r="modes" style="left:110px;top:880px;width:1700px;height:120px;display:flex;gap:14px">
        ${S.modes.map(([n, s], i) => `<div data-r="m${i}" style="flex:1;border-radius:16px;padding:18px 22px;background:#161922;border:1px solid #2c3140">
          <div style="display:flex;align-items:center;gap:10px"><i style="width:12px;height:12px;border-radius:50%;background:${['#6f7788', '#58a6ff', '#d29922', '#e5534b'][i]}"></i><b style="font-size:22px">${n}</b></div>
          <div style="color:#9aa3b2;font-size:17px;margin-top:8px">${s}</div></div>`).join('')}</div>
      <div class="abs" data-r="shield" style="left:110px;top:880px;width:1700px;height:120px;border-radius:18px;display:flex;align-items:center;justify-content:center;gap:22px;
          background:linear-gradient(90deg,rgba(63,185,80,.14),rgba(82,139,255,.14));border:1px solid rgba(110,200,140,.45)">
        <span class="iconbox" style="background:rgba(63,185,80,.2);color:#56d364">${ic('shield')}</span>
        <div><div style="font:720 32px Inter">${S.shield}</div><div style="color:#b9c2d3;font-size:20px;margin-top:4px">${S.audit}</div></div></div>`;
    const r = refs(el);
    r.S = S; r.cache = {};
    return r;
  },
  render(r, t, d, c, e) {
    animHead(r, t);
    const p = this.plan(d, c, e), S = r.S;
    slideX(r.cc, t, c[0] - .1, .8, -60);
    slideX(r.tg, t, c[0] + .15, .8, 60);
    // claude transcript
    let out = `<span style="color:#d97757">❯</span> ${esc(typed(S.prompt, t, p.t0, 24))}${t < p.t0 + S.prompt.length / 24 + .2 && blink(t) ? '▍' : ''}\n`;
    const shown = [];
    CC.forEach((ln, i) => {
      if (t < p.at[i]) return;
      if (ln[0] === 'wait' && t > p.allow + .15) return;
      shown.push(i);
      const tool = ln[1].split('  ');
      out += ln[0] === 'call' ? `\n<span style="color:#56d364">●</span> <b style="color:#fff">tgk</b> · <span style="color:#d7c9ff">${esc(tool[0])}</span>${tool[1] ? ` <span style="color:#9aa3b2">${esc(tool.slice(1).join('  '))}</span>` : ''}`
        : `\n  <span style="color:#6f7788">⎿</span> <span style="color:${{ res: '#b9c2d3', err: '#ff8b84', wait: '#e6c07b', ok: '#56d364' }[ln[0]]}">${esc(ln[1])}</span>`;
    });
    if (t > p.ans) out += `\n\n<span style="color:#e6e3dc">${esc(typed(S.answer, t, p.ans, 60))}</span>`;
    if (r.cache.cc !== out) { r.cache.cc = out; r.ccb.innerHTML = out; }
    // TGK agent log mirrors calls
    const clock = i => `09:41:${String(8 + i * 2).padStart(2, '0')}`;
    let log = '';
    CC.forEach((ln, i) => {
      if (ln[0] !== 'call' || t < p.at[i] + .25) return;
      const st = i === 8 ? (t > p.allow ? '<span style="color:#56d364">approved</span>' : '<span style="color:#e6c07b">waiting</span>') : i === 11 ? '<span style="color:#56d364">approved · sudo</span>' : '<span style="color:#56d364">ok</span>';
      log += `<div style="display:flex;gap:12px;padding:7px 10px;border-radius:8px;background:#1b1e27;margin-bottom:6px"><span style="color:#6f7788">${clock(i)}</span><span style="color:#d7c9ff">${ln[1].split('  ')[0]}</span><span style="color:#9aa3b2;flex:1;overflow:hidden;white-space:nowrap;text-overflow:ellipsis">${esc(ln[1].split('  ').slice(1).join(' '))}</span>${st}</div>`;
    });
    if (r.cache.log !== log) { r.cache.log = log; r.log.innerHTML = log; }
    // approval dialog
    const dk = win(t, p.dlg, p.allow + .1, .3);
    show(r.dlg, dk, 0, (1 - dk) * 24, lerp(.95, 1, dk));
    r.allow.style.boxShadow = t > p.allow - .4 && t < p.allow + .1 ? '0 0 0 4px rgba(82,139,255,.45)' : 'none';
    // modes, then the shield
    const mIn = E.out(P(t, c[0] + .4, .7)), sh = E.io(P(t, c[4] - .1, .6));
    show(r.modes, mIn * (1 - sh), 0, (1 - mIn) * 30 - sh * 20);
    show(r.shield, sh, 0, (1 - sh) * 30);
    const sel = t < c[2] ? 2 : t < c[2] + .6 ? 1 : t < c[2] + 1.2 ? 2 : t < c[2] + 1.8 ? 3 : 2;
    [0, 1, 2, 3].forEach(i => { const on = i === sel; r['m' + i].style.background = on ? 'rgba(82,139,255,.16)' : '#161922'; r['m' + i].style.borderColor = on ? 'rgba(82,139,255,.7)' : '#2c3140'; });
    // cursor to Allow (dialog at tg + (40,70); allow button near right-bottom)
    const ax = 1030 + 40 + 700 - 60, ay = 240 + 70 + 470;
    cursor([[p.dlg + .6, 1500, 960], [p.allow - .05, ax, ay], [p.allow + .8, ax + 40, ay + 60]], [p.allow], t, 1 - E.in(P(t, p.allow + .6, .3)));
  },
  sfx(d, c, e) {
    const p = this.plan(d, c, e);
    return [{ t: p.t0, type: 'type', n: 30, cps: 24 }, ...p.at.map(t => ({ t, type: 'tick' })), { t: p.dlg, type: 'alert' }, { t: p.allow, type: 'click' }, { t: p.ans, type: 'ding' },
      { t: c[4] - .1, type: 'swish' }];
  },
});

/* ===================== 9. security ===================== */
scene('security', {
  plan(d, c, e) { const s = e[2] - c[2]; return { k: [c[1] - .1, c[2], c[2] + s * .38, c[2] + s * .66, c[3] - .1], banner: c[4] - .1 }; },
  build(el, S) {
    const icons = ['lock', 'refresh', 'key', 'shield', 'server'], col = ['#82c4ff', '#c79bff', '#e6c07b', '#56d364', '#ff9b94'];
    const vis = [
      `<div style="margin-top:26px;height:52px;border-radius:10px;background:#0e0f14;border:1.5px solid #4a6fd0;display:flex;align-items:center;padding:0 16px;font:700 22px JBM;letter-spacing:.08em;color:#cfd6e4;overflow:hidden" data-r="dots"></div>`,
      `<div style="margin-top:22px;font:800 46px JBM;color:#fff;letter-spacing:-.02em" data-r="iter">0</div><div style="color:#8b93a3;font-size:16px">${S.iters}</div>`,
      `<div style="margin-top:22px;display:flex;align-items:center;gap:12px"><span style="font:600 16px JBM;color:#e6c07b;padding:8px 12px;border-radius:9px;background:rgba(230,192,123,.1);border:1px solid rgba(230,192,123,.35)" data-r="vk">4f9a…c21e</span>${ic('lock', 'width:26px;height:26px;color:#e6c07b')}</div>`,
      `<div style="margin-top:20px;display:grid;grid-template-columns:1fr 1fr;gap:8px">${['web-01', 'Production', 'deploy', 'known host'].map((x, i) => `<span data-r="it${i}" style="display:flex;gap:7px;align-items:center;font:600 14px Inter;padding:8px 10px;border-radius:9px;background:#1b2a20;border:1px solid #2f6b3d;color:#c9f0d2">${ic('lock', 'width:13px;height:13px')}${x}</span>`).join('')}</div>`,
      `<div data-r="hex" style="margin-top:20px;font:500 14px/1.5 JBM;color:#6f7788;height:84px;overflow:hidden;word-break:break-all"></div>`,
    ];
    el.innerHTML = `<div class="abs" style="left:120px;top:70px">${head('07', S.kick, S.title)}</div>
      ${S.cards.map(([tt, dd], i) => `<div class="panel" data-r="k${i}" style="left:${120 + i * 345}px;top:330px;width:300px;height:380px;padding:28px 24px">
        <div style="display:flex;align-items:center;justify-content:space-between"><span class="iconbox" style="background:${col[i]}22;color:${col[i]}">${ic(icons[i])}</span><span style="font:700 15px JBM;color:#5b6273">0${i + 1}</span></div>
        <div style="font:740 26px Inter;margin-top:22px;letter-spacing:-.01em">${tt}</div><div style="font-size:17px;line-height:1.45;color:#a3abbb;margin-top:10px">${dd}</div>${vis[i]}</div>`).join('')}
      ${[0, 1, 2, 3].map(i => `<div class="abs" data-r="a${i}" style="left:${120 + i * 345 + 302}px;top:511px;width:41px;height:18px;display:grid;place-items:center;color:#528bff">${ic('chevron-right', 'width:22px;height:22px')}</div>`).join('')}
      <div class="abs" data-r="dev" style="left:104px;top:300px;width:1050px;height:430px;border:1.5px dashed rgba(130,170,255,.35);border-radius:24px"></div>
      <div class="abs" data-r="devl" style="left:130px;top:288px;padding:0 10px;background:#0b0d12;font:700 14px Inter;letter-spacing:.16em;color:#8fb4ff">THIS DEVICE</div>
      <div class="abs" data-r="banner" style="left:120px;top:790px;width:1680px;height:150px;border-radius:20px;display:flex;align-items:center;gap:28px;padding:0 40px;
          background:linear-gradient(90deg,rgba(82,139,255,.18),rgba(155,123,255,.12));border:1px solid rgba(130,150,255,.45)">
        <span class="iconbox" style="width:76px;height:76px;background:rgba(82,139,255,.22);color:#9fc0ff">${ic('shield', 'width:38px;height:38px')}</span>
        <div><div style="font:760 40px Inter;letter-spacing:-.015em">${S.banner}</div><div style="font-size:21px;color:#b9c2d3;margin-top:6px">${S.sub}</div></div></div>`;
    return refs(el);
  },
  render(r, t, d, c, e) {
    animHead(r, t);
    const p = this.plan(d, c, e);
    [0, 1, 2, 3, 4].forEach(i => rise(r['k' + i], t, p.k[i], .7, 40));
    [0, 1, 2, 3].forEach(i => { const q = E.out(P(t, p.k[i + 1] - .1, .4)); show(r['a' + i], q, (1 - q) * -10, 0); });
    r.dots.textContent = '•'.repeat(Math.min(12, Math.floor(Math.max(0, t - p.k[0] - .3) * 14)));
    r.iter.textContent = fmt(Math.round(600000 * E.out(P(t, p.k[1] + .2, 1.6))));
    r.vk.style.opacity = .4 + .6 * E.out(P(t, p.k[2] + .3, .5));
    [0, 1, 2, 3].forEach(i => pop(r['it' + i], t, p.k[3] + .3 + i * .18, .4));
    const R = rng(Math.floor(t * 8));
    let h = ''; for (let i = 0; i < 96; i++) h += '0123456789abcdef'[Math.floor(R() * 16)];
    r.hex.textContent = h;
    const dv = E.out(P(t, p.k[0] + .2, .8));
    r.dev.style.opacity = dv * .9; r.devl.style.opacity = dv;
    rise(r.banner, t, p.banner, .7, 30);
  },
  sfx(d, c, e) { const p = this.plan(d, c, e); return [...p.k.map(t => ({ t, type: 'pop' })), { t: p.banner, type: 'impact' }]; },
});

/* ===================== 10. sync ===================== */
scene('sync', {
  build(el, S) {
    const dev = (n, i, x, y) => `<div class="node" data-r="d${i}" style="left:${x}px;top:${y}px;width:300px;display:flex;align-items:center;gap:16px">
        <span class="iconbox" style="background:#26324d;color:#82c4ff">${ic('monitor')}</span><div><b style="font-size:20px">${n}</b><span data-r="ds${i}" style="font-size:15px"></span></div></div>`;
    el.innerHTML = `<div class="abs" style="left:120px;top:70px">${head('08', S.kick, S.title)}</div>
      <div class="abs" data-r="badges" style="left:1340px;top:110px;display:flex;flex-direction:column;gap:12px;align-items:flex-start">
        ${S.badges.map((b, i) => `<span class="chip" data-r="b${i}">${ic(['lock', 'monitor', 'logout', 'refresh'][i], 'width:17px;height:17px;color:#8fb4ff')}${b}</span>`).join('')}</div>
      <div data-r="net" class="abs" style="inset:0">
        <svg class="wires"><g stroke="#2f3646" stroke-width="3" fill="none"><path d="M560 560 L760 500"/><path d="M1360 560 L1160 500"/><path d="M960 760 L960 570"/></g>
          ${[0, 1, 2].map(i => `<circle data-r="w${i}" r="7" fill="#82c4ff"/>`).join('')}</svg>
        <div class="node" data-r="srv" style="left:760px;top:420px;width:400px;text-align:center;border-color:#4a66c0;background:linear-gradient(180deg,#1f2742,#171b2a)">
          <span class="iconbox" style="margin:0 auto 10px;background:#2c3a66;color:#9fc0ff">${ic('server')}</span><b>tgk-server</b><span>${S.server} · HTTPS</span></div>
        ${dev(S.dev[0], 0, 260, 500)}${dev(S.dev[1], 1, 1360, 500)}${dev(S.dev[2], 2, 810, 770)}</div>
      <div data-r="loc" class="abs" style="left:560px;top:330px;width:800px">
        <div style="display:flex;justify-content:center;margin-bottom:26px"><div style="display:flex;padding:5px;border-radius:12px;background:#0e0f14;border:1px solid #2a2d36;position:relative">
          <span data-r="seg" style="position:absolute;top:5px;bottom:5px;width:220px;border-radius:9px;background:#2a2f3c;border:1px solid #3a4152"></span>
          <span style="position:relative;width:220px;text-align:center;padding:10px 0;font:600 17px Inter">Server account</span><span style="position:relative;width:220px;text-align:center;padding:10px 0;font:600 17px Inter">This device only</span></div></div>
        <div class="dlg" style="position:relative;width:520px;margin:0 auto;padding:30px 32px;text-align:left">
          <div style="text-align:center;font:760 24px Inter">TGK</div>
          <div style="color:#c9cfdb;font-size:15px;line-height:1.5;margin-top:16px">Your hosts and keys stay on this computer, encrypted with this password.</div>
          <div style="font:650 14px Inter;margin-top:18px;color:#c9cfdb">Master password</div>
          <div style="margin-top:8px;height:44px;border-radius:9px;border:1.5px solid #4a6fd0;background:#0e0f14;display:flex;align-items:center;padding:0 14px;font:700 20px JBM;letter-spacing:.2em" data-r="mp"></div>
          <div style="display:flex;gap:10px;align-items:center;margin-top:16px;font-size:15px;color:#c9cfdb"><span style="width:18px;height:18px;border-radius:4px;background:#528bff;display:grid;place-items:center">${ic('check', 'width:12px;height:12px;color:#fff')}</span>Keep unlocked on this device</div>
          <div class="btn2 pri" style="width:100%;margin-top:20px;height:46px;font-size:16px">Create local vault</div></div>
        <div style="text-align:center;margin-top:22px;font-size:20px;color:#a3abbb" data-r="lsub">${S.localSub}</div></div>
      <div data-r="sw" class="abs" style="left:1420px;top:420px;display:flex;flex-direction:column;gap:14px">
        ${S.sw.map((x, i) => `<span class="chip" data-r="s${i}" style="font-size:18px">${ic(['upload', 'download', 'save', 'refresh'][i], 'width:17px;height:17px;color:#8fb4ff')}${x}</span>`).join('')}</div>`;
    const r = refs(el);
    r.S = S;
    return r;
  },
  render(r, t, d, c) {
    animHead(r, t);
    const S = r.S;
    const net = win(t, .3, c[1] - .3, .5), loc = win(t, c[1] - .1, 999, .5);
    show(r.net, net, 0, 0, lerp(.97, 1, net));
    pop(r.srv, t, .4); [0, 1, 2].forEach(i => pop(r['d' + i], t, .8 + i * .25));
    [0, 1, 2, 3].forEach(i => slideX(r['b' + i], t, 1 + i * .35, .5, 30));
    r.badges.style.opacity = 1 - E.in(P(t, c[1] - .3, .4));
    const paths = [[560, 560, 760, 500], [1360, 560, 1160, 500], [960, 760, 960, 570]];
    paths.forEach(([x0, y0, x1, y1], i) => {
      const k = ((t * .7 + i * .33) % 1), back = Math.floor(t * .7 + i * .33) % 2;
      const q = back ? 1 - k : k;
      r['w' + i].setAttribute('cx', lerp(x0, x1, q)); r['w' + i].setAttribute('cy', lerp(y0, y1, q));
      r['w' + i].style.opacity = t > 1.6 ? Math.sin(k * Math.PI) : 0;
      r['ds' + i].innerHTML = Math.sin(k * Math.PI) > .6 && t > 1.6 ? `<span style="color:#e6c07b">${S.syncing}</span>` : `<span style="color:#56d364">● ${S.synced}</span>`;
    });
    show(r.loc, loc, 0, (1 - loc) * 30);
    r.mp.textContent = '•'.repeat(Math.min(14, Math.floor(Math.max(0, t - c[1] - .4) * 12)));
    const segK = t < c[2] ? 1 : E.io(clamp(.5 - .5 * Math.cos((t - c[2]) * 2.2)));
    r.seg.style.left = lerp(5, 225, t < c[2] ? 1 : 1 - segK) + 'px';
    [0, 1, 2, 3].forEach(i => slideX(r['s' + i], t, c[2] + .2 + i * .35, .5, 40));
    r.loc.style.left = lerp(560, 360, E.io(P(t, c[2] - .1, .7))) + 'px';
  },
  sfx(d, c) { return [{ t: .4, type: 'pop' }, { t: c[1] - .1, type: 'swish' }, { t: c[2], type: 'swish' }, ...[0, 1, 2, 3].map(i => ({ t: c[2] + .2 + i * .35, type: 'tick' }))]; },
});

/* ===================== 11. platforms & updates ===================== */
scene('platforms', {
  plan(d, c) { return { click: c[1] + 1.2, dl: c[1] + 1.3, ver: c[1] + 3.1, done: c[1] + 3.9 }; },
  build(el, S) {
    const tiles = [['Windows', 'TGK-win-x64-setup.exe'], ['Linux', 'TGK-x86_64.AppImage'], ['macOS', 'TGK-osx-arm64.dmg']];
    el.innerHTML = `<div class="abs" style="left:120px;top:70px">${head('09', S.kick, S.title)}</div>
      ${tiles.map(([n, f], i) => `<div class="panel" data-r="p${i}" style="left:${120 + i * 575}px;top:320px;width:530px;height:290px;padding:34px 36px">
        <span class="iconbox" style="background:#26324d;color:#82c4ff">${ic('monitor')}</span><div style="font:780 46px Inter;margin-top:22px;letter-spacing:-.02em">${n}</div>
        <div style="font:500 18px JBM;color:#9fc0ff;margin-top:10px">${f}</div><div style="color:#a3abbb;font-size:18px;margin-top:10px">${S.notes[i]}</div></div>`).join('')}
      <div class="abs" data-r="bar" style="left:120px;top:680px;width:1680px;height:190px;border-radius:18px;overflow:hidden;border:1px solid #2b2f3a;background:#17191f;box-shadow:0 40px 100px rgba(0,0,0,.5)">
        <div style="position:absolute;left:0;right:0;bottom:0;height:64px;background:#0d0e12;display:flex;align-items:center;gap:18px;padding:0 26px;font-size:19px;color:#c9cfdb" data-r="sb"></div>
        <div style="position:absolute;left:26px;top:30px;font:700 15px Inter;letter-spacing:.14em;color:#8b93a3;text-transform:uppercase">${S.upd}</div>
        <div style="position:absolute;right:26px;top:22px;display:flex;gap:8px"><span class="chip on" style="font-size:15px;padding:6px 14px">Stable</span><span class="chip" style="font-size:15px;padding:6px 14px">Nightly</span></div>
        <div style="position:absolute;left:26px;right:26px;top:74px;height:6px;border-radius:3px;background:#262a35;overflow:hidden"><div data-r="prog" style="height:100%;width:0;background:linear-gradient(90deg,#528bff,#9b7bff)"></div></div></div>`;
    return refs(el);
  },
  render(r, t, d, c) {
    animHead(r, t);
    const p = this.plan(d, c);
    [0, 1, 2].forEach(i => rise(r['p' + i], t, c[0] + i * .35, .7, 40));
    rise(r.bar, t, c[1] - .1, .7, 30);
    const lnk = s => `<span style="color:#82c4ff;font-weight:650">${s}</span>`;
    let sb;
    if (t < p.dl) sb = `<span style="width:9px;height:9px;border-radius:50%;background:#528bff"></span><b>TGK 0.4.0 is available</b><span data-r="un" style="padding:6px 14px;border-radius:8px;background:${t > p.click - .3 ? '#528bff' : 'rgba(82,139,255,.15)'};color:#fff;font-weight:650">Update now</span>${lnk('Download from GitHub')}<span style="color:#8b93a3">Skip this version</span><span style="color:#8b93a3">Remind me later</span>`;
    else if (t < p.ver) sb = `<span style="color:#9fc0ff">Downloading TGK 0.4.0… ${Math.floor(100 * clamp((t - p.dl) / (p.ver - p.dl - .2)))}%</span>`;
    else if (t < p.done) sb = `<span style="color:#e6c07b">Verifying SHA-256 against GitHub’s checksum…</span>`;
    else sb = `${ic('check', 'width:20px;height:20px;color:#56d364')}<b style="color:#c9f0d2">Verified · TGK 0.4.0 installs when you restart</b><span style="margin-left:auto;color:#56d364;font:500 16px JBM">sha256 ✓ 9f2c…e41a</span>`;
    if (r.sb.innerHTML.length !== sb.length) r.sb.innerHTML = sb;
    r.prog.style.width = 100 * clamp((t - p.dl) / (p.ver - p.dl - .2)) + '%';
    cursor([[p.click - 1, 900, 700], [p.click, 590, 1000 - 70 - 32 + 14], [p.click + .7, 700, 960]], [p.click], t, 1 - E.in(P(t, p.click + .5, .3)));
  },
  sfx(d, c) { const p = this.plan(d, c); return [0, 1, 2].map(i => ({ t: c[0] + i * .35, type: 'pop' })).concat([{ t: p.click, type: 'click' }, { t: p.done, type: 'ding' }]); },
});

/* ===================== 12. benefits ===================== */
scene('benefits', {
  build(el, S) {
    const icons = ['server', 'user', 'eye', 'lock', 'agent', 'clipboard'];
    el.innerHTML = `<div class="abs" style="left:0;right:0;top:80px;text-align:center"><div class="kick" data-r="kick" style="justify-content:center"><span class="num">10</span><span class="bar"></span><span>${S.kick}</span></div>
        <h1 data-r="h1" style="margin-top:22px;font-size:66px">${S.title}</h1></div>
      ${S.tiles.map(([tt, dd], i) => `<div class="panel" data-r="t${i}" style="left:${130 + (i % 3) * 563}px;top:${380 + Math.floor(i / 3) * 290}px;width:533px;height:260px;padding:32px 34px">
        <span class="iconbox" style="background:rgba(82,139,255,.16);color:#9fc0ff">${ic(icons[i])}</span>
        <div style="font:740 29px Inter;margin-top:20px;letter-spacing:-.01em">${tt}</div><div style="font-size:19px;line-height:1.45;color:#a3abbb;margin-top:10px">${dd}</div></div>`).join('')}`;
    return refs(el);
  },
  render(r, t, d, c, e) {
    animHead(r, t);
    const span = Math.max(4, e[0] - c[0] - 1.5);
    for (let i = 0; i < 6; i++) rise(r['t' + i], t, c[0] + 1 + span * i / 6, .7, 40);
  },
  sfx(d, c, e) { const span = Math.max(4, e[0] - c[0] - 1.5); return [0, 1, 2, 3, 4, 5].map(i => ({ t: c[0] + 1 + span * i / 6, type: 'pop' })); },
});

/* ===================== 13. community ===================== */
scene('community', {
  build(el, S) {
    el.innerHTML = `<div class="abs" style="left:120px;top:180px;width:700px">${head('11', S.kick, S.title)}
        <div style="margin-top:44px">${bullet(S.l1, 'l1')}${bullet(S.l2, 'l2')}</div>
        <div data-r="url" style="margin-top:30px;font:600 22px JBM;color:#9fc0ff">github.com/AlexandruMindra/TGK</div></div>
      <div class="panel" data-r="pr" style="left:900px;top:170px;width:900px;height:230px;padding:28px 32px">
        <div style="display:flex;align-items:center;gap:12px"><span data-r="prs" class="chip" style="font-size:15px;padding:6px 14px"></span><span style="color:#8b93a3;font-size:16px">Pull request</span>
          <span class="chip" style="margin-left:auto;font-size:13px;padding:4px 10px;color:#8b93a3">${S.example}</span></div>
        <div style="font:720 30px Inter;margin-top:16px">Add Telnet hosts for legacy network gear</div>
        <div style="color:#8b93a3;font-size:17px;margin-top:8px">6 commits · +412 −38 · CHANGELOG.md updated</div>
        <div style="display:flex;gap:12px;margin-top:20px">${['build · windows', 'build · linux', 'build · macOS', 'tests'].map((x, i) => `<span class="chip" data-r="ck${i}" style="font-size:15px;padding:6px 12px">${ic('check', 'width:14px;height:14px;color:#56d364')}${x}</span>`).join('')}</div></div>
      <div class="panel" data-r="is" style="left:900px;top:430px;width:900px;height:280px;padding:28px 32px">
        <div style="display:flex;align-items:center;gap:12px"><span class="chip" style="font-size:15px;padding:6px 14px;background:rgba(63,185,80,.14);border-color:rgba(63,185,80,.5);color:#8ee59f">Feature request</span>
          <span class="chip" style="font-size:15px;padding:6px 14px;background:rgba(163,113,247,.14);border-color:rgba(163,113,247,.5);color:#d6b8ff">enhancement</span>
          <span data-r="plan" class="chip" style="font-size:15px;padding:6px 14px;background:rgba(82,139,255,.18);border-color:rgba(82,139,255,.6)">Planned</span>
          <span class="chip" style="margin-left:auto;font-size:13px;padding:4px 10px;color:#8b93a3">${S.example}</span></div>
        <div style="font:720 30px Inter;margin-top:16px">Import hosts from an Ansible inventory</div>
        <div style="color:#a3abbb;font-size:18px;margin-top:10px">“We keep 300+ hosts in inventory files. Importing them with groups would save us days.”</div>
        <div style="display:flex;align-items:center;gap:16px;margin-top:24px"><span style="font:800 40px Inter" data-r="votes">👍 0</span><span style="color:#8b93a3;font-size:18px">${S.votes}</span>
          <div data-r="av" style="display:flex;margin-left:auto"></div></div></div>
      <div class="abs" data-r="pipe" style="left:120px;top:850px;width:1680px;height:150px">
        <div style="position:absolute;left:70px;right:70px;top:38px;height:3px;background:#2a2f3c"></div><div data-r="pfill" style="position:absolute;left:70px;top:38px;height:3px;background:linear-gradient(90deg,#528bff,#9b7bff)"></div>
        ${S.steps.map((s, i) => `<div data-r="s${i}" style="position:absolute;left:${70 + i * 308 - 70}px;top:0;width:140px;text-align:center">
          <div style="width:78px;height:78px;margin:0 auto;border-radius:50%;display:grid;place-items:center;background:#161922;border:2px solid #2f3646;color:#8b93a3">${ic(['bolt', 'file-plus', 'route', 'check', 'clock', 'download'][i], 'width:30px;height:30px')}</div>
          <div style="font:650 19px Inter;margin-top:12px;color:#dfe4ee">${s}</div><div style="font:500 13px JBM;color:#6f7788;margin-top:3px">${['', '#', 'PR', 'main', 'X.Y.Z-nightly.N', 'vX.Y.Z'][i]}</div></div>`).join('')}</div>`;
    return refs(el);
  },
  render(r, t, d, c) {
    animHead(r, t);
    rise(r.l1, t, c[1], .6, 16); rise(r.l2, t, c[2], .6, 16); rise(r.url, t, c[2] + .8, .6, 10);
    rise(r.pr, t, c[1] - .1, .7, 40);
    const merged = t > c[1] + 3.2;
    r.prs.innerHTML = merged ? `${ic('check', 'width:14px;height:14px')}Merged` : 'Open';
    Object.assign(r.prs.style, merged ? { background: 'rgba(163,113,247,.2)', borderColor: 'rgba(163,113,247,.6)', color: '#e2ccff' } : { background: 'rgba(63,185,80,.14)', borderColor: 'rgba(63,185,80,.5)', color: '#8ee59f' });
    [0, 1, 2, 3].forEach(i => pop(r['ck' + i], t, c[1] + 1 + i * .4, .4));
    rise(r.is, t, c[2] - .1, .7, 40);
    const v = Math.round(148 * E.out(P(t, c[2] + .5, 3.2)));
    r.votes.textContent = '👍 ' + v;
    const n = Math.min(9, Math.floor(v / 15));
    if (r.av.children.length !== n) r.av.innerHTML = Array.from({ length: n }, (_, i) => `<span style="width:40px;height:40px;border-radius:50%;margin-left:-10px;border:3px solid #1c1f28;background:hsl(${(i * 47) % 360} 55% 55%);display:grid;place-items:center;font:700 15px Inter;color:#fff">${'AMRDKSLTV'[i]}</span>`).join('');
    pop(r.plan, t, c[2] + 3.6);
    rise(r.pipe, t, c[3] - .2, .6, 20);
    const k = E.io(P(t, c[3] + .2, 2.4));
    r.pfill.style.width = 1540 * k + 'px';
    for (let i = 0; i < 6; i++) {
      const on = k * 5 >= i - .01, c0 = r['s' + i].firstElementChild;
      c0.style.borderColor = on ? '#528bff' : '#2f3646'; c0.style.color = on ? '#fff' : '#8b93a3'; c0.style.background = on ? '#1f2a48' : '#161922';
      c0.style.boxShadow = on && k * 5 < i + .6 ? '0 0 0 8px rgba(82,139,255,.18)' : 'none';
    }
  },
  sfx(d, c) { return [{ t: c[1] - .1, type: 'pop' }, { t: c[1] + 3.2, type: 'ding' }, { t: c[2] - .1, type: 'pop' }, ...[0, 1, 2, 3, 4, 5].map(i => ({ t: c[3] + .2 + 2.4 * i / 5, type: 'tick' }))]; },
});

/* ===================== 14. outro ===================== */
scene('outro', {
  noFadeOut: true,
  build(el, S) {
    el.innerHTML = `<div class="abs" data-r="glow" style="left:460px;top:-120px;width:1000px;height:1000px;border-radius:50%;background:radial-gradient(circle,rgba(82,139,255,.32),transparent 60%)"></div>
      <div class="abs" data-r="grp" style="left:0;top:0;width:1920px;height:1080px">
        <div class="abs" data-r="icon" style="left:663px;top:250px;width:170px;height:170px;border-radius:40px;background:linear-gradient(135deg,#72adff,#3d74e3);box-shadow:0 30px 90px rgba(60,120,255,.5),inset 0 2px 0 rgba(255,255,255,.35)">
          <svg viewBox="0 0 100 100" width="170" height="170"><path d="M28 31 L47 50 L28 69" fill="none" stroke="#fff" stroke-width="9" stroke-linecap="round" stroke-linejoin="round"/><path data-r="und" d="M55 69 H75" stroke="#fff" stroke-width="9" stroke-linecap="round"/></svg></div>
        <div class="abs" data-r="word" style="left:873px;top:243px;font:820 172px/1 Inter;letter-spacing:-.045em">TGK</div>
        <div class="abs" data-r="tag" style="left:0;width:1920px;top:480px;text-align:center;font:700 60px Inter;letter-spacing:-.02em">${S.tag}</div>
        <div class="abs" data-r="cta" style="left:0;width:1920px;top:600px;display:flex;justify-content:center;gap:16px">
          ${['Windows', 'Linux', 'macOS'].map(x => `<span class="chip" style="font-size:24px;padding:12px 26px">${ic('download', 'width:20px;height:20px;color:#8fb4ff')}${x}</span>`).join('')}</div>
        <div class="abs" data-r="url" style="left:0;width:1920px;top:710px;text-align:center"><span style="display:inline-flex;gap:14px;align-items:center;padding:16px 30px;border-radius:16px;background:#528bff;font:700 30px JBM;color:#fff;box-shadow:0 20px 60px rgba(82,139,255,.45)">${ic('link', 'width:26px;height:26px')}github.com/AlexandruMindra/TGK</span></div>
        <div class="abs" data-r="free" style="left:0;width:1920px;top:820px;text-align:center;font:500 24px Inter;color:#9aa3b2;letter-spacing:.06em">${S.free}</div></div>`;
    return refs(el);
  },
  render(r, t, d, c, e) {
    const ip = P(t, .1, .8);
    show(r.icon, clamp(ip * 3), 0, 0, lerp(.5, 1, E.back(ip)));
    r.word.style.clipPath = `inset(0 ${100 - 100 * E.out(P(t, .35, .8))}% 0 0)`;
    r.und.style.opacity = blink(t);
    rise(r.tag, t, c[0] + .6, .8);
    rise(r.cta, t, c[1] - .1, .7, 20);
    rise(r.url, t, c[1] + .5, .7, 20);
    rise(r.free, t, c[1] + 1.1, .7, 14);
    // end screen: move the block to the left third, leaving room for YouTube's end-screen elements
    const m = E.io(P(t, e[1] + 1.2, 1.2));
    r.grp.style.transform = `translate(${-470 * m}px, ${-20 * m}px) scale(${lerp(1, .78, m)})`;
    r.grp.style.transformOrigin = '960px 540px';
    r.glow.style.transform = `translateX(${-470 * m}px)`;
  },
  sfx(d, c) { return [{ t: .1, type: 'impact' }, { t: c[1] + .5, type: 'pop' }]; },
});
