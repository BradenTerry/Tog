// Records the README's GIFs. Two scripts:
//
// - extension: a request typed into an agent's chat, the agent writing and
//   building an extension, Tog asking to add it, and the new Tests tab
//   running the sample's tests.
// - question: an agent asked to go ahead, then left for another one; it stops
//   to ask permission, the Waiting sign example walks out, and its sign leads
//   back to the agent to answer.
//
// The app is the real build. The agent is replay-agent.mjs, which plays one
// fixed turn over ACP, so the picture is the same on every run and no model is
// called. Everything else is the app and the extensions doing what they do:
// the prompt to add one is Tog's own, and the tests really run.
//
// Frames come from Chromium's screencast, which sends one whenever the page
// changes, with its time. The GIF keeps those times, except while the
// extension builds, which is played three times faster so the picture does not
// sit still for seconds.

import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const width = 1440;
const height = 900;

export async function recordGif(browser, url, file, { colorScheme, settle, script }) {
  const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 2, colorScheme });
  await page.goto(url);
  // As in take.mjs: the sample has no node_modules, so every import would be
  // marked unresolved.
  await page.addStyleTag({ content: '.squiggly-error, .squiggly-warning { display: none; }' });
  await page.locator('.tree-row').first().waitFor();
  await settle(page);
  await script.prepare(page, settle);

  const frames = [];
  let speed = 1;
  const cdp = await page.context().newCDPSession(page);
  cdp.on('Page.screencastFrame', ({ data, metadata, sessionId }) => {
    frames.push({ data, at: metadata.timestamp * 1000, speed });
    cdp.send('Page.screencastFrameAck', { sessionId }).catch(() => {});
  });
  await cdp.send('Page.startScreencast', { format: 'png', maxWidth: width * 2, maxHeight: height * 2 });

  const hold = ms => page.waitForTimeout(ms);
  try {
    await script.play(page, hold, value => (speed = value));
  } catch (e) {
    await page.screenshot({ path: `${file}.failed.png` });
    throw e;
  }

  await cdp.send('Page.stopScreencast');
  await page.close();
  encode(frames, file);
}

async function openFile(page, settle, name) {
  await page.locator('button[title^="Go to File"]').click();
  await settle(page, 500);
  await page.keyboard.type(name, { delay: 20 });
  await settle(page);
  await page.keyboard.press('Enter');
  await settle(page, 3000);
}

async function send(page, hold, text) {
  const box = page.locator('textarea').last();
  await box.click();
  await box.pressSequentially(text, { delay: 28 });
  await hold(500);
  await page.keyboard.press('Enter');
}

/** The extension GIF. target is the folder the agent writes the extension to. */
export const extensionScript = target => ({
  // The tests the extension will find, open in the editor from the start.
  prepare: (page, settle) => openFile(page, settle, 'money.test.ts'),
  play: (page, hold, setSpeed) => playExtension(page, hold, setSpeed, target),
});

/** The Waiting sign GIF, on the agent with sessionId, which ended its last turn with a question. */
export const questionScript = (url, sessionId, elsewhere) => ({
  async prepare(page, settle) {
    await page.goto(new URL(`/chat/${sessionId}`, url).href);
    await page.locator('.tree-row').first().waitFor();
    await openFile(page, settle, 'money.ts');
  },
  async play(page, hold) {
    await hold(1200);
    await send(page, hold, 'Yes, move it into the API.');
    await hold(2600);

    // Off to another agent while this one works.
    await page.locator('.agent-count', { hasText: 'stopped' }).click();
    await hold(700);
    await page.locator('.agent-count-item', { hasText: elsewhere }).click();

    const walker = page.locator('.ws-walker');
    await walker.waitFor({ timeout: 60_000 });
    await hold(4200);
    await walker.click();
    await hold(1800);

    await page.getByRole('button', { name: 'Allow', exact: true }).click();
    await walker.waitFor({ state: 'detached', timeout: 30_000 });
    await hold(2500);
  },
});

async function playExtension(page, hold, setSpeed, target) {
  await hold(1200);

  await send(page, hold, 'Build me an extension that lists the tests in this project and lets me run them.');

  // The build is the one wait worth shortening. It starts once the agent
  // has written the last file, the stylesheet.
  const accept = page.getByRole('button', { name: 'Add and turn on' });
  for (let i = 0; i < 600 && !existsSync(join(target, 'assets', 'extension.css')); i++) {
    await hold(100);
  }

  setSpeed(3);
  await accept.waitFor({ timeout: 180_000 });
  setSpeed(1);
  await hold(2200);
  await accept.click();

  const tab = page.getByRole('tab', { name: 'Tests' });
  await tab.waitFor({ timeout: 60_000 });
  await hold(800);
  await tab.click();
  await page.locator('.node-tests-file').first().waitFor();
  await hold(1400);

  await page.getByRole('button', { name: 'Run all' }).click();
  await page.locator('.node-tests-icon.running').first().waitFor().catch(() => {});
  await page.waitForFunction(() =>
    document.querySelector('.node-tests-count.passed')?.textContent.trim() !== '0'
      && !document.querySelector('.node-tests-icon.running'), null, { timeout: 60_000 });
  await hold(3000);
}

// Writes the frames out with their durations and has ffmpeg build one shared
// palette for them, which keeps the file small and the colours steady.
function encode(frames, file) {
  const dir = mkdtempSync(join(tmpdir(), 'tog-gif-'));
  try {
    let list = '';
    frames.forEach((frame, i) => {
      const name = `f${String(i).padStart(5, '0')}.png`;
      writeFileSync(join(dir, name), Buffer.from(frame.data, 'base64'));
      const next = frames[i + 1];
      const ms = next ? (next.at - frame.at) / frame.speed : 3000;
      list += `file '${name}'\nduration ${(Math.max(ms, 20) / 1000).toFixed(3)}\n`;
    });

    // The concat format ignores the last entry's duration unless the file is named again.
    list += `file 'f${String(frames.length - 1).padStart(5, '0')}.png'\n`;
    writeFileSync(join(dir, 'frames.txt'), list);

    execFileSync('ffmpeg', [
      '-y', '-loglevel', 'error',
      '-f', 'concat', '-safe', '0', '-i', join(dir, 'frames.txt'),
      '-vf', `scale=${width}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=256:stats_mode=full[p];[b][p]paletteuse=dither=sierra2_4a`,
      '-fps_mode', 'vfr',
      file,
    ], { stdio: 'inherit' });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}
