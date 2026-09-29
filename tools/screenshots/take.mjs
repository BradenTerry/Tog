// Retakes the README screenshots in assets/screenshots.
//
//   npm ci --prefix tools/screenshots
//   npx --prefix tools/screenshots playwright install chromium
//   node tools/screenshots/take.mjs
//
// It builds the app, makes the sample project (sample.mjs), starts the app in
// --browser mode against it with no extensions and its own data folder, and
// drives a headless Chromium through it. Your own settings, agents and Claude
// config are never read: CLAUDE_CONFIG_DIR points the app at the sample's.
//
// CHROME=/path/to/chrome uses that browser instead of Playwright's download.
// --no-build skips the build. The sample is left on disk to look at, and
// replaced on the next run.

import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { createSample } from './sample.mjs';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = join(repoRoot, 'assets', 'screenshots');
const appDir = join(repoRoot, 'src', 'Tog.App');
const args = new Set(process.argv.slice(2));

// A short, neutral path, since the status bar prints it.
const sampleRoot = process.platform === 'win32' ? join(tmpdir(), 'tog-sample') : '/tmp/tog-sample';

if (!args.has('--no-build')) {
  execFileSync('dotnet', ['build', appDir, '-v', 'q', '-nologo'], { stdio: 'inherit' });
}

const dll = join(appDir, 'bin', 'Debug', 'net10.0', 'tog.dll');
if (!existsSync(dll)) {
  throw new Error(`No build at ${dll}. Run without --no-build.`);
}

const sample = createSample(sampleRoot);
mkdirSync(out, { recursive: true });

const app = spawn('dotnet', [dll, '--browser', '--no-extensions', '--data-dir', sample.dataDir], {
  cwd: appDir,
  env: { ...process.env, CLAUDE_CONFIG_DIR: sample.claudeDir, TZ: 'UTC', ASPNETCORE_ENVIRONMENT: 'Production' },
  stdio: ['ignore', 'pipe', 'pipe'],
});
app.stderr.resume();

try {
  const url = await new Promise((done, fail) => {
    let seen = '';
    const timer = setTimeout(() => fail(new Error(`The app printed no address in 60s:\n${seen}`)), 60_000);
    app.stdout.on('data', chunk => {
      seen += chunk;
      const match = /(http:\/\/\S+\?ui-key=\S+)/.exec(seen);
      if (match) {
        clearTimeout(timer);
        done(match[1]);
      }
    });
    app.on('exit', code => fail(new Error(`The app exited (${code}) before it printed an address:\n${seen}`)));
  });

  await shoot(url);
  console.log(`Screenshots written to ${out}`);
} finally {
  app.kill();
}

async function shoot(url) {
  const browser = await chromium.launch(process.env.CHROME ? { executablePath: process.env.CHROME } : {});
  try {
    // Each picture in both themes: the README shows the one matching the
    // reader's GitHub theme.
    for (const scheme of ['light', 'dark']) {
      const page = await open(browser, url, scheme);

      // The agent's change to the orders route, side by side.
      await openChange(page, 'orders.ts');
      await page.screenshot({ path: join(out, `overview-${scheme}.png`) });

      // The file the agent wrote, opened with Go to File.
      await goToFile(page, 'rateLimit.ts');
      await page.screenshot({ path: join(out, `editor-${scheme}.png`) });

      await page.close();
    }
  } finally {
    await browser.close();
  }
}

async function open(browser, url, colorScheme) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 2, colorScheme });
  await page.goto(url);
  // The sample has no node_modules, so Monaco's TypeScript worker marks every
  // package import as unresolved. A real checkout would not look like that.
  await page.addStyleTag({ content: '.squiggly-error, .squiggly-warning { display: none; }' });
  await page.locator('.tree-row').first().waitFor();
  await settle(page);
  return page;
}

async function openChange(page, file) {
  await page.getByRole('button', { name: new RegExp(`^\\S*\\s*${file.replace('.', '\\.')}`) }).last().click();
  await settle(page, 3000);
}

async function goToFile(page, file) {
  // Clicked rather than its shortcut: the chat box has focus on load and would
  // take the first letters typed before Go to File opens.
  await page.locator('button[title^="Go to File"]').click();
  await settle(page, 500);
  await page.keyboard.type(file, { delay: 20 });
  await settle(page);
  await page.keyboard.press('Enter');
  await settle(page, 3000);
}

// Monaco and the circuit paint after the DOM settles, so a fixed pause is what
// actually works here; waiting on an element returns before the colours land.
function settle(page, ms = 1500) {
  return page.waitForTimeout(ms);
}
