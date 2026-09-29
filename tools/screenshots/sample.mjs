// Builds the sample the README screenshots are taken of: a small store API in
// a fresh git repository, three agents each in a worktree of their own, and a
// Claude config folder holding their transcripts. Nothing here reads the
// machine it runs on, so the pictures show the same made-up project wherever
// they are taken, never a real repository or a real home folder.
//
// Git dates and transcript timestamps are fixed, so a rerun draws the same
// pictures unless the app itself changed.

import { execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

const when = '2026-03-12T14:00:00Z';
const gitEnv = {
  ...process.env,
  GIT_AUTHOR_NAME: 'Sam Rivera',
  GIT_AUTHOR_EMAIL: 'sam@example.com',
  GIT_COMMITTER_NAME: 'Sam Rivera',
  GIT_COMMITTER_EMAIL: 'sam@example.com',
  GIT_AUTHOR_DATE: when,
  GIT_COMMITTER_DATE: when,
  GIT_CONFIG_GLOBAL: process.platform === 'win32' ? 'NUL' : '/dev/null',
  GIT_CONFIG_NOSYSTEM: '1',
};

function git(cwd, ...args) {
  execFileSync('git', ['-c', 'init.defaultBranch=main', '-c', 'commit.gpgsign=false', ...args], {
    cwd, env: gitEnv, stdio: 'pipe',
  });
}

function write(root, files) {
  for (const [path, text] of Object.entries(files)) {
    const full = join(root, path);
    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, text);
  }
}

const base = {
  '.gitignore': 'node_modules/\ndist/\n.claude/worktrees/\n',
  'README.md': `# Acme Store API

The HTTP API behind the Acme storefront: products, carts and orders.

\`\`\`bash
npm install
npm run dev
\`\`\`

Requests are authenticated with an API key in the \`x-api-key\` header.
`,
  'package.json': `{
  "name": "acme-store-api",
  "version": "1.4.0",
  "private": true,
  "type": "module",
  "scripts": {
    "dev": "tsx watch src/server.ts",
    "build": "tsc -p .",
    "test": "vitest run"
  },
  "dependencies": {
    "express": "^5.1.0",
    "zod": "^4.1.0"
  },
  "devDependencies": {
    "@types/express": "^5.0.3",
    "supertest": "^7.1.0",
    "tsx": "^4.20.0",
    "typescript": "^5.9.0",
    "vitest": "^3.2.0"
  }
}
`,
  'tsconfig.json': `{
  "compilerOptions": {
    "target": "ES2023",
    "module": "NodeNext",
    "strict": true,
    "outDir": "dist"
  },
  "include": ["src"]
}
`,
  'src/server.ts': `import express from 'express';
import { requireApiKey } from './middleware/auth.js';
import { handleErrors } from './middleware/errors.js';
import { orders } from './routes/orders.js';
import { products } from './routes/products.js';

export const app = express();

app.use(express.json());
app.use(requireApiKey);

app.use('/products', products);
app.use('/orders', orders);

app.use(handleErrors);

if (process.env.NODE_ENV !== 'test') {
  const port = Number(process.env.PORT ?? 3000);
  app.listen(port, () => console.log(\`Acme Store API on :\${port}\`));
}
`,
  'src/middleware/auth.ts': `import type { NextFunction, Request, Response } from 'express';
import { db } from '../lib/db.js';

export async function requireApiKey(req: Request, res: Response, next: NextFunction) {
  const key = req.header('x-api-key');
  const client = key ? await db.clients.byKey(key) : undefined;
  if (!client) {
    return res.status(401).json({ error: 'A valid x-api-key header is required.' });
  }

  res.locals.client = client;
  next();
}
`,
  'src/middleware/errors.ts': `import type { NextFunction, Request, Response } from 'express';
import { ZodError } from 'zod';

export function handleErrors(err: unknown, _req: Request, res: Response, _next: NextFunction) {
  if (err instanceof ZodError) {
    return res.status(400).json({ error: 'Invalid request', issues: err.issues });
  }

  console.error(err);
  res.status(500).json({ error: 'Something went wrong.' });
}
`,
  'src/lib/db.ts': `export interface Client { id: string; name: string; plan: 'free' | 'pro' }
export interface Product { id: string; name: string; priceCents: number; stock: number }
export interface Order { id: string; clientId: string; lines: OrderLine[]; totalCents: number; createdAt: Date }
export interface OrderLine { productId: string; quantity: number; unitCents: number }

const clients = new Map<string, Client>();
const products = new Map<string, Product>();
const orders = new Map<string, Order>();

export const db = {
  clients: {
    byKey: async (key: string) => clients.get(key),
  },
  products: {
    all: async () => [...products.values()],
    byId: async (id: string) => products.get(id),
  },
  orders: {
    forClient: async (clientId: string) => [...orders.values()].filter(o => o.clientId === clientId),
    insert: async (order: Order) => void orders.set(order.id, order),
  },
};
`,
  'src/lib/money.ts': `export function totalCents(lines: { quantity: number; unitCents: number }[]) {
  return lines.reduce((sum, line) => sum + line.quantity * line.unitCents, 0);
}

export function formatCents(cents: number, currency = 'USD') {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(cents / 100);
}
`,
  'src/routes/products.ts': `import { Router } from 'express';
import { db } from '../lib/db.js';

export const products = Router();

products.get('/', async (_req, res) => {
  res.json(await db.products.all());
});

products.get('/:id', async (req, res) => {
  const product = await db.products.byId(req.params.id);
  if (!product) {
    return res.status(404).json({ error: 'No such product.' });
  }

  res.json(product);
});
`,
  'src/routes/orders.ts': `import { randomUUID } from 'node:crypto';
import { Router } from 'express';
import { z } from 'zod';
import { db } from '../lib/db.js';
import { totalCents } from '../lib/money.js';

export const orders = Router();

const NewOrder = z.object({
  lines: z.array(z.object({
    productId: z.string(),
    quantity: z.number().int().positive(),
  })).min(1),
});

orders.get('/', async (_req, res) => {
  res.json(await db.orders.forClient(res.locals.client.id));
});

orders.post('/', async (req, res) => {
  const body = NewOrder.parse(req.body);

  const lines = [];
  for (const line of body.lines) {
    const product = await db.products.byId(line.productId);
    if (!product) {
      return res.status(404).json({ error: \`No such product: \${line.productId}\` });
    }

    lines.push({ ...line, unitCents: product.priceCents });
  }

  const order = {
    id: randomUUID(),
    clientId: res.locals.client.id,
    lines,
    totalCents: totalCents(lines),
    createdAt: new Date(),
  };

  await db.orders.insert(order);
  res.status(201).json(order);
});
`,
  'test/orders.test.ts': `import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { app } from '../src/server.js';

const key = { 'x-api-key': 'test-key' };

describe('POST /orders', () => {
  it('prices each line from the catalogue', async () => {
    const res = await request(app).post('/orders').set(key)
      .send({ lines: [{ productId: 'mug', quantity: 2 }] });

    expect(res.status).toBe(201);
    expect(res.body.totalCents).toBe(2400);
  });

  it('refuses an unknown product', async () => {
    const res = await request(app).post('/orders').set(key)
      .send({ lines: [{ productId: 'nope', quantity: 1 }] });

    expect(res.status).toBe(404);
  });
});
`,
};

// The rate-limits agent's work, left uncommitted for Source control to show.
const rateLimit = `import type { NextFunction, Request, Response } from 'express';

/**
 * A token bucket per client. Each client may make \`limit\` requests in a burst
 * and earns one back every \`windowMs / limit\` milliseconds, so a steady client
 * is never throttled and a burst is smoothed rather than cut off at a boundary.
 */
export function rateLimit({ limit, windowMs }: { limit: number; windowMs: number }) {
  const buckets = new Map<string, { tokens: number; refilledAt: number }>();
  const refillEvery = windowMs / limit;

  return (req: Request, res: Response, next: NextFunction) => {
    const id: string = res.locals.client?.id ?? req.ip ?? 'anonymous';
    const now = Date.now();
    const bucket = buckets.get(id) ?? { tokens: limit, refilledAt: now };

    const earned = Math.floor((now - bucket.refilledAt) / refillEvery);
    if (earned > 0) {
      bucket.tokens = Math.min(limit, bucket.tokens + earned);
      bucket.refilledAt += earned * refillEvery;
    }

    res.setHeader('RateLimit-Limit', limit);
    res.setHeader('RateLimit-Remaining', Math.max(0, bucket.tokens - 1));

    if (bucket.tokens === 0) {
      const retryAfter = Math.ceil((bucket.refilledAt + refillEvery - now) / 1000);
      res.setHeader('Retry-After', retryAfter);
      buckets.set(id, bucket);
      return res.status(429).json({ error: 'Too many requests.', retryAfter });
    }

    bucket.tokens -= 1;
    buckets.set(id, bucket);
    next();
  };
}
`;

const serverAfter = base['src/server.ts']
  .replace("import { handleErrors } from './middleware/errors.js';\n",
    "import { handleErrors } from './middleware/errors.js';\nimport { rateLimit } from './middleware/rateLimit.js';\n")
  .replace("app.use(requireApiKey);\n",
    "app.use(requireApiKey);\napp.use(rateLimit({ limit: 60, windowMs: 60_000 }));\n");

const ordersAfter = base['src/routes/orders.ts']
  .replace("import { totalCents } from '../lib/money.js';\n",
    "import { totalCents } from '../lib/money.js';\nimport { rateLimit } from '../middleware/rateLimit.js';\n")
  .replace("orders.post('/', async (req, res) => {",
    "// Placing an order reserves stock, so it gets a tighter limit than reads.\norders.post('/', rateLimit({ limit: 10, windowMs: 60_000 }), async (req, res) => {");

const testAfter = base['test/orders.test.ts'] + `
describe('rate limiting', () => {
  it('answers 429 with Retry-After once a client runs out', async () => {
    const order = { lines: [{ productId: 'mug', quantity: 1 }] };
    for (let i = 0; i < 10; i++) {
      await request(app).post('/orders').set(key).send(order);
    }

    const res = await request(app).post('/orders').set(key).send(order);

    expect(res.status).toBe(429);
    expect(Number(res.header['retry-after'])).toBeGreaterThan(0);
  });
});
`;

const readmeAfter = base['README.md'] + `
Every client may make 60 requests a minute, and 10 new orders a minute. Past
that the API answers \`429 Too Many Requests\` with a \`Retry-After\` header.
`;

const agents = [
  {
    name: 'rate-limits',
    sessionId: '5f0c2a8e-4b1d-4c7e-9a3f-2d6b8e1f4a01',
    title: 'Add per-client rate limiting to the API',
    start: '2026-03-12T14:02:00Z',
    prompt: 'Add per-client rate limiting to the API. Orders should have a tighter limit than reads, and a throttled client needs to know when to retry.',
    work(dir) {
      write(dir, { 'src/middleware/rateLimit.ts': rateLimit, 'src/server.ts': serverAfter });
      git(dir, 'add', 'src/server.ts');
      write(dir, { 'src/routes/orders.ts': ordersAfter, 'test/orders.test.ts': testAfter, 'README.md': readmeAfter });
    },
    turns: dir => [
      ['said', "I'll look at how requests reach the routes first, so the limit sits after the API key check and can key on the client."],
      ['read', join(dir, 'src/server.ts')],
      ['read', join(dir, 'src/middleware/auth.ts')],
      ['read', join(dir, 'src/routes/orders.ts')],
      ['said', 'Auth puts the client on res.locals, so the limiter can key on its id. I\'ll use a token bucket rather than a fixed window, so a client that bursts at the end of one window and the start of the next is not let through twice.'],
      ['write', join(dir, 'src/middleware/rateLimit.ts'), rateLimit],
      ['edit', join(dir, 'src/server.ts'), "app.use(requireApiKey);\n", "app.use(requireApiKey);\napp.use(rateLimit({ limit: 60, windowMs: 60_000 }));\n"],
      ['edit', join(dir, 'src/routes/orders.ts'), "orders.post('/', async (req, res) => {", "// Placing an order reserves stock, so it gets a tighter limit than reads.\norders.post('/', rateLimit({ limit: 10, windowMs: 60_000 }), async (req, res) => {"],
      ['edit', join(dir, 'test/orders.test.ts'), "});\n", testAfter.slice(base['test/orders.test.ts'].length - 4)],
      ['bash', 'npm test', ' ✓ test/orders.test.ts (3 tests) 212ms\n\n Test Files  1 passed (1)\n      Tests  3 passed (3)'],
      ['said', `Done. Every client now gets 60 requests a minute, and placing an order is held to 10 a minute on top of that. The limiter in src/middleware/rateLimit.ts is a token bucket per client, keyed on the client auth resolved. A throttled request gets a 429 with Retry-After, and the new test drains the order limit to check it. All 3 tests pass.

The buckets are in memory, so each instance counts on its own. If the API runs behind a load balancer, this wants Redis; say if you want that next.`],
    ],
  },
  {
    name: 'checkout-rounding',
    sessionId: '8a1e7d3c-2f64-4b9a-b0c5-7e3d9f2a6b12',
    title: 'Fix rounding in order totals',
    start: '2026-03-12T13:41:00Z',
    prompt: 'Order totals are off by a cent on some multi-line orders. Find out why and fix it.',
    work() {},
    turns: dir => [
      ['read', join(dir, 'src/lib/money.ts')],
      ['said', 'Totals are summed in integer cents, so the total itself cannot drift. The cent goes missing earlier: the storefront sends prices in dollars and they are converted with Math.round(price * 100) per unit. Want me to move the conversion to one place in the API?'],
    ],
  },
  {
    name: 'node-22',
    sessionId: '3c9b4e1f-7a2d-4e8b-8f6c-1b5a0d9e7c23',
    title: 'Upgrade to Node 22',
    start: '2026-03-12T13:15:00Z',
    prompt: 'Upgrade the project to Node 22 and make sure the tests still pass.',
    work() {},
    turns: () => [
      ['bash', 'npm test', ' Test Files  1 passed (1)\n      Tests  2 passed (2)'],
      ['said', 'Upgraded to Node 22: engines in package.json, the CI image and .nvmrc. The tests pass unchanged.'],
    ],
  },
];

function transcript({ sessionId, prompt, start, turns }, cwd) {
  let clock = Date.parse(start);
  let n = 0;
  const lines = [];
  const stamp = () => new Date((clock += 9_000)).toISOString();
  const common = () => ({ sessionId, cwd, isSidechain: false, uuid: `${sessionId.slice(0, 8)}-${String(++n).padStart(4, '0')}`, timestamp: stamp() });
  const assistant = content => lines.push({ type: 'assistant', ...common(), message: { role: 'assistant', content } });
  const result = (id, text, extra = {}) =>
    lines.push({ type: 'user', ...common(), message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: id, content: text }] }, ...extra });

  lines.push({ type: 'user', ...common(), message: { role: 'user', content: prompt } });
  for (const [kind, a, b, c] of turns(cwd)) {
    const id = `toolu_${String(++n).padStart(4, '0')}`;
    switch (kind) {
      case 'said':
        assistant([{ type: 'text', text: a }]);
        break;
      case 'read':
        assistant([{ type: 'tool_use', id, name: 'Read', input: { file_path: a } }]);
        result(id, '(file contents)');
        break;
      case 'bash':
        assistant([{ type: 'tool_use', id, name: 'Bash', input: { command: a, description: 'Run the tests' } }]);
        result(id, b);
        break;
      case 'write':
        assistant([{ type: 'tool_use', id, name: 'Write', input: { file_path: a, content: b } }]);
        result(id, `File created successfully at: ${a}`, { toolUseResult: { type: 'create', filePath: a } });
        break;
      case 'edit':
        assistant([{ type: 'tool_use', id, name: 'Edit', input: { file_path: a, old_string: b, new_string: c } }]);
        result(id, `The file ${a} has been updated.`, { toolUseResult: { type: 'update', filePath: a } });
        break;
    }
  }

  return { text: lines.map(l => JSON.stringify(l)).join('\n') + '\n', endedAt: new Date(clock).toISOString() };
}

// Claude names a project's transcript folder after its working directory,
// with every separator, dot and space turned into a dash.
const slug = cwd => cwd.replace(/[\\/:. ]/g, '-');

/**
 * Makes the sample under root, replacing any earlier one. Returns the paths
 * the app is pointed at.
 */
export function createSample(root) {
  rmSync(root, { recursive: true, force: true });
  const repo = join(root, 'acme-store');
  const claudeDir = join(root, 'claude');
  const dataDir = join(root, 'dashboard');
  mkdirSync(repo, { recursive: true });

  write(repo, base);
  git(repo, 'init', '-q');
  git(repo, 'add', '-A');
  git(repo, 'commit', '-q', '-m', 'Orders, products and API keys');

  const records = [];
  for (const agent of agents) {
    const dir = join(repo, '.claude', 'worktrees', agent.name);
    git(repo, 'worktree', 'add', '-q', '-b', agent.name, dir);
    agent.work(dir);

    const { text, endedAt } = transcript(agent, dir);
    write(join(claudeDir, 'projects', slug(dir)), { [`${agent.sessionId}.jsonl`]: text });
    records.push({
      SessionId: agent.sessionId,
      Cwd: dir,
      Title: agent.title,
      AddedAt: agent.start,
      Prompt: agent.prompt,
      Context: { Used: 41200, Size: 200000, Percent: 21 },
      TurnEndedAt: endedAt,
    });
  }

  write(dataDir, {
    'settings.json': JSON.stringify({ RepoRoots: [repo], HiddenRoots: [], TrustedRoots: [repo], NotifyOnWaiting: false }, null, 2),
    'agents.json': JSON.stringify(records, null, 2),
    'last-view.json': JSON.stringify({ SessionId: agents[0].sessionId, WorktreePath: records[0].Cwd, RepoRoot: repo, Pinned: false }),
    // Every finished turn counts as read, so no agent carries an unread dot.
    'seen-turns.json': JSON.stringify(Object.fromEntries(records.map(r => [r.SessionId, r.TurnEndedAt]))),
  });

  return { repo, claudeDir, dataDir };
}
