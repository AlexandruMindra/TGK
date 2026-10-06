// Renders the stage frame by frame with headless Chromium and encodes it with ffmpeg.
//   node render.mjs <lang> video [--fps 30] [--workers 4]   -> build/<lang>/video.mp4 + sfx.json
//   node render.mjs <lang> stills 3.5 12 40.2               -> build/<lang>/stills/t<sec>.png
//   node render.mjs <lang> thumb                            -> build/<lang>/thumbnail.png (1280x720)
import { createServer } from 'node:http';
import { readFile, mkdir, writeFile, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { join, extname, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '../..');
const [lang = 'en', mode = 'stills', ...rest] = process.argv.slice(2);
const opt = (k, d) => { const i = rest.indexOf('--' + k); return i >= 0 ? +rest[i + 1] : d; };
const FPS = opt('fps', 30), WORKERS = opt('workers', 4);
const OUT = join(HERE, 'build', lang);
const timing = JSON.parse(await readFile(join(OUT, 'timing.json'), 'utf8'));

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.ttf': 'font/ttf', '.woff2': 'font/woff2', '.json': 'application/json', '.png': 'image/png' };
const server = createServer(async (req, res) => {
  try {
    const p = join(ROOT, decodeURIComponent(new URL(req.url, 'http://x').pathname));
    if (!p.startsWith(ROOT)) throw new Error('outside');
    const body = await readFile(p);
    res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' });
    res.end(body);
  } catch { res.writeHead(404); res.end(); }
}).listen(0, '127.0.0.1');
await new Promise(r => server.once('listening', r));
const URL0 = `http://127.0.0.1:${server.address().port}/marketing/video/stage/index.html`;

const browser = await chromium.launch({ args: ['--force-color-profile=srgb', '--font-render-hinting=none', '--disable-lcd-text'] });
async function openPage(w = 1920, h = 1080, scale = 1) {
  const page = await browser.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: scale });
  page.on('pageerror', e => console.error('page error:', e.message));
  page.on('console', m => { if (m.type() === 'error') console.error('console:', m.text()); });
  await page.goto(URL0);
  await page.evaluate(tm => window.init(tm), timing);
  const cdp = await page.context().newCDPSession(page);
  const shot = async (fmt = 'jpeg') => Buffer.from((await cdp.send('Page.captureScreenshot', fmt === 'png' ? { format: 'png' } : { format: 'jpeg', quality: 94, optimizeForSpeed: true })).data, 'base64');
  return { page, shot };
}

if (mode === 'stills') {
  await mkdir(join(OUT, 'stills'), { recursive: true });
  const { page, shot } = await openPage();
  for (const s of rest.filter(x => !x.startsWith('--'))) {
    await page.evaluate(async t => { seek(t); await document.fonts.ready; }, +s);
    await writeFile(join(OUT, 'stills', `t${(+s).toFixed(2)}.png`), await shot('png'));
  }
  console.log('stills written');
} else if (mode === 'sfx') {
  const { page } = await openPage();
  await writeFile(join(OUT, 'sfx.json'), JSON.stringify(await page.evaluate(() => sfxEvents()), null, 1));
  console.log('sfx.json written');
} else if (mode === 'thumb') {
  const { page, shot } = await openPage(1920, 1080, 1);
  await page.evaluate(async () => { seek(0); thumb(); await document.fonts.ready; });
  await writeFile(join(OUT, 'thumbnail-full.png'), await shot('png'));
  await new Promise(r => spawn('ffmpeg', ['-y', '-loglevel', 'error', '-i', join(OUT, 'thumbnail-full.png'), '-vf', 'scale=1280:720:flags=lanczos', join(OUT, 'thumbnail.png')], { stdio: 'inherit' }).on('close', r));
  console.log('thumbnail written');
} else if (mode === 'video') {
  const total = Math.ceil(timing.total * FPS);
  await mkdir(join(OUT, 'chunks'), { recursive: true });
  const per = Math.ceil(total / WORKERS);
  const t0 = Date.now();
  let done = 0;
  await Promise.all(Array.from({ length: WORKERS }, async (_, w) => {
    const a = w * per, b = Math.min(total, a + per);
    const { page, shot } = await openPage();
    const ff = spawn('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', String(FPS), '-c:v', 'mjpeg', '-i', '-',
      '-c:v', 'libx264', '-preset', 'slow', '-crf', '15', '-pix_fmt', 'yuv420p', '-tune', 'animation', '-r', String(FPS), join(OUT, 'chunks', `c${w}.mp4`)], { stdio: ['pipe', 'inherit', 'inherit'] });
    for (let f = a; f < b; f++) {
      await page.evaluate(t => seek(t), f / FPS);
      const buf = await shot();
      if (!ff.stdin.write(buf)) await new Promise(r => ff.stdin.once('drain', r));
      if (++done % 300 === 0) console.log(`${done}/${total} frames, ${((Date.now() - t0) / 1000).toFixed(0)} s`);
    }
    ff.stdin.end();
    await new Promise(r => ff.on('close', r));
  }));
  await writeFile(join(OUT, 'chunks', 'list.txt'), Array.from({ length: WORKERS }, (_, w) => `file 'c${w}.mp4'`).join('\n'));
  await new Promise((r, j) => spawn('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', join(OUT, 'chunks', 'list.txt'), '-c', 'copy', join(OUT, 'video.mp4')], { stdio: 'inherit' })
    .on('close', c => (c ? j(new Error('concat failed')) : r())));
  const { page } = await openPage();
  await writeFile(join(OUT, 'sfx.json'), JSON.stringify(await page.evaluate(() => sfxEvents()), null, 1));
  await rm(join(OUT, 'chunks'), { recursive: true });
  console.log(`video: ${total} frames in ${((Date.now() - t0) / 1000).toFixed(0)} s`);
}
await browser.close();
server.close();
