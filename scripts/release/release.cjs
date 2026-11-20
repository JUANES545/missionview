'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');

function parseVersion(tag) {
  const match = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(tag);
  if (!match) throw new Error(`Invalid release tag: ${tag}`);
  const parts = match.slice(1).map(Number);
  if (parts.some(value => value > 65534)) throw new Error(`Windows version overflow: ${tag}`);
  return parts;
}

function planRelease(tags, sha, nextVersion = null) {
  if (!/^[a-f0-9]{40}$/.test(sha)) throw new Error('Expected a full commit SHA');
  const versions = tags.filter(tag => /^v\d/.test(tag.name)).map(tag => ({
    ...tag, parts: parseVersion(tag.name)
  }));
  versions.sort((a, b) => a.parts[0] - b.parts[0] ||
    a.parts[1] - b.parts[1] || a.parts[2] - b.parts[2]);
  const sameCommit = versions.filter(tag => tag.sha === sha);
  if (sameCommit.length > 1) throw new Error('Multiple release versions point to this SHA');
  const latest = versions.at(-1);
  const tag = sameCommit[0]?.name ?? (nextVersion !== null ? `v${nextVersion}` :
    (latest ? `v${latest.parts[0]}.${latest.parts[1]}.${latest.parts[2] + 1}` : 'v0.1.0'));
  const proposed = parseVersion(tag);
  if (!sameCommit.length && latest) {
    const difference = proposed[0] - latest.parts[0] || proposed[1] - latest.parts[1] ||
      proposed[2] - latest.parts[2];
    if (difference <= 0) throw new Error('Prepared version must advance the release sequence');
  }
  return { tag, version: tag.slice(1), sha, existingTag: sameCommit.length === 1,
    previousTag: latest?.name ?? null };
}

function planFromTagRefs(refs, sha, nextVersion = null) {
  const owned = refs.filter(ref => ref.type === 'tag' &&
    ref.subject === `MissionView release ${ref.name}`);
  const plan = planRelease(owned, sha, nextVersion);
  if (!plan.existingTag && refs.some(ref => ref.name === plan.tag)) {
    throw new Error(`Version collides with an unrelated tag: ${plan.tag}`);
  }
  return plan;
}

function assertGitFlow(base, head) {
  if ((base === 'main' && head === 'develop') ||
      (base === 'develop' && /^feature\/[a-z0-9][a-z0-9/-]*$/.test(head))) return;
  throw new Error(`Expected feature/* -> develop -> main; got ${head} -> ${base}`);
}

function planFromGit(sha, verifyAncestry = false) {
  const git = (...args) => execFileSync('git', args, { encoding: 'utf8' }).trim();
  const tags = git('for-each-ref', '--format=%(refname:short)%09%(objecttype)%09%(contents:subject)',
    'refs/tags').split(/\r?\n/).filter(Boolean).map(line => {
    const [name, type, subject] = line.split('\t');
    return { name, type, subject,
      sha: type === 'tag' && subject === `MissionView release ${name}` ? git('rev-parse', `${name}^{commit}`) : null };
  });
  const policy = JSON.parse(fs.readFileSync(path.join(__dirname, '../../installer/release-readiness.json'), 'utf8'));
  const plan = planFromTagRefs(tags, sha, policy.nextVersion ?? null);
  if (verifyAncestry && !plan.existingTag && plan.previousTag) {
    git('merge-base', '--is-ancestor', plan.previousTag, sha);
  }
  return plan;
}

function expectedAssets(version) {
  parseVersion(`v${version}`);
  return [`MissionView-${version}-win-x64.zip`, `MissionView-${version}-Setup.exe`,
    'release-manifest.json', 'SHA256SUMS.txt'];
}

function assertReleaseReady(policy) {
  if (policy.publicReleaseApproved !== true || !Array.isArray(policy.blockers) ||
      policy.blockers.length !== 0) {
    throw new Error(`Public release blocked: ${JSON.stringify(policy.blockers)}`);
  }
}

function validateAssets(directory, plan) {
  const expected = expectedAssets(plan.version).sort();
  const actual = fs.readdirSync(directory).sort();
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error('Release directory must contain exactly the four expected assets');
  }
  const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'release-manifest.json')));
  if (manifest.version !== plan.version || manifest.sourceSha !== plan.sha ||
      manifest.runtime !== 'win-x64' || manifest.selfContained !== true) {
    throw new Error('Release manifest does not match this build');
  }
  const lines = fs.readFileSync(path.join(directory, 'SHA256SUMS.txt'), 'utf8').trim().split(/\r?\n/);
  const hashed = expected.filter(name => name !== 'SHA256SUMS.txt');
  if (lines.length !== hashed.length) throw new Error('Incomplete checksum manifest');
  const seen = new Set();
  for (const line of lines) {
    const match = /^([a-f0-9]{64})  ([a-zA-Z0-9.-]+)$/.exec(line);
    if (!match || !hashed.includes(match[2]) || seen.has(match[2])) {
      throw new Error('Invalid checksum entry');
    }
    seen.add(match[2]);
    const hash = crypto.createHash('sha256').update(fs.readFileSync(path.join(directory, match[2]))).digest('hex');
    if (hash !== match[1]) throw new Error(`Checksum mismatch: ${match[2]}`);
  }
}

module.exports = { parseVersion, planRelease, planFromTagRefs, assertGitFlow, planFromGit, expectedAssets,
  validateAssets, assertReleaseReady };

if (require.main === module) {
  if (process.env.GITHUB_EVENT_NAME === 'pull_request') {
    assertGitFlow(process.env.GITHUB_BASE_REF, process.env.GITHUB_HEAD_REF);
    if (process.env.GITHUB_BASE_REF === 'main') {
      const event = JSON.parse(fs.readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
      if (event.pull_request.head.repo.full_name !== event.pull_request.base.repo.full_name) {
        throw new Error('Main promotions must come from this repository develop branch');
      }
    }
  }
  const plan = planFromGit(process.env.GITHUB_SHA ?? execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim());
  console.log(JSON.stringify(plan));
  if (process.env.GITHUB_OUTPUT) {
    fs.appendFileSync(process.env.GITHUB_OUTPUT, `version=${plan.version}\ntag=${plan.tag}\n`);
  }
}
