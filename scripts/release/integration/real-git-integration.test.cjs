'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const childProcess = require('node:child_process');
const crypto = require('node:crypto');
const { objectId, createVerifier } = require('../equivalence-core.cjs');
const { loadCandidate, fakeGitHub, captured, assetDirectory } = require('./fixture-loader.cjs');

const root = path.resolve(__dirname, '../../..');
const policy = require('./fixture-policy.json');
const testRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'missionview-history-integration-'));
require('node:test').after(() => fs.rmSync(testRoot, { recursive: true }));
const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith('GIT_')));
Object.assign(env, { GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: process.platform === 'win32' ? 'NUL' : '/dev/null',
  GIT_TERMINAL_PROMPT: '0', GIT_NO_REPLACE_OBJECTS: '1' });
let fixtureNumber = 0;

function history(t) {
  const dir = path.join(testRoot, `case-${++fixtureNumber}-${process.pid}.git`);
  childProcess.execFileSync('git', ['clone', '--mirror', '--no-hardlinks',
    root, dir], { env, stdio: 'pipe' });
  t.after(() => fs.rmSync(dir, { recursive: true }));
  const git = (...args) => childProcess.execFileSync('git', ['-C', dir, ...args],
    { env, stdio: 'pipe' });
  git('remote', 'remove', 'origin');
  const write = (type, raw) => {
    const id = childProcess.execFileSync('git', ['-C', dir, 'hash-object', '-t', type, '-w', '--stdin'],
      { input: raw, env, stdio: 'pipe' }).toString().trim();
    assert.equal(id, objectId(type, raw));
    return id;
  };
  const tree = git('rev-parse', `${policy.newTip}^{tree}`).toString().trim();
  function descendant(parent, seconds, message) {
    // Test objects only: these are not part of the six-entry product mapping.
    return write('commit', Buffer.from(`tree ${tree}\nparent ${parent}\n` +
      `author History Test Fixture <fixture@example.invalid> ${seconds} +0000\n` +
      `committer History Test Fixture <fixture@example.invalid> ${seconds} +0000\n\n${message}\n`));
  }
  const later = descendant(policy.newTip, 1800000000, 'Isolated descendant fixture');
  const next = descendant(later, 1800000001, 'Isolated later descendant fixture');
  function tag(name, sha) {
    const raw = Buffer.from(`object ${sha}\ntype commit\ntag ${name}\n` +
      `tagger History Test Fixture <fixture@example.invalid> 1800000010 +0000\n\nMissionView release ${name}\n`);
    const object = write('tag', raw);
    git('update-ref', `refs/tags/${name}`, object);
    return { name, sha, object, raw };
  }
  const commands = [];
  const failures = new Map();
  const originalRefs = git('for-each-ref').toString();
  const originalObjects = git('count-objects', '-v').toString();
  const h = {
    policy: structuredClone(policy), dir, git, write, tag, descendant: later, next, commands, failures,
    originalTag: { name: 'v0.1.0', sha: policy.oldTip, object: policy.tag.object,
      raw: git('cat-file', 'tag', policy.tag.object) },
    execFileSync(command, args, options = {}) {
      assert.equal(command, 'git');
      const key = args.join(' ');
      commands.push(key);
      assert(['for-each-ref', 'rev-parse', 'cat-file', 'merge-base'].includes(args[0]),
        `Planner attempted a non-read-only Git command: ${key}`);
      if (failures.has(key)) throw failures.get(key);
      return childProcess.execFileSync(command, ['-C', dir, ...args],
        { ...options, env, stdio: 'pipe' });
    },
    assertNoGitWrites() {
      assert.equal(git('for-each-ref').toString(), originalRefs);
      assert.equal(git('count-objects', '-v').toString(), originalObjects);
    }
  };
  return h;
}

async function rejected(t, mutate, workflow = 'tip', options) {
  const h = history(t);
  const api = fakeGitHub(h, workflow === 'tip' ? policy.newTip : h.descendant);
  mutate(h, api);
  const { publisher } = loadCandidate(h, options);
  await assert.rejects(() => publisher.prepare(api));
  assert.equal(api.outputs.size, 0);
  assert.equal(api.counters.writes, 0);
}

test('real Git: full raw proof through planner/prepare at exact tip emits only publish=false', async t => {
  const h = history(t);
  assert.equal(typeof require('../reviewed-history-verifier.cjs'), 'function');
  const { planner, publisher } = loadCandidate(h);
  const plan = planner.planFromGit(policy.newTip, true);
  assert.equal(plan.sha, policy.newTip);
  assert.equal(plan.existingTag, false);
  assert.equal(plan.historyEquivalence.originalSourceSha, policy.oldTip);
  assert.equal(plan.historyEquivalence.alreadyPublishedEquivalent, true);
  const api = fakeGitHub(h);
  await publisher.prepare(api);
  assert.deepEqual([...api.outputs], [['publish', 'false']]);
  assert.equal(api.calls.filter(call => call.startsWith('download:')).length, 4);
  assert(!api.calls.includes('release:v0.1.1'));
  assert.equal(api.counters.writes, 0);
  h.assertNoGitWrites();
});

test('real Git: descendant plans 0.1.1 and verifies original predecessor', async t => {
  const h = history(t);
  const { publisher } = loadCandidate(h);
  const api = fakeGitHub(h, h.descendant);
  await publisher.prepare(api);
  const plan = JSON.parse(api.outputs.get('plan'));
  assert.equal(api.outputs.get('publish'), 'true');
  assert.equal(plan.version, '0.1.1');
  assert.equal(plan.sha, h.descendant);
  assert.equal(plan.previousTag, 'v0.1.0');
  assert.equal(plan.historyEquivalence.originalSourceSha, policy.oldTip);
  assert.equal(plan.historyEquivalence.alreadyPublishedEquivalent, false);
  assert.equal(api.calls.filter(call => call.startsWith('download:')).length, 4);
  assert.equal(api.counters.writes, 0);
  h.assertNoGitWrites();
});

test('real Git: original rerun needs no mapping and is a publication no-op', async t => {
  const h = history(t);
  const { planner, publisher } = loadCandidate(h);
  const plan = planner.planFromGit(policy.oldTip, true);
  assert.equal(plan.version, '0.1.0');
  assert.equal(plan.existingTag, true);
  assert(!Object.hasOwn(plan, 'historyEquivalence'));
  const api = fakeGitHub(h, policy.oldTip);
  await publisher.prepare(api);
  assert.deepEqual([...api.outputs], [['publish', 'false']]);
  assert.equal(api.counters.writes, 0);
  h.assertNoGitWrites();
});

test('real Git: later ordinary tag/rerun need no history equivalence proof', async t => {
  const h = history(t);
  const tag = h.tag('v0.1.1', h.descendant);
  const { planner, publisher } = loadCandidate(h);
  const plan = planner.planFromGit(h.next, true);
  assert.equal(plan.version, '0.1.2');
  assert.equal(plan.previousTag, 'v0.1.1');
  assert(!Object.hasOwn(plan, 'historyEquivalence'));
  const api = fakeGitHub(h, h.next);
  api.installRelease(tag);
  await publisher.prepare(api);
  assert.equal(api.outputs.get('publish'), 'true');
  assert(api.calls.includes(`tag:${tag.object}`));
  assert(!api.calls.includes(`tag:${policy.tag.object}`));
  const rerun = fakeGitHub(h, h.descendant);
  rerun.installRelease(tag);
  await publisher.prepare(rerun);
  assert.deepEqual([...rerun.outputs], [['publish', 'false']]);
  assert.equal(api.counters.writes + rerun.counters.writes, 0);
  assert(!h.commands.some(command => command.startsWith('cat-file')));
});

test('real Git: pinned production binding cannot accept a caller-substituted tip', async t => {
  const h = history(t);
  h.policy.newTip = policy.upstream;
  const { publisher } = loadCandidate(h);
  const api = fakeGitHub(h, policy.upstream);
  await assert.rejects(() => publisher.prepare(api), /not a real ancestor/);
  assert.equal(api.calls.length, 0);
  assert.equal(api.outputs.size, 0);
  h.assertNoGitWrites();
});

test('real Git: candidate map must have exactly six entries', t => {
  const h = history(t);
  for (const entries of [h.policy.entries.slice(1), [...h.policy.entries, h.policy.entries[0]]]) {
    assert.throws(() => createVerifier({ ...h.policy, entries }), /exactly six mappings/);
  }
  const duplicate = structuredClone(h.policy);
  duplicate.entries[1].new = duplicate.entries[0].new;
  assert.throws(() => createVerifier(duplicate), /bijective/);
});

test('real Git: unapproved metadata bytes fail even with a recomputed candidate tip', async t => {
  await rejected(t, (h, api) => {
    const raw = h.git('cat-file', 'commit', h.policy.newTip);
    const changed = Buffer.from(raw.toString().replace(/committer ([^\n]+) (\d+) ([+-]\d{4})\n/,
      (_all, identity, date, zone) => `committer ${identity} ${Number(date) + 1} ${zone}\n`));
    assert(!raw.equals(changed));
    const newId = h.write('commit', changed);
    api.context.sha = newId;
  });
});

test('real Git: unrelated existing workflow object fails equivalent ancestry', async t => {
  await rejected(t, (_h, api) => { api.context.sha = policy.upstream; });
});

test('real Git: foreign latest tag cannot enter finite v0.1.0 exception', async t => {
  await rejected(t, h => { h.tag('v0.2.0', policy.oldTip); });
});

test('real Git: replaced local annotation is rejected', async t => {
  await rejected(t, h => { h.tag('v0.1.0', policy.oldTip); });
});

test('real Git: real missing object errors remain operational errors', async t => {
  await rejected(t, h => {
    fs.renameSync(path.join(h.dir, 'objects'), path.join(h.dir, 'missing-objects'));
    fs.mkdirSync(path.join(h.dir, 'objects'));
  });
});

for (const command of ['merge-base --is-ancestor v0.1.0', 'cat-file commit']) {
  test(`real Git: injected operational failure in ${command} is not ancestry=false`, async t => {
    await rejected(t, h => {
      const key = command.startsWith('merge-base') ? `${command} ${policy.newTip}` : `${command} ${policy.oldTip}`;
      h.failures.set(key, Object.assign(new Error('Injected Git operational failure'), { status: 128 }));
    });
  });
}

for (const workflow of ['tip', 'descendant']) {
  for (const defect of ['wrong-source', 'checksum', 'extra-asset', 'missing-asset', 'duplicate-asset',
    'draft', 'prerelease', 'unpublished', 'remote-tag-object', 'remote-tag-source',
    'remote-lightweight-tag', 'api-auth', 'download']) {
    test(`real Git ${workflow}: ${defect} fails closed`, async t => {
      await rejected(t, (h, api) => {
        if (defect === 'wrong-source') api.installRelease(h.originalTag, policy.newTip);
        const previous = api.releases.get('v0.1.0');
        if (defect === 'checksum') previous.assets[0].data = Buffer.from('altered fixture bytes');
        if (defect === 'extra-asset') previous.assets.push({ name: 'unknown.bin', id: 'extra', data: Buffer.from('extra') });
        if (defect === 'missing-asset') previous.assets.pop();
        if (defect === 'duplicate-asset') previous.assets[1] = previous.assets[0];
        if (defect === 'draft') previous.release.draft = true;
        if (defect === 'prerelease') previous.release.prerelease = true;
        if (defect === 'unpublished') previous.release.published_at = null;
        if (defect === 'remote-tag-object') api.tagRefs.get('tags/v0.1.0').object.sha = '7'.repeat(40);
        if (defect === 'remote-tag-source') api.tagObjects.get(policy.tag.object).object.sha = policy.newTip;
        if (defect === 'remote-lightweight-tag') api.tagRefs.get('tags/v0.1.0').object = { type: 'commit', sha: policy.oldTip };
        if (defect === 'api-auth') api.github.rest.repos.getReleaseByTag = async () => {
          throw Object.assign(new Error('Injected API authorization failure'), { status: 401 });
        };
        if (defect === 'download') api.github.rest.repos.getReleaseAsset = async () => {
          throw Object.assign(new Error('Injected asset download failure'), { status: 403 });
        };
      }, workflow);
    });
  }
}

test('real Git: readiness failure blocks a descendant without any publication output', async t => {
  await rejected(t, () => {}, 'descendant', { readiness: false });
});

test('real Git: stale main descendant emits no publication plan/version', async t => {
  const h = history(t);
  const { publisher } = loadCandidate(h);
  const api = fakeGitHub(h, h.descendant);
  api.state.mainSha = h.next;
  await publisher.prepare(api);
  assert.deepEqual([...api.outputs], [['publish', 'false']]);
  assert.equal(api.counters.writes, 0);
  h.assertNoGitWrites();
});

test('real Git and captured original asset bytes: immutable digests/source and no-op publication', async t => {
  const h = history(t);
  const expected = [
    ['MissionView-0.1.0-Setup.exe', 61289058, '6b7c2bfb0c14d9fc7ba7022a6201bd701dbf2e5e3fe4a136bf688d372ccff365'],
    ['MissionView-0.1.0-win-x64.zip', 88794408, '999a0c2d0a9abe03eeb3607b014b3be4f0a7e789ccf1c72f987314a08e32d45f'],
    ['release-manifest.json', 192, '2faa2d281762cbf8582e7bc77a1bcf649bb1d647d1c848b93c987170d8fe7a05'],
    ['SHA256SUMS.txt', 281, 'e10b26f29e2c15da182c317dd1315c0c1e496f8c3400abaffdc7eb14530d3af5']
  ];
  assert.deepEqual(captured.release.assets.map(asset => [asset.name, asset.size, asset.digest]),
    expected.map(([name, bytes, digest]) => [name, bytes, `sha256:${digest}`]));
  const api = fakeGitHub(h);
  const original = api.releases.get('v0.1.0');
  original.release = { ...captured.release };
  original.assets = expected.map(([name, bytes, digest]) => {
    const data = fs.readFileSync(path.join(assetDirectory, name));
    assert.equal(data.length, bytes);
    assert.equal(crypto.createHash('sha256').update(data).digest('hex'), digest);
    const metadata = captured.release.assets.find(asset => asset.name === name);
    return { ...metadata, data };
  });
  const { publisher } = loadCandidate(h);
  await publisher.prepare(api);
  assert.deepEqual([...api.outputs], [['publish', 'false']]);
  assert.equal(api.calls.filter(call => call.startsWith('download:')).length, 4);
  assert.equal(api.counters.writes, 0);
  h.assertNoGitWrites();
  for (const [name, bytes, digest] of expected) {
    const data = fs.readFileSync(path.join(assetDirectory, name));
    assert.equal(data.length, bytes);
    assert.equal(crypto.createHash('sha256').update(data).digest('hex'), digest);
  }
});

function replaceCoherently(api) {
  const assets = api.releases.get('v0.1.0').assets;
  for (const asset of assets) {
    if (asset.name.endsWith('.exe') || asset.name.endsWith('.zip')) {
      asset.data = Buffer.from(`Coordinated replacement: ${asset.name}`);
    } else if (asset.name === 'release-manifest.json') {
      asset.data = Buffer.from(JSON.stringify({
        version: '0.1.0', sourceSha: policy.oldTip, runtime: 'win-x64', selfContained: true,
        unreviewed: 'Replacement manifest retains the expected source and version'
      }));
    }
  }
  const checksums = assets.find(asset => asset.name === 'SHA256SUMS.txt');
  checksums.data = Buffer.from(assets.filter(asset => asset !== checksums).map(asset =>
    `${crypto.createHash('sha256').update(asset.data).digest('hex')}  ${asset.name}`).join('\n') + '\n');
  for (const asset of assets) {
    asset.size = asset.data.length;
    asset.digest = `sha256:${crypto.createHash('sha256').update(asset.data).digest('hex')}`;
  }
}

for (const workflow of ['tip', 'descendant', 'original-rerun', 'ordinary-original-descendant']) {
  for (const defect of ['coordinated-replacement', 'metadata-size', 'metadata-digest',
    'missing-size', 'missing-digest', 'missing-asset', 'extra-asset',
    'checksum-bytes', 'manifest-bytes', 'payload-bytes', 'same-size-bytes']) {
    test(`immutable assets ${workflow}: ${defect} blocks before any outputs or writes`, async t => {
      const h = history(t);
      const workflowSha = workflow === 'tip' ? policy.newTip
        : workflow === 'descendant' ? h.descendant
          : workflow === 'original-rerun' ? policy.oldTip : policy.entries[3].old;
      const api = fakeGitHub(h, workflowSha);
      if (workflow === 'ordinary-original-descendant') {
        // Make an ordinary original-history child in this disposable database only.
        const old = h.git('cat-file', 'commit', h.descendant).toString();
        api.context.sha = h.write('commit', Buffer.from(old.replace(policy.newTip, policy.oldTip)));
        api.state.mainSha = api.context.sha;
      }
      const assets = api.releases.get('v0.1.0').assets;
      if (defect === 'coordinated-replacement') replaceCoherently(api);
      if (defect === 'metadata-size') assets[0].size++;
      if (defect === 'metadata-digest') assets[0].digest = `sha256:${'0'.repeat(64)}`;
      if (defect === 'missing-size') delete assets[0].size;
      if (defect === 'missing-digest') delete assets[0].digest;
      if (defect === 'missing-asset') assets.pop();
      if (defect === 'extra-asset') assets.push({ id: 'foreign', name: 'foreign.bin' });
      if (defect === 'checksum-bytes') assets.find(asset => asset.name === 'SHA256SUMS.txt').data = Buffer.from('new checksums');
      if (defect === 'manifest-bytes') assets.find(asset => asset.name === 'release-manifest.json').data = Buffer.from('{}');
      if (defect === 'payload-bytes') assets[0].data = Buffer.from('new payload');
      if (defect === 'same-size-bytes') {
        const asset = assets.find(item => item.name === 'SHA256SUMS.txt');
        asset.data = Buffer.from(asset.data);
        asset.data[0] = asset.data[0] === 97 ? 98 : 97;
        assert.equal(asset.data.length, asset.size);
      }
      const { publisher } = loadCandidate(h);
      await assert.rejects(() => publisher.prepare(api),
        /Historical v0.1.0 asset (metadata differs|bytes differ)|missing or unexpected assets/);
      assert.equal(api.outputs.size, 0);
      assert.equal(api.counters.writes, 0);
    });
  }
}

test('immutable original rerun rejects draft, missing release, changed source/tag and unpublished state', async t => {
  const h = history(t);
  const { publisher } = loadCandidate(h);
  for (const defect of ['draft', 'missing', 'prerelease', 'unpublished', 'tag', 'source']) {
    const api = fakeGitHub(h, policy.oldTip);
    const previous = api.releases.get('v0.1.0');
    if (defect === 'draft') previous.release.draft = true;
    if (defect === 'missing') api.releases.delete('v0.1.0');
    if (defect === 'prerelease') previous.release.prerelease = true;
    if (defect === 'unpublished') previous.release.published_at = null;
    if (defect === 'tag') api.tagRefs.get('tags/v0.1.0').object.sha = 'e'.repeat(40);
    if (defect === 'source') api.tagObjects.get(policy.tag.object).object.sha = policy.newTip;
    await assert.rejects(() => publisher.prepare(api));
    assert.equal(api.outputs.size, 0);
    assert.equal(api.counters.writes, 0);
  }
});

test('immutable checksums cannot be bypassed by coherent API metadata, even on direct verifier/local validator routes', async t => {
  const h = history(t);
  const api = fakeGitHub(h, policy.oldTip);
  replaceCoherently(api);
  const { publisher, planner } = loadCandidate(h);
  const plan = { sha: policy.oldTip, version: '0.1.0', tag: 'v0.1.0' };
  const release = api.releases.get('v0.1.0');
  const directory = fs.mkdtempSync(path.join(testRoot, 'replacement-'));
  t.after(() => fs.rmSync(directory, { recursive: true }));
  for (const asset of release.assets) fs.writeFileSync(path.join(directory, asset.name), asset.data);
  // The replacement is self-consistent; only immutable pins should reject it.
  const { sourceSha, version } = JSON.parse(
    release.assets.find(asset => asset.name === 'release-manifest.json').data);
  assert.equal(sourceSha, plan.sha);
  assert.equal(version, plan.version);
  for (const asset of release.assets) {
    assert.equal(asset.size, asset.data.length);
    assert.equal(asset.digest, `sha256:${crypto.createHash('sha256').update(asset.data).digest('hex')}`);
  }
  assert.throws(() => planner.validateAssets(directory, plan), /Historical v0.1.0 asset bytes differ/);
  await assert.rejects(() => publisher.verifyRemote(api.github, api.context.repo, release.release, plan),
    /Historical v0.1.0 asset metadata differs/);
  await assert.rejects(() => publisher.verifyPreviousRelease(api.github, api.context.repo, 'v0.1.0'),
    /Historical v0.1.0 asset metadata differs/);
  await assert.rejects(() => publisher.publish(api, plan, directory), /Historical v0.1.0 asset bytes differ/);
  assert.equal(api.counters.writes, 0);
});

test('immutable bytes reject coordinated replacement even when the API falsely reports the original pins', async t => {
  const h = history(t);
  const api = fakeGitHub(h);
  const original = api.releases.get('v0.1.0').assets.map(asset => ({ size: asset.size, digest: asset.digest }));
  replaceCoherently(api);
  api.releases.get('v0.1.0').assets.forEach((asset, index) => Object.assign(asset, original[index]));
  const { publisher } = loadCandidate(h);
  await assert.rejects(() => publisher.prepare(api), /Historical v0.1.0 asset bytes differ/);
  assert.equal(api.outputs.size, 0);
  assert.equal(api.counters.writes, 0);
});

test('historical publish rerun only verifies and never recreates or uploads original assets', async t => {
  const h = history(t);
  const api = fakeGitHub(h, policy.oldTip);
  const directory = fs.mkdtempSync(path.join(testRoot, 'original-'));
  t.after(() => fs.rmSync(directory, { recursive: true }));
  for (const asset of api.releases.get('v0.1.0').assets) {
    fs.writeFileSync(path.join(directory, asset.name), asset.data);
  }
  const { publisher } = loadCandidate(h);
  await publisher.publish(api, { sha: policy.oldTip, version: '0.1.0', tag: 'v0.1.0' }, directory);
  assert.equal(api.counters.writes, 0);
  assert.equal(api.outputs.size, 0);
  for (const defect of ['draft', 'missing', 'changed-bytes']) {
    const invalid = fakeGitHub(h, policy.oldTip);
    if (defect === 'draft') invalid.releases.get('v0.1.0').release.draft = true;
    if (defect === 'missing') invalid.releases.delete('v0.1.0');
    if (defect === 'changed-bytes') replaceCoherently(invalid);
    await assert.rejects(() => publisher.publish(invalid,
      { sha: policy.oldTip, version: '0.1.0', tag: 'v0.1.0' }, directory));
    assert.equal(invalid.counters.writes, 0);
  }
  h.assertNoGitWrites();
});

test('ordinary original-history descendant still plans its next release with pinned historical predecessor', async t => {
  const h = history(t);
  const raw = h.git('cat-file', 'commit', h.descendant).toString();
  const descendant = h.write('commit', Buffer.from(raw.replace(policy.newTip, policy.oldTip)));
  const beforeRefs = h.git('for-each-ref').toString();
  const beforeObjects = h.git('count-objects', '-v').toString();
  const api = fakeGitHub(h, descendant);
  const { publisher } = loadCandidate(h);
  await publisher.prepare(api);
  const plan = JSON.parse(api.outputs.get('plan'));
  assert.equal(api.outputs.get('publish'), 'true');
  assert.equal(plan.version, '0.1.1');
  assert.equal(plan.previousTag, 'v0.1.0');
  assert.equal(plan.sha, descendant);
  assert(!Object.hasOwn(plan, 'historyEquivalence'));
  assert.equal(api.calls.filter(call => call.startsWith('download:')).length, 4);
  assert.equal(api.counters.writes, 0);
  assert.equal(h.git('for-each-ref').toString(), beforeRefs);
  assert.equal(h.git('count-objects', '-v').toString(), beforeObjects);
});

for (const name of ['MissionView-0.1.0-Setup.exe', 'MissionView-0.1.0-win-x64.zip',
  'release-manifest.json', 'SHA256SUMS.txt']) {
  test(`fixed historical pin rejects same-length changed bytes for ${name}`, async t => {
    const h = history(t);
    const api = fakeGitHub(h);
    const asset = api.releases.get('v0.1.0').assets.find(item => item.name === name);
    asset.data = Buffer.from(asset.data);
    asset.data[0] ^= 1;
    assert.equal(asset.data.length, asset.size);
    const { publisher } = loadCandidate(h);
    await assert.rejects(() => publisher.prepare(api), /Historical v0.1.0 asset bytes differ/);
    assert.equal(api.outputs.size, 0);
    assert.equal(api.counters.writes, 0);
  });
  test(`fixed historical pin rejects changed API size/digest for ${name}`, async t => {
    const h = history(t);
    const { publisher } = loadCandidate(h);
    for (const field of ['size', 'digest']) {
      const api = fakeGitHub(h);
      const asset = api.releases.get('v0.1.0').assets.find(item => item.name === name);
      if (field === 'size') asset.size++;
      else asset.digest = `sha256:${'f'.repeat(64)}`;
      await assert.rejects(() => publisher.prepare(api), /Historical v0.1.0 asset metadata differs/);
      assert.equal(api.outputs.size, 0);
      assert.equal(api.counters.writes, 0);
      assert.equal(api.calls.filter(call => call.startsWith('download:')).length, 0);
    }
  });
}
