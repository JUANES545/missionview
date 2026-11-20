'use strict';

const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { planFromGit, validateAssets, expectedAssets, assertReleaseReady } = require('./release.cjs');

async function findRelease(github, repo, tag) {
  try {
    return (await github.rest.repos.getReleaseByTag({ ...repo, tag })).data;
  } catch (error) {
    if (error.status !== 404) throw error;
    return null;
  }
}

async function verifyRemote(github, repo, release, plan) {
  const assets = await github.paginate(github.rest.repos.listReleaseAssets, {
    ...repo, release_id: release.id, per_page: 100
  });
  if (assets.length !== 4 ||
      assets.some(asset => !expectedAssets(plan.version).includes(asset.name))) {
    throw new Error('Remote release has missing or unexpected assets');
  }
  const directory = fs.mkdtempSync(path.join(process.env.RUNNER_TEMP ?? os.tmpdir(), 'missionview-verify-'));
  try {
    for (const asset of assets) {
      const response = await github.rest.repos.getReleaseAsset({
        ...repo, asset_id: asset.id, headers: { accept: 'application/octet-stream' }
      });
      fs.writeFileSync(path.join(directory, asset.name), Buffer.from(response.data));
    }
    validateAssets(directory, plan);
  } finally {
    fs.rmSync(directory, { recursive: true });
  }
}

async function resolveTagCommit(github, repo, target) {
  while (target.type === 'tag') {
    target = (await github.rest.git.getTag({ ...repo, tag_sha: target.sha })).data.object;
  }
  if (target.type !== 'commit' || !/^[a-f0-9]{40}$/.test(target.sha)) {
    throw new Error('Release tag must resolve to a commit');
  }
  return target.sha;
}

async function verifyPreviousRelease(github, repo, tag) {
  const previous = await findRelease(github, repo, tag);
  if (!previous || previous.draft) {
    throw new Error(`Previous release ${tag} is incomplete; resolve it before advancing the sequence`);
  }
  const ref = (await github.rest.git.getRef({ ...repo, ref: `tags/${tag}` })).data;
  const sha = await resolveTagCommit(github, repo, ref.object);
  await verifyRemote(github, repo, previous, { version: tag.slice(1), sha });
}

async function prepare({ github, context, core }) {
  const plan = planFromGit(context.sha, true);
  const release = await findRelease(github, context.repo, plan.tag);
  if (release && !release.draft) {
    await verifyRemote(github, context.repo, release, plan);
    core.notice(`Verified existing ${plan.tag}; keeping published assets unchanged.`);
    core.setOutput('publish', 'false');
    return;
  }
  const head = await github.rest.git.getRef({ ...context.repo, ref: 'heads/main' });
  if (head.data.object.sha !== context.sha) {
    core.notice('A newer main commit exists; this run will not publish.');
    core.setOutput('publish', 'false');
    return;
  }
  assertReleaseReady(JSON.parse(fs.readFileSync('installer/release-readiness.json', 'utf8')));
  if (!plan.existingTag && plan.previousTag) {
    await verifyPreviousRelease(github, context.repo, plan.previousTag);
  }
  core.setOutput('publish', 'true');
  core.setOutput('plan', JSON.stringify(plan));
  core.setOutput('version', plan.version);
}

async function publish({ github, context, core }, plan, directory) {
  validateAssets(directory, plan);
  const repo = context.repo;
  if (plan.sha !== context.sha) throw new Error('Release SHA differs from workflow SHA');
  const head = await github.rest.git.getRef({ ...repo, ref: 'heads/main' });
  if (head.data.object.sha !== plan.sha) throw new Error('Main advanced during packaging; rerun the latest SHA');
  assertReleaseReady(JSON.parse(fs.readFileSync('installer/release-readiness.json', 'utf8')));
  let tagRef;
  try {
    tagRef = (await github.rest.git.getRef({ ...repo, ref: `tags/${plan.tag}` })).data;
  } catch (error) {
    if (error.status !== 404) throw error;
    const tag = (await github.rest.git.createTag({
      ...repo, tag: plan.tag, message: `MissionView release ${plan.tag}\n\nSource: ${plan.sha}`,
      object: plan.sha, type: 'commit'
    })).data;
    tagRef = (await github.rest.git.createRef({
      ...repo, ref: `refs/tags/${plan.tag}`, sha: tag.sha
    })).data;
  }
  const tagCommit = await resolveTagCommit(github, repo, tagRef.object);
  if (tagCommit !== plan.sha) throw new Error('Refusing to move an existing tag');
  let release = await findRelease(github, repo, plan.tag);
  if (release && !release.draft) {
    await verifyRemote(github, repo, release, plan);
    core.notice(`Verified existing ${plan.tag}; nothing to publish.`);
    return;
  }
  if (!release) {
    release = (await github.rest.repos.createRelease({
      ...repo, tag_name: plan.tag, target_commitish: plan.sha,
      name: `MissionView ${plan.version}`, draft: true, prerelease: false,
      body: `Windows x64 installer and self-contained ZIP. No .NET installation required.\n\n` +
        `Requires Windows 11 24H2 (build 26100) or later. Unsigned binaries may trigger SmartScreen.\n\n` +
        `Verify downloads with SHA256SUMS.txt.\n\nSource: ${plan.sha}`
    })).data;
  }
  const previousAssets = await github.paginate(github.rest.repos.listReleaseAssets, {
    ...repo, release_id: release.id, per_page: 100
  });
  if (previousAssets.some(asset => !expectedAssets(plan.version).includes(asset.name))) {
    throw new Error('Refusing to replace unknown assets in an existing draft');
  }
  for (const asset of previousAssets) {
    await github.rest.repos.deleteReleaseAsset({ ...repo, asset_id: asset.id });
  }
  for (const name of expectedAssets(plan.version)) {
    const data = fs.readFileSync(path.join(directory, name));
    await github.rest.repos.uploadReleaseAsset({
      ...repo, release_id: release.id, name, data,
      headers: { 'content-type': 'application/octet-stream', 'content-length': data.length }
    });
  }
  await verifyRemote(github, repo, release, plan);
  const latestHead = await github.rest.git.getRef({ ...repo, ref: 'heads/main' });
  if (latestHead.data.object.sha !== plan.sha) {
    throw new Error('Main advanced during transfers; keeping the release draft for review');
  }
  await github.rest.repos.updateRelease({ ...repo, release_id: release.id, draft: false, make_latest: 'true' });
  core.notice(`Published ${plan.tag} from ${plan.sha}.`);
}

module.exports = { prepare, publish, findRelease, verifyRemote, verifyPreviousRelease };
