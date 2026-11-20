'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { publish, findRelease, verifyRemote, verifyPreviousRelease } = require('./publish-release.cjs');
const { expectedAssets } = require('./release.cjs');
const plan = { sha: 'a'.repeat(40), version: '0.1.0', tag: 'v0.1.0' };

function fixture(build = plan) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'missionview-publication-test-'));
  const directory = path.join(root, 'assets');
  fs.mkdirSync(directory);
  fs.mkdirSync(path.join(root, 'installer'));
  fs.writeFileSync(path.join(root, 'installer', 'release-readiness.json'),
    JSON.stringify({ publicReleaseApproved: true, blockers: [] }));
  const names = expectedAssets(build.version);
  for (const name of names.slice(0, 2)) fs.writeFileSync(path.join(directory, name), 'fixture');
  fs.writeFileSync(path.join(directory, names[2]), JSON.stringify({
    sourceSha: build.sha, version: build.version, runtime: 'win-x64', selfContained: true
  }));
  fs.writeFileSync(path.join(directory, names[3]), names.slice(0, -1).map(name => {
    const hash = crypto.createHash('sha256').update(fs.readFileSync(path.join(directory, name))).digest('hex');
    return `${hash}  ${name}`;
  }).join('\n') + '\n');
  const cwd = process.cwd();
  process.chdir(root);
  return { directory, dispose() { process.chdir(cwd); fs.rmSync(root, { recursive: true }); } };
}

function fakeGitHub() {
  const state = { release: null, ref: null, tag: null, assets: [], uploads: 0, publications: 0,
    failUpload: false, mainSha: plan.sha };
  const notFound = () => { throw Object.assign(new Error('not found'), { status: 404 }); };
  const github = {
    paginate: async () => state.assets,
    rest: {
      git: {
        getRef: async ({ ref }) => ref === 'heads/main'
          ? { data: { object: { sha: state.mainSha, type: 'commit' } } }
          : state.ref ? { data: state.ref } : notFound(),
        createRef: async ({ sha }) => {
          state.ref = { object: { sha, type: 'tag' } };
          return { data: state.ref };
        },
        createTag: async ({ object }) => {
          state.tag = { sha: 'c'.repeat(40), object: { type: 'commit', sha: object } };
          return { data: state.tag };
        },
        getTag: async () => ({ data: state.tag })
      },
      repos: {
        getReleaseByTag: async () => state.release ? { data: state.release } : notFound(),
        createRelease: async () => {
          state.release = { id: 1, draft: true };
          return { data: state.release };
        },
        listReleaseAssets: () => {},
        deleteReleaseAsset: async ({ asset_id }) => {
          state.assets = state.assets.filter(asset => asset.id !== asset_id);
        },
        uploadReleaseAsset: async ({ name, data }) => {
          if (state.failUpload) {
            state.failUpload = false;
            throw new Error('simulated upload interruption');
          }
          state.uploads++;
          state.assets.push({ id: state.uploads, name, data });
        },
        getReleaseAsset: async ({ asset_id }) => ({
          data: state.assets.find(asset => asset.id === asset_id).data
        }),
        updateRelease: async () => { state.release.draft = false; state.publications++; }
      }
    }
  };
  return { github, state, context: { repo: { owner: 'fixture', repo: 'fixture' }, sha: plan.sha },
    core: { notice() {} } };
}

test('publishes only after verification, then reruns without replacing published assets', async () => {
  const local = fixture();
  try {
    const api = fakeGitHub();
    await publish(api, plan, local.directory);
    assert.equal(api.state.publications, 1);
    assert.equal(api.state.uploads, 4);
    await publish(api, plan, local.directory);
    assert.equal(api.state.publications, 1);
    assert.equal(api.state.uploads, 4);
    api.state.assets[0].data = Buffer.from('tampered remote asset');
    await assert.rejects(() => publish(api, plan, local.directory), /Checksum mismatch/);
    assert.equal(api.state.uploads, 4);
  } finally { local.dispose(); }
});

test('interrupted draft upload can resume using the same version and tag', async () => {
  const local = fixture();
  try {
    const api = fakeGitHub();
    api.state.failUpload = true;
    await assert.rejects(() => publish(api, plan, local.directory), /upload interruption/);
    assert.equal(api.state.release.draft, true);
    assert.equal(api.state.publications, 0);
    await publish(api, plan, local.directory);
    assert.equal(api.state.publications, 1);
    assert.equal(api.state.tag.object.sha, plan.sha);
  } finally { local.dispose(); }
});

test('does not retarget an existing release tag or publish blocked product builds', async () => {
  const local = fixture();
  try {
    const api = fakeGitHub();
    api.state.ref = { object: { type: 'commit', sha: 'b'.repeat(40) } };
    await assert.rejects(() => publish(api, plan, local.directory), /Refusing to move/);
    assert.equal(api.state.publications, 0);
    fs.writeFileSync(path.join('installer', 'release-readiness.json'),
      JSON.stringify({ publicReleaseApproved: false, blockers: ['not accepted'] }));
    await assert.rejects(() => publish(api, plan, local.directory), /Public release blocked/);
    assert.equal(api.state.uploads, 0);
  } finally { local.dispose(); }
});

test('a stale main SHA cannot create tags or releases', async () => {
  const local = fixture();
  try {
    const api = fakeGitHub();
    api.github.rest.git.getRef = async () => ({
      data: { object: { type: 'commit', sha: 'b'.repeat(40) } }
    });
    await assert.rejects(() => publish(api, plan, local.directory), /Main advanced/);
    assert.equal(api.state.ref, null);
    assert.equal(api.state.release, null);
  } finally { local.dispose(); }
});

test('main advancing during transfers leaves the verified release draft', async () => {
  const local = fixture();
  try {
    const api = fakeGitHub();
    const download = api.github.rest.repos.getReleaseAsset;
    api.github.rest.repos.getReleaseAsset = async args => {
      const response = await download(args);
      api.state.mainSha = 'b'.repeat(40);
      return response;
    };
    await assert.rejects(() => publish(api, plan, local.directory), /Main advanced during transfers/);
    assert.equal(api.state.uploads, 4);
    assert.equal(api.state.publications, 0);
    assert.equal(api.state.release.draft, true);
  } finally { local.dispose(); }
});

test('missing, corrupt or wrong-source predecessor assets block advancement', async () => {
  for (const defect of ['missing', 'corrupt', 'wrong-source']) {
    const local = fixture();
    try {
      const api = fakeGitHub();
      api.state.release = { id: 1, draft: false };
      api.state.ref = { object: { type: 'tag', sha: 'c'.repeat(40) } };
      api.state.tag = { object: { type: 'commit', sha: defect === 'wrong-source' ? 'b'.repeat(40) : plan.sha } };
      api.state.assets = expectedAssets(plan.version).map((name, id) => ({
        name, id, data: fs.readFileSync(path.join(local.directory, name))
      }));
      if (defect === 'missing') api.state.assets.pop();
      if (defect === 'corrupt') api.state.assets[0].data = Buffer.from('corrupt predecessor');
      await assert.rejects(() => verifyPreviousRelease(api.github, api.context.repo, plan.tag),
        /missing or unexpected assets|Checksum mismatch|manifest does not match/);
      assert.equal(api.state.uploads, 0);
      assert.equal(api.state.publications, 0);
    } finally { local.dispose(); }
  }
});

test('a complete predecessor uses the annotated tag commit, not a mutable release target', async () => {
  const previous = { ...plan, sha: 'b'.repeat(40) };
  const local = fixture(previous);
  try {
    const api = fakeGitHub();
    api.state.release = { id: 1, draft: false, target_commitish: 'main' };
    api.state.ref = { object: { type: 'tag', sha: 'c'.repeat(40) } };
    api.state.tag = { object: { type: 'commit', sha: previous.sha } };
    api.state.assets = expectedAssets(previous.version).map((name, id) => ({
      name, id, data: fs.readFileSync(path.join(local.directory, name))
    }));
    await assert.doesNotReject(() => verifyPreviousRelease(api.github, api.context.repo, previous.tag));
    assert.equal(api.state.publications, 0);
  } finally { local.dispose(); }
});

test('failed download verification keeps the release draft', async () => {
  const local = fixture();
  try {
    const api = fakeGitHub();
    const download = api.github.rest.repos.getReleaseAsset;
    api.github.rest.repos.getReleaseAsset = async args => {
      const asset = api.state.assets.find(item => item.id === args.asset_id);
      return asset.name.endsWith('.zip') ? { data: Buffer.from('corrupt download') } : download(args);
    };
    await assert.rejects(() => publish(api, plan, local.directory), /Checksum mismatch/);
    assert.equal(api.state.publications, 0);
    assert.equal(api.state.release.draft, true);
  } finally { local.dispose(); }
});

test('unknown draft assets require review instead of deletion', async () => {
  const local = fixture();
  try {
    const api = fakeGitHub();
    api.state.release = { id: 1, draft: true };
    api.state.assets = [{ id: 100, name: 'manual-attachment.txt', data: Buffer.from('keep') }];
    await assert.rejects(() => publish(api, plan, local.directory), /unknown assets/);
    assert.equal(api.state.assets[0].name, 'manual-attachment.txt');
    assert.equal(api.state.publications, 0);
  } finally { local.dispose(); }
});

test('only a real 404 means a release is absent; authentication and network failures propagate', async () => {
  const api = fakeGitHub();
  assert.equal(await findRelease(api.github, api.context.repo, plan.tag), null);
  api.github.rest.repos.getReleaseByTag = async () => {
    throw Object.assign(new Error('unauthorized'), { status: 401 });
  };
  await assert.rejects(() => findRelease(api.github, api.context.repo, plan.tag), /unauthorized/);
});

test('rejects remote asset names before writing downloads', async () => {
  const api = fakeGitHub();
  api.state.assets = expectedAssets(plan.version).map((name, id) => ({ name, id }));
  api.state.assets[0].name = '../outside';
  await assert.rejects(() => verifyRemote(api.github, api.context.repo, { id: 1 }, plan),
    /unexpected assets/);
});
