// A faithful HTML rebuild of the TGK window (compare docs/images/*.png), at the screenshots' 1280 x 800.
const GROUPS = [
  ['PRODUCTION', [['api-gateway', 'deploy@api.prod.example.com:2222', '#a371f7'], ['db-primary', 'postgres@10.0.1.20', '#e5534b'], ['web-01', 'deploy@web-01.prod.example.com', '#e5534b']]],
  ['STAGING', [['staging-db', 'admin@10.0.2.15', '#d29922'], ['staging-web', 'deploy@staging.example.com', '#d29922']]],
  ['PERSONAL', [['homelab', 'pi@192.168.1.10', '#3fb950'], ['vps', 'root@vps.example.net', '#58a6ff']]],
  ['UNGROUPED', [['localhost', '127.0.0.1', '#8b949e']]],
];
const HOSTC = Object.fromEntries(GROUPS.flatMap(g => g[1].map(h => [h[0], h[2]])));
const GROUPOF = Object.fromEntries(GROUPS.flatMap(g => g[1].map(h => [h[0], g[0][0] + g[0].slice(1).toLowerCase()])));
const ADDR = Object.fromEntries(GROUPS.flatMap(g => g[1].map(h => [h[0], h[1]])));

function sidebar(sel) {
  let s = `<div class="side"><div class="search">${ic('search', 'width:14px;height:14px')}Search hosts</div><div class="groups">`;
  for (const [g, hs] of GROUPS) {
    s += `<div class="grp">${ic('chevron-down', 'width:12px;height:12px')}${g}<span class="n">${hs.length}</span></div>`;
    for (const [n, a, c] of hs) s += `<div class="host${n === sel ? ' sel' : ''}"><i class="dot" style="background:${c}"></i><b>${n}</b><span>${a}</span></div>`;
  }
  return s + `</div><div class="sidefoot"><span>${ic('plus', 'width:14px;height:14px')}New host</span><span>${ic('key', 'width:14px;height:14px')}Keys</span></div></div>`;
}
const tab = (name, { act = false, dot = 'g', key = '' } = {}) =>
  `<div class="tab${act ? ' act' : ''}" ${key ? `data-r="${key}"` : ''}>${dot ? `<i class="d${dot === 'y' ? ' y' : ''}"></i>` : ''}<span class="tn">${name}</span>${act ? ic('x', 'width:11px;height:11px;margin-left:auto;color:#7d8594') : ''}</div>`;

function appShell({ side = true, sel = '', tabs = '', status = '8 saved hosts', right = '', main = '' } = {}) {
  return `<div class="app">
    <div class="top">
      <div class="brand${side ? '' : ' collapsed'}">${ic('sidebar', 'width:16px;height:16px;color:#aab2c0')}${side ? `<span class="logo">${ic('terminal')}</span><b>TGK</b>` : ''}</div>
      <div class="tabs" data-r="tabs">${tabs}<div class="plus" data-r="plus">${ic('plus', 'width:15px;height:15px')}</div></div>
      <div class="right">${right}<span><i class="sd"></i>Synced</span><span class="avatar">D</span></div>
    </div>
    ${side ? sidebar(sel) : ''}
    <div class="main${side ? '' : ' full'}" data-r="main">${main}</div>
    <div class="status"><span data-r="statusL">${status}</span><span data-r="statusR">Synced</span></div>
  </div>`;
}

function hostCard(n, extra) {
  const c = HOSTC[n];
  return `<div class="card"><div class="ico" style="background:${c}26;color:${c}">${ic('server', 'width:18px;height:18px')}</div>
    <b>${n}</b><div class="t">${extra || GROUPOF[n]}</div><span>${ADDR[n]}</span></div>`;
}
function homeView() {
  const all = ['api-gateway', 'db-primary', 'homelab', 'localhost', 'staging-db', 'staging-web', 'vps', 'web-01'];
  return `<div class="home" data-r="home">
    <h2>Connect to a server</h2><div class="sub">Type an address or pick one of your saved hosts.</div>
    <div class="qc"><div class="inp" data-r="inp">${ic('terminal', 'width:16px;height:16px;color:#9aa3b2')}<span data-r="typed"></span><span class="caret" data-r="caret"></span><span class="ph" data-r="ph">user@host[:port]</span>
      <div class="sugg" data-r="sugg"></div></div>
      <div class="btn">${ic('bolt', 'width:15px;height:15px')}Connect</div></div>
    <div class="hint">Press Enter to connect &nbsp;·&nbsp; Ctrl+L to focus &nbsp;·&nbsp; Ctrl+T for a new tab</div>
    <div class="lbl">RECENT</div>
    <div class="cards">${hostCard('staging-web', 'just now')}${hostCard('homelab', '40m ago')}${hostCard('db-primary', '2h ago')}</div>
    <div class="lbl">ALL HOSTS</div>
    <div class="cards">${all.map(n => hostCard(n)).join('')}</div>
  </div>`;
}

// Terminal colour schemes (src/TGK.Client/Terminal/ColorScheme.cs)
const SCHEMES = [
  ['TGK Dark', ['#2A2D37', '#E8646A', '#8FCB7B', '#E6C07B', '#3F83DE', '#C792EA', '#3FB4C2', '#CDD3DE', '#5E6575', '#FF7E84', '#AEE58F', '#FFD68A', '#82C4FF', '#DDAAFF', '#7CDCE6', '#F5F7FA'], '#D8DCE4', '#0F1015', '#E8EBF1'],
  ['Dracula', ['#21222C', '#FF5555', '#50FA7B', '#F1FA8C', '#BD93F9', '#FF79C6', '#8BE9FD', '#F8F8F2', '#6272A4', '#FF6E6E', '#69FF94', '#FFFFA5', '#D6ACFF', '#FF92DF', '#A4FFFF', '#FFFFFF'], '#F8F8F2', '#282A36', '#F8F8F2'],
  ['Nord', ['#3B4252', '#BF616A', '#A3BE8C', '#EBCB8B', '#81A1C1', '#B48EAD', '#88C0D0', '#E5E9F0', '#4C566A', '#BF616A', '#A3BE8C', '#EBCB8B', '#81A1C1', '#B48EAD', '#8FBCBB', '#ECEFF4'], '#D8DEE9', '#2E3440', '#D8DEE9'],
  ['Gruvbox Dark', ['#282828', '#CC241D', '#98971A', '#D79921', '#458588', '#B16286', '#689D6A', '#A89984', '#928374', '#FB4934', '#B8BB26', '#FABD2F', '#83A598', '#D3869B', '#8EC07C', '#EBDBB2'], '#EBDBB2', '#282828', '#EBDBB2'],
  ['One Dark', ['#282C34', '#E06C75', '#98C379', '#E5C07B', '#61AFEF', '#C678DD', '#56B6C2', '#ABB2BF', '#5C6370', '#E06C75', '#98C379', '#E5C07B', '#61AFEF', '#C678DD', '#56B6C2', '#FFFFFF'], '#ABB2BF', '#282C34', '#528BFF'],
  ['Solarized Dark', ['#073642', '#DC322F', '#859900', '#B58900', '#268BD2', '#D33682', '#2AA198', '#EEE8D5', '#002B36', '#CB4B16', '#586E75', '#657B83', '#839496', '#6C71C4', '#93A1A1', '#FDF6E3'], '#839496', '#002B36', '#93A1A1'],
  ['Amber CRT', ['#2A1A05', '#C25A12', '#FFB000', '#FFD27A', '#9C5A0E', '#D9822B', '#FFC54D', '#FFE2A8', '#6B4510', '#E0731E', '#FFC233', '#FFE9B8', '#B8742A', '#F0A04B', '#FFD98A', '#FFF4DE'], '#FFB000', '#120B02', '#FFD27A', 'retro'],
  ['Commodore 64', ['#000000', '#9F4E44', '#5CAB5E', '#C9D487', '#50459B', '#A057A3', '#6ABFC6', '#ADADAD', '#626262', '#CB7E75', '#9AE29B', '#EDF171', '#887ECB', '#C77ACB', '#9AE6EB', '#FFFFFF'], '#887ECB', '#40318D', '#887ECB', 'retro'],
  ['MS-DOS', ['#000000', '#AA0000', '#00AA00', '#AA5500', '#0000AA', '#AA00AA', '#00AAAA', '#AAAAAA', '#555555', '#FF5555', '#55FF55', '#FFFF55', '#5555FF', '#FF55FF', '#55FFFF', '#FFFFFF'], '#AAAAAA', '#000000', '#AAAAAA', 'retro'],
  ["Synthwave '84", ['#241B30', '#FE4450', '#72F1B8', '#FEDE5D', '#6E95FF', '#FF7EDB', '#03EDF9', '#E0D7EE', '#495495', '#FF6E7A', '#9CFFD6', '#FFF08A', '#9AB6FF', '#FFA6EA', '#7CF6FF', '#FFFFFF'], '#F2E9FF', '#241B30', '#FF7EDB', 'retro'],
  ['Teletype', ['#2B2620', '#A6322B', '#4F7A28', '#9A6A12', '#2F5A8C', '#7A3E78', '#2E7A73', '#8A7F6E', '#6B6254', '#C4453C', '#5E9130', '#B98318', '#3C6FA8', '#965092', '#38928A', '#1A1712'], '#2B2620', '#F1E8D2', '#2B2620', 'retro'],
];
function applyScheme(el, a, b = a, k = 0) {
  const A = SCHEMES[a], B = SCHEMES[b];
  for (let i = 0; i < 16; i++) el.style.setProperty('--c' + i, mix(A[1][i], B[1][i], k));
  el.style.setProperty('--fg', mix(A[2], B[2], k));
  el.style.setProperty('--bg', mix(A[3], B[3], k));
  el.style.setProperty('--cursor', mix(A[4], B[4], k));
}
const TGKDARK = 0;

// Files view rows: [name, size, modified, perms, dir, link]
function fileRows(rows, { y0 = 74, sel = -1, drop = -1, compact = false } = {}) {
  return rows.map((r, i) => {
    const [n, sz, m, p, dir, link, warn] = r;
    return `<div class="frow${i === sel ? ' sel' : ''}${i === drop ? ' drop' : ''}" data-r="row${i}" style="top:${y0 + i * 30}px;left:4px;right:4px">
      <span class="fc-ic${dir ? ' dir' : ''}">${ic(dir ? 'folder' : 'file', 'width:15px;height:15px')}</span>
      <span class="fc-n">${n}${link ? `<small style="${warn ? 'color:#e5534b' : ''}">→ ${link}</small>` : ''}</span>
      <span class="fc-s">${sz}</span>${compact ? '' : `<span class="fc-m">${m}</span><span class="fc-p">${p}</span>`}</div>`;
  }).join('');
}
const fileHead = (compact = false) => `<div class="fhead"><span class="fc-ic"></span><span class="fc-n" style="color:#c9cfdb;font-weight:650">Name ↑</span><span class="fc-s">Size</span>${compact ? '' : '<span class="fc-m">Modified</span><span class="fc-p" style="font-family:Inter;font-size:11.5px">Permissions</span>'}</div>`;
function filesToolbar(path) {
  return `<div class="ftool">${ic('chevron-left')}${ic('chevron-right')}${ic('arrow-up')}${ic('refresh')}
    <div class="fpath" data-r="path">${path}</div><div class="ffilter">${ic('search', 'width:13px;height:13px')}Filter</div>
    ${ic('upload')}${ic('download')}${ic('folder-plus')}${ic('more')}</div>`;
}
