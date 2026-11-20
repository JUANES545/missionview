'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createVerifier, objectId } = require('./equivalence-core.cjs');

function fixture() {
  const upstream = '1'.repeat(40);
  const trees = ['2'.repeat(40), '3'.repeat(40)];
  const objects = new Map();
  const entries = [];
  function add(parents, tree, message, after, signed) {
    const prefix = [`tree ${tree}`, ...parents.map(parent => `parent ${parent}`),
      'author Fixture Author <fixture@example.invalid> 1700000000 -0500',
      'committer Fixture Committer <fixture@example.invalid> 1700000001 +0000',
      'encoding UTF-8'];
    const signature = ['gpgsig -----BEGIN PGP SIGNATURE-----', ' fixture-only-not-a-real-signature',
      ' -----END PGP SIGNATURE-----'];
    const original = Buffer.from([...prefix, ...(signed ? signature : [])].join('\n') + '\n\n' + message);
    const old = objectId('commit', original);
    const mapped = prefix.map(line => {
      if (!line.startsWith('parent ')) return line;
      const parent = line.slice(7);
      return `parent ${entries.find(entry => entry.old === parent)?.new ?? parent}`;
    });
    const rewritten = Buffer.from(mapped.join('\n') + '\n\n' + (after ?? message));
    const next = objectId('commit', rewritten);
    objects.set(old, original);
    objects.set(next, rewritten);
    entries.push({ old, new: next, removeSignature: signed,
      ...(after ? { before: message, after } : {}) });
    return old;
  }
  const first = add([upstream], trees[0], 'Fixture message one\n', 'Fixture replacement one\n', false);
  const featureMerge = add([upstream, first], trees[0], 'Fixture merge one\n', null, true);
  const mainMerge = add([upstream, featureMerge], trees[0], 'Fixture merge two\n', null, true);
  const second = add([featureMerge], trees[1], 'Fixture message two\n', 'Fixture replacement two\n', false);
  const develop = add([featureMerge, second], trees[1], 'Fixture merge three\n', null, true);
  const oldTip = add([mainMerge, develop], trees[1], 'Fixture merge four\n', null, true);
  const newTip = entries.at(-1).new;
  const tagRaw = Buffer.from(`object ${oldTip}\ntype commit\ntag v0.1.0\ntagger Fixture <fixture@example.invalid> 1700000002 +0000\n\nFixture release\n`);
  const policy = { upstream, oldTip, newTip, entries,
    tag: { name: 'v0.1.0', object: objectId('tag', tagRaw) } };
  const input = {
    tagName: policy.tag.name, tagObject: policy.tag.object, tagRaw,
    readCommit: id => {
      assert(objects.has(id), 'Missing fixture object');
      return objects.get(id);
    },
    isAncestor: (ancestor, target) => ancestor === newTip && [newTip, '4'.repeat(40)].includes(target),
    workflowSha: newTip
  };
  function mutate(index, replace) {
    const entry = policy.entries[index];
    const bytes = replace(objects.get(entry.new));
    const id = objectId('commit', bytes);
    objects.set(id, bytes);
    entry.new = id;
    if (index === policy.entries.length - 1) {
      policy.newTip = id;
      input.workflowSha = id;
      input.isAncestor = (ancestor, target) => ancestor === id && target === id;
    }
  }
  return { policy, input, objects, mutate };
}

test('validates six complete preimages while preserving original release source', () => {
  const { policy, input } = fixture();
  const result = createVerifier(policy)(input);
  assert.equal(result.workflowSha, policy.newTip);
  assert.equal(result.originalSourceSha, policy.oldTip);
  assert.equal(result.alreadyPublishedEquivalent, true);
  assert.notEqual(result.workflowSha, result.originalSourceSha);
  assert.equal(Object.hasOwn(result, 'existingTag'), false);
});

test('requires a real descendant for a later workflow SHA', () => {
  const { policy, input } = fixture();
  input.workflowSha = '4'.repeat(40);
  assert.equal(createVerifier(policy)(input).alreadyPublishedEquivalent, false);
  input.isAncestor = () => false;
  assert.throws(() => createVerifier(policy)(input), /not a real ancestor/);
});

test('policy is captured by value, not mutable caller configuration', () => {
  const { policy, input } = fixture();
  const verify = createVerifier(policy);
  policy.entries.length = 0;
  assert.equal(verify(input).alreadyPublishedEquivalent, true);
});

for (const [label, replace] of [
  ['tree', text => text.replace(/tree [a-f0-9]{40}/, `tree ${'5'.repeat(40)}`)],
  ['author identity', text => text.replace('Fixture Author', 'Different Author')],
  ['committer identity', text => text.replace('Fixture Committer', 'Different Committer')],
  ['author timestamp', text => text.replace('1700000000', '1700000003')],
  ['committer timestamp', text => text.replace('1700000001', '1700000003')],
  ['timezone', text => text.replace('-0500', '+0100')],
  ['encoding header', text => text.replace('encoding UTF-8', 'encoding ISO-8859-1')],
  ['header ordering', text => text.replace(
    /(author [^\n]+)\n(committer [^\n]+)/, '$2\n$1')],
  ['message', text => text.replace('Fixture merge four', 'Unapproved merge message')],
  ['message final newline', text => text.slice(0, -1)],
  ['parent order', text => text.replace(/(parent [a-f0-9]{40})\n(parent [a-f0-9]{40})/, '$2\n$1')],
  ['parent count', text => text.replace(/parent [a-f0-9]{40}\n/, '')],
  ['signature insertion', text => text.replace('\n\n', '\ngpgsig fabricated\n\n')],
  ['unreviewed header', text => text.replace('\n\n', '\nextra unexpected\n\n')],
  ['duplicate author', text => text.replace('\n\n', '\nauthor Extra <fixture@example.invalid> 1 +0000\n\n')]
]) {
  test(`rejects ${label} tampering even with matching candidate object SHA`, () => {
    const f = fixture();
    f.mutate(5, bytes => Buffer.from(replace(bytes.toString('utf8'))));
    assert.throws(() => createVerifier(f.policy)(f.input));
  });
}

test('rejects candidate bytes whose declared object SHA was not updated', () => {
  const f = fixture();
  f.objects.set(f.policy.newTip, Buffer.from('not the pinned object'));
  assert.throws(() => createVerifier(f.policy)(f.input), /New commit hash differs/);
});

test('rejects original bytes whose declared object SHA was not updated', () => {
  const f = fixture();
  f.objects.set(f.policy.oldTip, Buffer.from('not the original object'));
  assert.throws(() => createVerifier(f.policy)(f.input), /Old commit hash differs/);
});

test('rejects copying the original invalidated signature into rewritten merges', () => {
  const f = fixture();
  const original = f.objects.get(f.policy.oldTip).toString('utf8');
  const signature = original.slice(original.indexOf('gpgsig '), original.indexOf('\n\n'));
  f.mutate(5, bytes => Buffer.from(bytes.toString('utf8').replace('\n\n', `\n${signature}\n\n`)));
  assert.throws(() => createVerifier(f.policy)(f.input), /preimage differs/);
});

test('rejects an unsigned-copy policy that does not remove exactly four signatures', () => {
  const f = fixture();
  f.policy.entries[1].removeSignature = false;
  assert.throws(() => createVerifier(f.policy), /four signature removals/);
});

test('rejects removing a signature from an unsigned message commit instead of a signed merge', () => {
  const f = fixture();
  f.policy.entries[0].removeSignature = true;
  f.policy.entries[1].removeSignature = false;
  assert.throws(() => createVerifier(f.policy)(f.input), /signature|Signature/);
});

test('rejects omitted and extra approved message transformations', () => {
  for (const defect of ['omitted', 'extra']) {
    const f = fixture();
    if (defect === 'omitted') {
      delete f.policy.entries[0].before;
      delete f.policy.entries[0].after;
    } else {
      f.policy.entries[1].before = 'Fixture merge one\n';
      f.policy.entries[1].after = 'Unapproved third message change\n';
    }
    assert.throws(() => createVerifier(f.policy), /two message changes/);
  }
});

test('rejects wrong expected original and replacement messages', () => {
  for (const key of ['before', 'after']) {
    const f = fixture();
    f.policy.entries[0][key] += 'tampered';
    assert.throws(() => createVerifier(f.policy)(f.input), /message differs|preimage differs/);
  }
});

test('rejects partial, duplicate, disconnected, and overlapping maps', () => {
  for (const defect of ['partial', 'duplicate', 'disconnected', 'overlap']) {
    const f = fixture();
    if (defect === 'partial') f.policy.entries.pop();
    if (defect === 'duplicate') f.policy.entries[0].new = f.policy.entries[1].new;
    if (defect === 'disconnected') f.policy.entries[0].old = '6'.repeat(40);
    if (defect === 'overlap') f.policy.entries[0].new = f.policy.entries[1].old;
    assert.throws(() => createVerifier(f.policy)(f.input));
  }
});

test('rejects another tag, modified annotated object and original-source substitution', () => {
  for (const defect of ['name', 'object', 'bytes', 'source']) {
    const f = fixture();
    if (defect === 'name') f.input.tagName = 'v0.1.1';
    if (defect === 'object') f.input.tagObject = '7'.repeat(40);
    if (defect === 'bytes') f.input.tagRaw = Buffer.concat([f.input.tagRaw, Buffer.from('changed')]);
    if (defect === 'source') {
      f.input.tagRaw = Buffer.from(f.input.tagRaw.toString().replace(f.policy.oldTip, f.policy.newTip));
      f.input.tagObject = f.policy.tag.object = objectId('tag', f.input.tagRaw);
    }
    assert.throws(() => createVerifier(f.policy)(f.input));
  }
});

test('refuses to accept a truthy non-boolean ancestry response', () => {
  const { policy, input } = fixture();
  input.isAncestor = () => 'true';
  assert.throws(() => createVerifier(policy)(input), /not a real ancestor/);
});

test('propagates object access and ancestry errors without an equivalence fallback', () => {
  const first = fixture();
  first.input.readCommit = () => { throw new Error('object unavailable'); };
  assert.throws(() => createVerifier(first.policy)(first.input), /object unavailable/);
  const second = fixture();
  second.input.isAncestor = () => { throw new Error('Git ancestry error'); };
  assert.throws(() => createVerifier(second.policy)(second.input), /Git ancestry error/);
});
