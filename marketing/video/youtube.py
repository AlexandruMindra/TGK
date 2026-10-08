"""Writes build/<lang>/youtube.md: title, description with chapters, and tags, ready to paste into YouTube Studio."""
import json, sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
URL = 'https://github.com/AlexandruMindra/TGK'

TEXT = {
    'en': {
        'title': 'TGK — every server, one encrypted vault | SSH, SFTP & AI agents for Windows, Linux, macOS',
        'intro': 'TGK is a free, open-source SSH session and credential manager with browser-style tabs. Keep your hosts, '
                 'keys and connection settings in an end-to-end encrypted vault, synced through your own lightweight '
                 'server or kept entirely on one machine.',
        'points': ['Browser-style tabs and split view (2, 3, 4 or 6 sessions at once)',
                   'xterm-compatible terminal with truecolor and 11 color schemes',
                   'SFTP file browser: drag & drop, built-in editor, host-to-host copies',
                   'Jump-host chains, port forwarding, settings inherited from defaults to group to host',
                   'AI agents (MCP): let Claude Code work on your servers under Read only / Ask / Full rules, with an audit log',
                   'Zero-knowledge vault: PBKDF2-SHA256 (600k), AES-256-GCM per item, TOTP two-factor',
                   'Self-updating builds for Windows, Linux and macOS'],
        'feature': 'Missing a feature? Build it and send a pull request, or open a request on GitHub: if it helps many '
                   'people, we build it together.',
        'dl': 'Download', 'issues': 'Feature requests', 'chapters': 'Chapters',
        'tags': ['ssh client', 'ssh manager', 'sftp client', 'terminal', 'devops', 'sysadmin', 'open source',
                 'end-to-end encryption', 'self-hosted', 'mcp', 'claude code', 'ai agents', 'windows', 'linux', 'macos', 'TGK'],
    },
    'ro': {
        'title': 'TGK — toate serverele, un singur seif criptat | SSH, SFTP și agenți AI pentru Windows, Linux, macOS',
        'intro': 'TGK este un manager gratuit și open source de sesiuni SSH și credențiale, cu taburi ca în browser. '
                 'Serverele, cheile și setările de conectare stau într-un seif criptat end-to-end, sincronizat prin '
                 'propriul tău server sau păstrat complet pe un singur calculator.',
        'points': ['Taburi ca în browser și ecran împărțit (2, 3, 4 sau 6 sesiuni deodată)',
                   'Terminal compatibil xterm, truecolor și 11 scheme de culori',
                   'Explorator de fișiere SFTP: drag & drop, editor integrat, copiere între servere',
                   'Lanțuri de jump host, port forwarding, setări moștenite de la implicit la grup la server',
                   'Agenți AI (MCP): Claude Code lucrează pe serverele tale după reguli Read only / Ask / Full, cu jurnal de audit',
                   'Seif zero-knowledge: PBKDF2-SHA256 (600k), AES-256-GCM pe fiecare element, autentificare TOTP',
                   'Versiuni care se actualizează singure pentru Windows, Linux și macOS'],
        'feature': 'Îți lipsește o funcționalitate? O construiești și trimiți un pull request, sau deschizi o cerere pe '
                   'GitHub: dacă ajută mai mulți oameni, o construim împreună.',
        'dl': 'Descarcă', 'issues': 'Cereri de funcționalități', 'chapters': 'Capitole',
        'tags': ['client ssh', 'manager ssh', 'client sftp', 'terminal', 'devops', 'sysadmin', 'open source',
                 'criptare end-to-end', 'self-hosted', 'mcp', 'claude code', 'agenți ai', 'windows', 'linux', 'macos', 'TGK'],
    },
}


def main(lang):
    out = HERE / 'build' / lang
    t = TEXT[lang]
    tm = json.loads((out / 'timing.json').read_text())
    chapters = []
    for s in tm['scenes']:
        m, sec = divmod(int(round(s['start'])), 60)
        chapters.append(f"{m}:{sec:02} {s['chapter']}")
    desc = '\n'.join([t['intro'], '', *[f'• {p}' for p in t['points']], '', t['feature'], '',
                      f"{t['dl']}: {URL}/releases/latest", f"{t['issues']}: {URL}/issues", '',
                      f"{t['chapters']}:", *chapters])
    md = f"# YouTube — {lang}\n\n## Title\n\n{t['title']}\n\n## Description\n\n```\n{desc}\n```\n\n## Tags\n\n{', '.join(t['tags'])}\n\n" \
         f"## Files\n\n- Video: `TGK-{lang}.mp4`\n- Thumbnail: `thumbnail.png` (1280x720)\n- Subtitles: `subtitles.srt` (upload under Subtitles)\n" \
         f"- End screen: the last ~10 s leave the right half free for YouTube end-screen elements (video + subscribe).\n"
    (out / 'youtube.md').write_text(md)
    print(f'{lang}: youtube.md')


if __name__ == '__main__':
    main(sys.argv[1])
