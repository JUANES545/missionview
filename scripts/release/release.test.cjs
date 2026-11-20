'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const { planRelease, planFromTagRefs, assertGitFlow, expectedAssets, validateAssets, assertReleaseReady } = require('./release.cjs');
const sha = 'a'.repeat(40);
const otherSha = 'b'.repeat(40);

test('release tooling stays trackable while local state and build outputs remain ignored', () => {
  for (const [file, status] of [
    [path.join('scripts', 'release', 'release.cjs'), 1],
    [path.join('.github', 'workflows', 'delivery.yml'), 1],
    ['settings.json', 0],
    ['enumeration-diagnostic.json', 0],
    [path.join('tests', 'WindowScatter.WpfTests', 'bin', 'example.dll'), 0]
  ]) {
    const result = spawnSync('git', ['check-ignore', '--quiet', '--no-index', file], {
      cwd: path.join(__dirname, '../..'), encoding: 'utf8'
    });
    assert.equal(result.status, status, `${file}: ${result.stderr}`);
  }
});

test('first release and numeric, consecutive patch progression', () => {
  assert.equal(planRelease([], sha).version, '0.1.0');
  assert.equal(planRelease([
    { name: 'v0.1.9', sha: otherSha }, { name: 'v0.1.10', sha: otherSha },
    { name: 'upstream-note', sha: otherSha }
  ], sha).version, '0.1.11');
});

test('rerunning an older released SHA keeps its original version', () => {
  const plan = planRelease([
    { name: 'v0.1.0', sha }, { name: 'v0.1.1', sha: otherSha }
  ], sha);
  assert.equal(plan.version, '0.1.0');
  assert.equal(plan.existingTag, true);
});

test('upstream tags cannot seed or overwrite the MissionView version sequence', () => {
  const inherited = { name: 'v9.0.0', sha: otherSha, type: 'tag', subject: 'Upstream release' };
  assert.equal(planFromTagRefs([inherited], sha).version, '0.1.0');
  const owned = { name: 'v0.1.3', sha: otherSha, type: 'tag', subject: 'MissionView release v0.1.3' };
  assert.equal(planFromTagRefs([inherited, owned], sha).version, '0.1.4');
  assert.throws(() => planFromTagRefs([{ ...inherited, name: 'v0.1.0' }], sha), /collides/);
});

test('a reviewed nextVersion supports minor or major releases without commit parsing', () => {
  const tags = [{ name: 'v0.1.9', sha: otherSha }];
  assert.equal(planRelease(tags, sha, '0.2.0').version, '0.2.0');
  assert.equal(planRelease(tags, sha, '1.0.0').version, '1.0.0');
  assert.throws(() => planRelease(tags, sha, '0.1.9'), /must advance/);
  assert.throws(() => planRelease(tags, sha, '0.1.8'), /must advance/);
  assert.equal(planRelease([{ name: 'v0.1.9', sha }], sha, '1.0.0').version, '0.1.9');
});

test('rejects ambiguous tags, malformed versions, overflow and unsafe SHA input', () => {
  for (const tags of [
    [{ name: 'v0.1.0', sha }, { name: 'v0.1.1', sha }],
    [{ name: 'v01.1.0', sha: otherSha }],
    [{ name: 'v0.1.65534', sha: otherSha }]
  ]) assert.throws(() => planRelease(tags, sha));
  assert.throws(() => planRelease([], '--untrusted'));
});

test('enforces only feature to develop and develop to main PRs', () => {
  assert.doesNotThrow(() => assertGitFlow('develop', 'feature/release-pipeline'));
  assert.doesNotThrow(() => assertGitFlow('main', 'develop'));
  for (const [base, head] of [
    ['main', 'feature/release'], ['develop', 'main'], ['develop', 'user-feature'],
    ['main', 'develop;command'], ['develop', 'feature/']
  ]) assert.throws(() => assertGitFlow(base, head));
});

test('public releases require explicit approval and an empty blocker list', () => {
  for (const policy of [
    {}, { publicReleaseApproved: true }, { publicReleaseApproved: false, blockers: [] },
    { publicReleaseApproved: true, blockers: ['settings migration pending'] }
  ]) assert.throws(() => assertReleaseReady(policy));
  assert.doesNotThrow(() => assertReleaseReady({ publicReleaseApproved: true, blockers: [] }));
});

test('release assets reject tampering, traversal, extra files and mismatched source', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'missionview-release-test-'));
  const plan = planRelease([{ name: 'v0.1.9', sha: otherSha }], sha, '0.2.0');
  const names = expectedAssets(plan.version);
  try {
    fs.writeFileSync(path.join(directory, names[0]), 'zip fixture');
    fs.writeFileSync(path.join(directory, names[1]), 'setup fixture');
    fs.writeFileSync(path.join(directory, names[2]), JSON.stringify({
      version: plan.version, sourceSha: sha, runtime: 'win-x64', selfContained: true
    }));
    const checksums = names.slice(0, -1).map(name =>
      `${crypto.createHash('sha256').update(fs.readFileSync(path.join(directory, name))).digest('hex')}  ${name}`
    ).join('\n') + '\n';
    fs.writeFileSync(path.join(directory, names[3]), checksums);
    assert.doesNotThrow(() => validateAssets(directory, plan));
    assert.throws(() => validateAssets(directory, { ...plan, sha: otherSha }));
    fs.writeFileSync(path.join(directory, 'unexpected.txt'), 'extra');
    assert.throws(() => validateAssets(directory, plan));
    fs.unlinkSync(path.join(directory, 'unexpected.txt'));
    fs.appendFileSync(path.join(directory, names[0]), 'corrupt');
    assert.throws(() => validateAssets(directory, plan));
    fs.writeFileSync(path.join(directory, names[3]), `${'a'.repeat(64)}  ../outside\n`);
    assert.throws(() => validateAssets(directory, plan));
  } finally {
    fs.rmSync(directory, { recursive: true });
  }
});
