// A stand-in for the Claude ACP bridge, for recording the extension GIF only.
//
// take.mjs starts the app from a content root of its own whose acp/ folder
// holds this script where the bridge would be, so the app is the real build,
// unchanged, and talks ACP to this exactly as it would to Claude. What this
// plays is one fixed turn: it streams a reply, writes the example extension in
// examples/NodeTests into a folder of the sample, really builds it with dotnet,
// and really calls Tog's tog_extension_add tool, which is what puts the
// "Add and turn on" prompt on screen. It writes Claude's transcript as it goes,
// since that is where the app reads a finished conversation from.
//
// Nothing here talks to a model. It is started by the app, never by hand.
//
//   TOG_DEMO_EXAMPLE  the example to write out (examples/NodeTests)
//   TOG_DEMO_TARGET   where to write it
//   TOG_DEMO_SDK      the app's sdk folder, holding the API it published

import { spawn } from 'node:child_process';
import { appendFileSync, copyFileSync, mkdirSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { createInterface } from 'node:readline';
import { slug } from './sample.mjs';

const example = process.env.TOG_DEMO_EXAMPLE;
const target = process.env.TOG_DEMO_TARGET;
const claudeDir = process.env.CLAUDE_CONFIG_DIR;

// The files in the order a person would write them: the manifest and project
// first, then the model, the work, and the view last.
const order = [
  'extension.json', 'NodeTests.csproj', 'NodeTestsExtension.cs', '_Imports.razor',
  'Testing/Model.cs', 'Testing/Discovery.cs', 'Testing/NodeTestProcess.cs', 'Testing/TestRunner.cs',
  'TestsView.razor', 'assets/extension.css',
];

const opening = "I'll make it a Tog extension with a worktree view, so the Tests tab follows whichever worktree you have open. "
  + 'It lists the tests by reading the *.test.ts files, without running anything, and runs them with node --test only when you press Run.';

const closing = 'Built with no warnings. Tog is asking you to add it: accept and a Tests tab appears beside Source control. '
  + 'Nothing runs until you press Run all, and each file runs on its own, so its results show as soon as it finishes.';

const sessions = new Map();
const sleep = ms => new Promise(done => setTimeout(done, ms));

function send(message) {
  process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...message }) + '\n');
}

function update(sessionId, body) {
  send({ method: 'session/update', params: { sessionId, update: body } });
}

createInterface({ input: process.stdin }).on('line', async line => {
  if (!line.trim()) {
    return;
  }

  const message = JSON.parse(line);
  if (message.method === undefined) {
    return;
  }

  try {
    const result = await handle(message.method, message.params ?? {});
    if (message.id !== undefined) {
      send({ id: message.id, result });
    }
  } catch (e) {
    process.stderr.write(`replay-agent: ${e.stack}\n`);
    if (message.id !== undefined) {
      send({ id: message.id, error: { code: -32603, message: e.message } });
    }
  }
});

async function handle(method, params) {
  switch (method) {
    case 'initialize':
      return {
        protocolVersion: 1,
        agentCapabilities: { mcpCapabilities: { http: true }, sessionCapabilities: { resume: {} } },
        authMethods: [],
      };
    case 'session/new':
    case 'session/resume': {
      const sessionId = params.sessionId ?? crypto.randomUUID();
      sessions.set(sessionId, {
        cwd: params.cwd,
        servers: params.mcpServers ?? [],
        env: params._meta?.claudeCode?.options?.env ?? {},
        n: 0,
      });
      return method === 'session/new' ? { sessionId } : {};
    }
    case 'session/prompt':
      await turn(params.sessionId, params.prompt.map(p => p.text ?? '').join(''));
      return { stopReason: 'end_turn' };
    default:
      return {};
  }
}

async function turn(sessionId, prompt) {
  const session = sessions.get(sessionId);
  const log = transcript(sessionId, session);
  log.user(prompt);

  await say(sessionId, opening);
  log.said(opening);

  for (const file of order) {
    const to = join(target, file);
    const id = log.toolId();
    update(sessionId, { sessionUpdate: 'tool_call', toolCallId: id, title: `Write ${to}`, kind: 'edit', status: 'in_progress' });
    mkdirSync(dirname(to), { recursive: true });
    copyFileSync(join(example, file), to);
    log.write(id, to);
    update(sessionId, { sessionUpdate: 'tool_call_update', toolCallId: id, status: 'completed' });
    await sleep(180);
  }

  const build = `dotnet build ${relative(session.cwd, target)}`;
  const buildId = log.toolId();
  update(sessionId, { sessionUpdate: 'tool_call', toolCallId: buildId, title: build, kind: 'execute', status: 'in_progress' });
  const output = await dotnetBuild();
  log.bash(buildId, build, output);
  update(sessionId, { sessionUpdate: 'tool_call_update', toolCallId: buildId, status: 'completed' });

  const addId = log.toolId();
  update(sessionId, { sessionUpdate: 'tool_call', toolCallId: addId, title: 'tog_extension_add', kind: 'other', status: 'in_progress' });
  const added = await callTool(session, 'tog_extension_add', { path: target });
  log.mcp(addId, 'tog_extension_add', { path: target }, added);
  update(sessionId, { sessionUpdate: 'tool_call_update', toolCallId: addId, status: 'completed' });

  await say(sessionId, closing);
  log.said(closing);
}

// Streams text the way the bridge does, a few characters at a time.
async function say(sessionId, text) {
  for (let i = 0; i < text.length; i += 4) {
    update(sessionId, { sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: text.slice(i, i + 4) } });
    await sleep(22);
  }
}

function dotnetBuild() {
  // The app publishes its API as sdk/<major>.<minor> when it starts.
  const root = process.env.TOG_DEMO_SDK;
  const sdk = join(root, readdirSync(root).sort((a, b) => a.localeCompare(b, 'en', { numeric: true })).at(-1));
  return new Promise((done, fail) => {
    const child = spawn('dotnet', ['build', target, '-v', 'q', '-nologo', `-p:TogSdk=${sdk}`], { stdio: ['ignore', 'pipe', 'pipe'] });
    let text = '';
    child.stdout.on('data', d => (text += d));
    child.stderr.on('data', d => (text += d));
    child.on('error', fail);
    child.on('exit', code => (code === 0 ? done(text.trim().split('\n').slice(-6).join('\n')) : fail(new Error(`dotnet build failed:\n${text}`))));
  });
}

// Tog's MCP server, called as Claude would call it: the header names the
// session's key as a variable, and the value is only in that session's env.
async function callTool(session, name, args) {
  const server = session.servers.find(s => s.name === 'tog');
  if (!server) {
    throw new Error('The app handed this session no tog MCP server.');
  }

  const headers = { 'content-type': 'application/json' };
  for (const { name: header, value } of server.headers ?? []) {
    headers[header] = value.replace(/\$\{(\w+)\}/g, (_, v) => session.env[v] ?? '');
  }

  const post = async body => {
    const res = await fetch(server.url, { method: 'POST', headers, body: JSON.stringify({ jsonrpc: '2.0', ...body }) });
    if (!res.ok) {
      throw new Error(`${server.url} answered ${res.status}`);
    }

    return res.status === 204 ? null : res.json();
  };

  await post({ id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'replay-agent', version: '1' } } });
  const answer = await post({ id: 2, method: 'tools/call', params: { name, arguments: args } });
  return answer.result.content.map(c => c.text).join('\n');
}

// Claude's transcript for the session, one JSON line per message, with the
// fields the app reads. The times are real: the chat drops its "Sent." copy of
// a message once the transcript has it, and only one stamped after it was sent.
function transcript(sessionId, session) {
  const file = join(claudeDir, 'projects', slug(session.cwd), `${sessionId}.jsonl`);
  mkdirSync(dirname(file), { recursive: true });

  const common = () => ({
    sessionId,
    cwd: session.cwd,
    isSidechain: false,
    uuid: `${sessionId.slice(0, 8)}-${String(++session.n).padStart(4, '0')}`,
    timestamp: new Date().toISOString(),
  });
  const line = value => appendFileSync(file, JSON.stringify(value) + '\n');
  const assistant = content => line({ type: 'assistant', ...common(), message: { role: 'assistant', content } });
  const result = (id, text, extra = {}) =>
    line({ type: 'user', ...common(), message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: id, content: text }] }, ...extra });

  return {
    toolId: () => `toolu_${String(++session.n).padStart(4, '0')}`,
    user: text => line({ type: 'user', ...common(), message: { role: 'user', content: text } }),
    said: text => assistant([{ type: 'text', text }]),
    write(id, path) {
      assistant([{ type: 'tool_use', id, name: 'Write', input: { file_path: path, content: readFileSync(path, 'utf8') } }]);
      result(id, `File created successfully at: ${path}`, { toolUseResult: { type: 'create', filePath: path } });
    },
    bash(id, command, output) {
      assistant([{ type: 'tool_use', id, name: 'Bash', input: { command, description: 'Build the extension' } }]);
      result(id, output);
    },
    mcp(id, tool, input, output) {
      assistant([{ type: 'tool_use', id, name: `mcp__tog__${tool}`, input }]);
      result(id, output);
    },
  };
}
