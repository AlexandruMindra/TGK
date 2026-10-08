// YouTube thumbnail (render.mjs <lang> thumb): rendered at 1920x1080, scaled to 1280x720 by the build.
window.thumb = () => {
  const lang = TL.lang;
  const T = { en: ['Every server.', 'One vault.', ['SSH', 'SFTP', 'AI agents', 'E2E encrypted']], ro: ['Toate serverele.', 'Un singur seif.', ['SSH', 'SFTP', 'Agenți AI', 'Criptat E2E']] }[lang];
  document.querySelectorAll('.scene,#cursor,#ripple').forEach(e => (e.style.display = 'none'));
  document.getElementById('fade').style.opacity = 0;
  const tabs = ['web-01', 'db-primary', 'staging-web', 'vps'].map((n, i) => `<div class="tab${i ? '' : ' act'}" style="width:170px"><i class="d"></i><span class="tn">${n}</span></div>`).join('');
  const el = html(`<div class="scene" style="display:block">
    <div class="abs" style="left:-200px;top:-300px;width:1400px;height:1400px;border-radius:50%;background:radial-gradient(circle,rgba(82,139,255,.35),transparent 60%)"></div>
    <div class="abs" style="left:900px;top:150px;transform:perspective(1800px) rotateY(-16deg) rotateX(4deg) scale(.95);transform-origin:0 50%">${appShell({ side: false, tabs, status: '4 sessions' })}</div>
    <div class="abs" style="inset:0;background:linear-gradient(90deg,rgba(7,8,12,.92) 0%,rgba(7,8,12,.75) 40%,transparent 62%)"></div>
    <div class="abs" style="left:90px;top:120px;display:flex;align-items:center;gap:26px">
      <div style="width:130px;height:130px;border-radius:32px;background:linear-gradient(135deg,#72adff,#3d74e3);box-shadow:0 20px 70px rgba(60,120,255,.6),inset 0 2px 0 rgba(255,255,255,.35)">
        <svg viewBox="0 0 100 100" width="130" height="130"><path d="M28 31 L47 50 L28 69" fill="none" stroke="#fff" stroke-width="9" stroke-linecap="round" stroke-linejoin="round"/><path d="M55 69 H75" stroke="#fff" stroke-width="9" stroke-linecap="round"/></svg></div>
      <div style="font:850 130px/1 Inter;letter-spacing:-.045em">TGK</div></div>
    <div class="abs" style="left:84px;top:340px;font:850 ${lang === 'ro' ? 118 : 150}px/1.0 Inter;letter-spacing:-.045em"><span style="text-shadow:0 10px 50px rgba(0,0,0,.7)">${T[0]}</span><br><em style="background-image:linear-gradient(92deg,#7cb2ff 0%,#a49bff 50%,#d99bff 100%)">${T[1]}</em></div>
    <div class="abs" style="left:90px;top:720px;display:flex;flex-wrap:wrap;gap:16px;width:900px">${T[2].map(x => `<span class="chip" style="font-size:34px;padding:14px 28px;font-weight:700;background:rgba(20,24,36,.85)">${x}</span>`).join('')}</div>
  </div>`);
  document.getElementById('stage').appendChild(el);
  const main = el.querySelector('[data-r="main"]');
  [[0, 0], [1, 0], [0, 1], [1, 1]].forEach(([x, y], i) => {
    const p = html(`<div class="abs" style="left:${6 + x * 637}px;top:${6 + y * 362}px;width:631px;height:356px;border-radius:8px;overflow:hidden;background:#0f1015;border:1px solid ${i ? '#2a2d36' : '#528bff'}">
      <div class="term" style="font-size:13px;line-height:16px"></div></div>`);
    const tm = p.querySelector('.term');
    applyScheme(tm, [0, 1, 9, 4][i]);
    tm.innerHTML = termHTML(paneText([0, 1, 2, 5][i], 6), false);
    main.appendChild(p);
  });
};
