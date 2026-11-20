'use strict';

const assert = require('node:assert/strict');
const { createVerifier } = require('./equivalence-core.cjs');

// This finite binding applies only to the reviewed original v0.1.0 history.
const POLICY = {
  upstream: 'a01fb74b3db664344a9ce2f95ca01194bfddfd9f',
  oldTip: '445ca5936a6ddabcac0cc4010509912b406e9398',
  newTip: 'a02cb3db856d5e55ca624feefc26546fbf1e7bf0',
  tag: { name: 'v0.1.0', object: '07bb6595afa0cdfa1b38ebb55f487c7374d530b6' },
  entries: [
    { old: 'd21ccc9667aca1479568abf61440ada74810b617',
      new: '68654c95ba8bc6dfe37a39cad964ea5d8bf0fb70', removeSignature: false,
      before: 'Prepara MissionView 0.1.0 y su flujo de entrega\n',
      after: 'Prepare MissionView 0.1.0 and its delivery workflow\n' },
    { old: '5ef73393fa8b0568658ceeea9d876ebf63e8d1b7',
      new: 'c6a66d78d588a70fe6204739b10388f43d7e23eb', removeSignature: true },
    { old: '0c8fe9b84db5097471246e7f6d503e9e3e98697b',
      new: 'a473720b4b6e8afa8037fb055bb78d3d36c7c97c', removeSignature: true },
    { old: 'e720dba40b3b34f73b80f90eb9959f8f2ff3658a',
      new: 'b042e1122e2229baa4f0ee0cc6ef2cc81ec4e93c', removeSignature: false,
      before: 'Documenta la descarga e instalación de MissionView\n',
      after: 'Document MissionView download and installation\n' },
    { old: 'da33326fdc8321400603159a0ed6545c2856a8dd',
      new: 'ce33bc03ea6492649f9b4962ec421cb84f9999c3', removeSignature: true },
    { old: '445ca5936a6ddabcac0cc4010509912b406e9398',
      new: 'a02cb3db856d5e55ca624feefc26546fbf1e7bf0', removeSignature: true }
  ]
};

if (POLICY !== null) {
  assert.equal(POLICY.upstream, 'a01fb74b3db664344a9ce2f95ca01194bfddfd9f');
  assert.equal(POLICY.oldTip, '445ca5936a6ddabcac0cc4010509912b406e9398');
  assert.equal(POLICY.tag.name, 'v0.1.0');
  assert.equal(POLICY.tag.object, '07bb6595afa0cdfa1b38ebb55f487c7374d530b6');
  const expected = [
    ['d21ccc9667aca1479568abf61440ada74810b617', false,
      'Prepara MissionView 0.1.0 y su flujo de entrega\n',
      'Prepare MissionView 0.1.0 and its delivery workflow\n'],
    ['5ef73393fa8b0568658ceeea9d876ebf63e8d1b7', true],
    ['0c8fe9b84db5097471246e7f6d503e9e3e98697b', true],
    ['e720dba40b3b34f73b80f90eb9959f8f2ff3658a', false,
      'Documenta la descarga e instalación de MissionView\n',
      'Document MissionView download and installation\n'],
    ['da33326fdc8321400603159a0ed6545c2856a8dd', true],
    ['445ca5936a6ddabcac0cc4010509912b406e9398', true]
  ];
  assert.deepEqual(POLICY.entries.map(entry =>
    [entry.old, entry.removeSignature, ...(Object.hasOwn(entry, 'before') ? [entry.before, entry.after] : [])]), expected);
}

module.exports = createVerifier(POLICY);
