'use strict';

const crypto = require('node:crypto');

// Immutable original v0.1.0 evidence; never derive these pins from downloaded metadata.
const SOURCE = '445ca5936a6ddabcac0cc4010509912b406e9398';
const TAG_OBJECT = '07bb6595afa0cdfa1b38ebb55f487c7374d530b6';
const ASSETS = Object.freeze({
  'MissionView-0.1.0-Setup.exe': Object.freeze({
    size: 61289058, sha256: '6b7c2bfb0c14d9fc7ba7022a6201bd701dbf2e5e3fe4a136bf688d372ccff365'
  }),
  'MissionView-0.1.0-win-x64.zip': Object.freeze({
    size: 88794408, sha256: '999a0c2d0a9abe03eeb3607b014b3be4f0a7e789ccf1c72f987314a08e32d45f'
  }),
  'release-manifest.json': Object.freeze({
    size: 192, sha256: '2faa2d281762cbf8582e7bc77a1bcf649bb1d647d1c848b93c987170d8fe7a05'
  }),
  'SHA256SUMS.txt': Object.freeze({
    size: 281, sha256: 'e10b26f29e2c15da182c317dd1315c0c1e496f8c3400abaffdc7eb14530d3af5'
  })
});

function assertHistoricalSource(plan) {
  if (plan.version === '0.1.0' && plan.sha !== SOURCE) {
    throw new Error('Historical v0.1.0 source differs from the immutable original');
  }
}

function assertHistoricalTag(ref) {
  if (ref.object.type !== 'tag' || ref.object.sha !== TAG_OBJECT) {
    throw new Error('Historical v0.1.0 annotated tag differs from the immutable original');
  }
}

function assertHistoricalAssetMetadata(asset) {
  const pinned = Object.hasOwn(ASSETS, asset.name) ? ASSETS[asset.name] : null;
  if (!pinned || asset.size !== pinned.size || asset.digest !== `sha256:${pinned.sha256}`) {
    throw new Error(`Historical v0.1.0 asset metadata differs: ${asset.name}`);
  }
}

function assertHistoricalAssetBytes(name, data) {
  const pinned = Object.hasOwn(ASSETS, name) ? ASSETS[name] : null;
  if (!pinned || data.length !== pinned.size ||
      crypto.createHash('sha256').update(data).digest('hex') !== pinned.sha256) {
    throw new Error(`Historical v0.1.0 asset bytes differ: ${name}`);
  }
}

module.exports = { assertHistoricalSource, assertHistoricalTag,
  assertHistoricalAssetMetadata, assertHistoricalAssetBytes };
