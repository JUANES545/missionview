'use strict';

const { execFileSync } = require('node:child_process');
const reviewedVerifier = require('./reviewed-history-verifier.cjs');

function isAncestor(ancestor, descendant) {
  try {
    execFileSync('git', ['merge-base', '--is-ancestor', ancestor, descendant], { stdio: 'pipe' });
    return true;
  } catch (error) {
    if (error.status === 1) return false;
    throw error;
  }
}

function verifyHistoryEquivalence(workflowSha, previousTag) {
  if (previousTag !== 'v0.1.0') throw new Error('No history equivalence is permitted for this release tag');
  if (reviewedVerifier === null) {
    throw new Error('History equivalence disabled: the reviewed six-entry MissionView mapping is unavailable');
  }
  const tagObject = execFileSync('git', ['rev-parse', '--verify', 'refs/tags/v0.1.0'],
    { encoding: 'utf8' }).trim();
  return reviewedVerifier({
    workflowSha,
    tagName: previousTag,
    tagObject,
    tagRaw: execFileSync('git', ['cat-file', 'tag', tagObject]),
    readCommit: sha => execFileSync('git', ['cat-file', 'commit', sha]),
    isAncestor
  });
}

module.exports = { isAncestor, verifyHistoryEquivalence };
