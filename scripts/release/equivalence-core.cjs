'use strict';

// Prototype only: production must bind a reviewed constant policy, not caller configuration.
const crypto = require('node:crypto');
const assert = require('node:assert/strict');

function objectId(type, bytes) {
  assert(Buffer.isBuffer(bytes), 'Git object bytes required');
  return crypto.createHash('sha1').update(`${type} ${bytes.length}\0`).update(bytes).digest('hex');
}

function parseCommit(raw) {
  assert(Buffer.isBuffer(raw), 'Commit bytes required');
  const separator = raw.indexOf('\n\n');
  assert(separator > 0, 'Missing commit header separator');
  const header = raw.subarray(0, separator);
  assert(!header.includes(0) && !header.includes(13), 'Invalid commit header bytes');
  const records = [];
  for (const line of header.toString('latin1').split('\n')) {
    if (line.startsWith(' ')) {
      assert(records.at(-1)?.key === 'gpgsig', 'Unexpected multiline header');
      records.at(-1).lines.push(line);
    } else {
      const match = /^(tree|parent|author|committer|encoding|gpgsig) (.*)$/.exec(line);
      assert(match, 'Unreviewed commit header');
      records.push({ key: match[1], value: match[2], lines: [line] });
    }
  }
  for (const key of ['tree', 'author', 'committer']) {
    assert.equal(records.filter(record => record.key === key).length, 1, `Expected one ${key}`);
  }
  for (const key of ['encoding', 'gpgsig']) {
    assert(records.filter(record => record.key === key).length <= 1, `Duplicate ${key}`);
  }
  assert.equal(records[0].key, 'tree', 'Tree must be first');
  for (const record of records.filter(record => ['tree', 'parent'].includes(record.key))) {
    assert.match(record.value, /^[a-f0-9]{40}$/, 'Invalid Git object reference');
  }
  return { records, message: raw.subarray(separator + 2) };
}

function createVerifier(reviewedPolicy) {
  const policy = structuredClone(reviewedPolicy);
  const sha = value => assert.match(value, /^[a-f0-9]{40}$/, 'Invalid pinned SHA');
  sha(policy.upstream);
  sha(policy.tag.object);
  sha(policy.oldTip);
  sha(policy.newTip);
  assert.equal(policy.entries.length, 6, 'Expected exactly six mappings');
  const byOld = new Map();
  const newIds = new Set();
  for (const entry of policy.entries) {
    sha(entry.old);
    sha(entry.new);
    assert(entry.old !== entry.new, 'Unchanged mapping entry');
    assert(!byOld.has(entry.old) && !newIds.has(entry.new), 'Mapping must be bijective');
    assert.equal(typeof entry.removeSignature, 'boolean', 'Explicit signature policy required');
    assert.equal(Object.hasOwn(entry, 'before'), Object.hasOwn(entry, 'after'), 'Incomplete message policy');
    if (Object.hasOwn(entry, 'before')) {
      assert.equal(typeof entry.before, 'string', 'Pinned old message required');
      assert.equal(typeof entry.after, 'string', 'Pinned new message required');
      assert.notEqual(entry.before, entry.after, 'Message change must differ');
    }
    byOld.set(entry.old, entry);
    newIds.add(entry.new);
  }
  assert(!byOld.has(policy.upstream) && !newIds.has(policy.upstream), 'Upstream cannot be rewritten');
  assert([...newIds].every(id => !byOld.has(id)), 'Old/new object domains overlap');
  assert.equal(byOld.get(policy.oldTip)?.new, policy.newTip, 'Tip mapping missing');
  assert.equal(policy.entries.filter(entry => Object.hasOwn(entry, 'after')).length, 2, 'Expected two message changes');
  assert.equal(policy.entries.filter(entry => entry.removeSignature).length, 4, 'Expected four signature removals');

  return function verify({ tagName, tagObject, tagRaw, readCommit, isAncestor, workflowSha }) {
    sha(workflowSha);
    assert.equal(tagName, policy.tag.name, 'Unreviewed release tag');
    assert.equal(tagObject, policy.tag.object, 'Release tag object changed');
    assert.equal(objectId('tag', tagRaw), policy.tag.object, 'Tag object bytes differ');
    const tagHeader = tagRaw.subarray(0, tagRaw.indexOf('\n\n')).toString('utf8').split('\n');
    assert.equal(tagHeader[0], `object ${policy.oldTip}`, 'Tag must retain original source');
    assert.equal(tagHeader[1], 'type commit', 'Tag must point directly to original commit');
    assert.equal(tagHeader[2], `tag ${policy.tag.name}`, 'Tag name differs');
    const visited = new Set();
    const visiting = new Set();
    let changes = 0;
    let removals = 0;

    function check(oldId) {
      if (visited.has(oldId)) return;
      assert(!visiting.has(oldId), 'Mapping cycle');
      visiting.add(oldId);
      const entry = byOld.get(oldId);
      assert(entry, 'Unmapped original parent');
      const oldRaw = readCommit(entry.old);
      const newRaw = readCommit(entry.new);
      assert.equal(objectId('commit', oldRaw), entry.old, 'Old commit hash differs');
      assert.equal(objectId('commit', newRaw), entry.new, 'New commit hash differs');
      const original = parseCommit(oldRaw);
      parseCommit(newRaw);
      const parents = original.records.filter(record => record.key === 'parent');
      assert(parents.length >= 1 && parents.length <= 2, 'Unexpected topology');
      let changedParents = false;
      const lines = [];
      for (const record of original.records) {
        if (record.key === 'parent') {
          if (record.value !== policy.upstream) {
            assert(byOld.has(record.value), 'Parent lies outside pinned history');
            check(record.value);
          }
          const replacement = byOld.get(record.value)?.new ?? record.value;
          changedParents ||= replacement !== record.value;
          lines.push(`parent ${replacement}`);
        } else if (record.key === 'gpgsig') {
          assert(entry.removeSignature, 'Unreviewed signature removal');
          removals++;
        } else {
          lines.push(...record.lines);
        }
      }
      const signatures = original.records.filter(record => record.key === 'gpgsig').length;
      assert.equal(signatures, Number(entry.removeSignature), 'Signature policy differs from original');
      if (entry.removeSignature) assert(changedParents, 'Only changed-parent signatures may be removed');
      let message = original.message;
      if (Object.hasOwn(entry, 'after')) {
        assert(message.equals(Buffer.from(entry.before, 'utf8')), 'Original message differs from policy');
        message = Buffer.from(entry.after, 'utf8');
        changes++;
      }
      const expected = Buffer.concat([Buffer.from(lines.join('\n') + '\n\n', 'latin1'), message]);
      assert(newRaw.equals(expected), 'Full commit preimage differs from approved transformation');
      assert.equal(objectId('commit', expected), entry.new, 'Transformed commit hash differs');
      visiting.delete(oldId);
      visited.add(oldId);
    }

    check(policy.oldTip);
    assert.equal(visited.size, 6, 'Mapping contains disconnected or omitted history');
    assert.equal(changes, 2, 'Wrong message change count');
    assert.equal(removals, 4, 'Wrong signature removal count');
    assert.equal(isAncestor(policy.newTip, workflowSha), true, 'Equivalent release is not a real ancestor');
    return Object.freeze({
      workflowSha,
      equivalentTip: policy.newTip,
      originalSourceSha: policy.oldTip,
      originalTag: policy.tag.name,
      originalTagObject: policy.tag.object,
      alreadyPublishedEquivalent: workflowSha === policy.newTip
    });
  };
}

module.exports = { createVerifier, objectId };
