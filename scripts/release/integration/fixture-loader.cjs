'use strict';

const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const { createRequire } = require('node:module');
const directory = path.resolve(__dirname, '..');
const assetDirectory = process.env.MISSIONVIEW_HISTORY_ASSETS;
if (!assetDirectory) {
  throw new Error('Set MISSIONVIEW_HISTORY_ASSETS to the verified original v0.1.0 asset directory');
}
const captured = {
  release: { id: 452383776, tag_name: 'v0.1.0', draft: false, prerelease: false,
    published_at: '2026-11-20T07:23:44Z', target_commitish: '445ca5936a6ddabcac0cc4010509912b406e9398',
    assets: [
      { id: 770874884, name: 'MissionView-0.1.0-Setup.exe', size: 61289058,
        digest: 'sha256:6b7c2bfb0c14d9fc7ba7022a6201bd701dbf2e5e3fe4a136bf688d372ccff365' },
      { id: 770874727, name: 'MissionView-0.1.0-win-x64.zip', size: 88794408,
        digest: 'sha256:999a0c2d0a9abe03eeb3607b014b3be4f0a7e789ccf1c72f987314a08e32d45f' },
      { id: 770874961, name: 'release-manifest.json', size: 192,
        digest: 'sha256:2faa2d281762cbf8582e7bc77a1bcf649bb1d647d1c848b93c987170d8fe7a05' },
      { id: 770874980, name: 'SHA256SUMS.txt', size: 281,
        digest: 'sha256:e10b26f29e2c15da182c317dd1315c0c1e496f8c3400abaffdc7eb14530d3af5' }
    ] }
};
const capturedAssets = captured.release.assets.map(asset => {
  const data = fs.readFileSync(path.join(assetDirectory, asset.name));
  if (data.length !== asset.size ||
      `sha256:${crypto.createHash('sha256').update(data).digest('hex')}` !== asset.digest) {
    throw new Error(`Captured fixture integrity mismatch: ${asset.name}`);
  }
  return { ...asset, data };
});

function loadCandidate(history, { readiness = true } = {}) {
  const cache = new Map();
  const realRequire = createRequire(__filename);
  function load(filename) {
    if (cache.has(filename)) return cache.get(filename).exports;
    const module = { exports: {} };
    cache.set(filename, module);
    const localRequire = id => {
      if (id === 'node:child_process') return { execFileSync: history.execFileSync };
      if (id === 'node:fs') return {
        ...fs,
        readFileSync(file, ...args) {
          if (String(file).replaceAll('\\', '/').endsWith('installer/release-readiness.json')) {
            return JSON.stringify({ publicReleaseApproved: readiness, blockers: readiness ? [] : ['fixture not approved'] });
          }
          return fs.readFileSync(file, ...args);
        }
      };
      return id.startsWith('.') ? load(path.resolve(path.dirname(filename), id)) : realRequire(id);
    };
    const compile = vm.compileFunction(fs.readFileSync(filename, 'utf8'),
      ['require', 'module', 'exports', '__filename', '__dirname'], { filename });
    compile(localRequire, module, module.exports, filename, path.dirname(filename));
    return module.exports;
  }
  return { planner: load(path.join(directory, 'release.cjs')),
    publisher: load(path.join(directory, 'publish-release.cjs')) };
}

function fakeGitHub(history, workflowSha = history.policy.newTip) {
  const releases = new Map();
  const tagRefs = new Map();
  const tagObjects = new Map();
  const calls = [];
  const outputs = new Map();
  const counters = { writes: 0 };
  const state = { mainSha: workflowSha };
  const missing = () => { throw Object.assign(new Error('Fixture resource not found'), { status: 404 }); };
  function installRelease(tag, source = tag.sha) {
    const version = tag.name.slice(1);
    const names = [`MissionView-${version}-win-x64.zip`, `MissionView-${version}-Setup.exe`,
      'release-manifest.json', 'SHA256SUMS.txt'];
    const bytes = [Buffer.from('synthetic ZIP'), Buffer.from('synthetic Setup'), Buffer.from(JSON.stringify({
      version, sourceSha: source, runtime: 'win-x64', selfContained: true
    }))];
    bytes.push(Buffer.from(bytes.map((data, i) =>
      `${crypto.createHash('sha256').update(data).digest('hex')}  ${names[i]}`).join('\n') + '\n'));
    let release = { id: releases.size + 1, tag_name: tag.name, draft: false, prerelease: false,
      published_at: '2026-01-01T00:00:00Z', target_commitish: 'main' };
    let assets = names.map((name, i) => ({ id: `${release.id}:${i}`, name, data: bytes[i] }));
    if (tag.name === 'v0.1.0' && source === '445ca5936a6ddabcac0cc4010509912b406e9398') {
      release = { ...captured.release };
      // Share read-only Buffers; each test replaces a data property instead of mutating the capture.
      assets = capturedAssets.map(asset => ({ ...asset }));
    }
    releases.set(tag.name, { release, assets });
    tagRefs.set(`tags/${tag.name}`, { object: { type: 'tag', sha: tag.object } });
    tagObjects.set(tag.object, { object: { type: 'commit', sha: tag.sha } });
    return releases.get(tag.name);
  }
  installRelease(history.originalTag);
  const forbiddenWrite = () => { counters.writes++; throw new Error('Fixture forbids publication writes'); };
  const github = {
    paginate: async (_method, { release_id }) => {
      calls.push(`assets:${release_id}`);
      const found = [...releases.values()].find(item => item.release.id === release_id);
      return found ? found.assets : missing();
    },
    rest: {
      git: {
        getRef: async ({ ref }) => {
          calls.push(`ref:${ref}`);
          return { data: ref === 'heads/main'
            ? { object: { type: 'commit', sha: state.mainSha } }
            : tagRefs.get(ref) ?? missing() };
        },
        getTag: async ({ tag_sha }) => {
          calls.push(`tag:${tag_sha}`);
          return { data: tagObjects.get(tag_sha) ?? missing() };
        },
        createTag: forbiddenWrite,
        createRef: forbiddenWrite
      },
      repos: {
        getReleaseByTag: async ({ tag }) => {
          calls.push(`release:${tag}`);
          return { data: releases.get(tag)?.release ?? missing() };
        },
        listReleaseAssets() { throw new Error('Expected paginated asset access'); },
        getReleaseAsset: async ({ asset_id }) => {
          calls.push(`download:${asset_id}`);
          const asset = [...releases.values()].flatMap(item => item.assets).find(item => item.id === asset_id);
          return { data: asset?.data ?? missing() };
        },
        createRelease: forbiddenWrite,
        uploadReleaseAsset: forbiddenWrite,
        deleteReleaseAsset: forbiddenWrite,
        updateRelease: forbiddenWrite
      }
    }
  };
  return {
    github, context: { sha: workflowSha, repo: { owner: 'fixture', repo: 'fixture' } },
    core: { notice() {}, setOutput: (name, value) => outputs.set(name, value) },
    outputs, calls, counters, releases, tagRefs, tagObjects, state, installRelease
  };
}

module.exports = { loadCandidate, fakeGitHub, captured, assetDirectory };
