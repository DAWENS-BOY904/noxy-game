// Dependency-free GLB inspector: validates scale, pivot, orientation, geometry, materials, textures and licence metadata
// without needing Unity or Blender.   node glb-inspect.mjs <file.glb> [more.glb ...]   (add --json for machine output)
import { readFileSync, existsSync, statSync } from 'node:fs';
import { basename } from 'node:path';

const args = process.argv.slice(2);
const asJson = args.includes('--json');
const files = args.filter(a => !a.startsWith('--'));
if (!files.length) { console.error('usage: node glb-inspect.mjs <file.glb> ...'); process.exit(2); }

// column-major 4x4 helpers
const I4 = () => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
const mul = (a, b) => { const o = new Array(16).fill(0); for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) for (let k = 0; k < 4; k++) o[c * 4 + r] += a[k * 4 + r] * b[c * 4 + k]; return o; };
function local(n) {
  if (n.matrix) return n.matrix;
  const [x, y, z, w] = n.rotation || [0, 0, 0, 1], [sx, sy, sz] = n.scale || [1, 1, 1], [tx, ty, tz] = n.translation || [0, 0, 0];
  return [
    (1 - 2 * (y * y + z * z)) * sx, (2 * (x * y + z * w)) * sx, (2 * (x * z - y * w)) * sx, 0,
    (2 * (x * y - z * w)) * sy, (1 - 2 * (x * x + z * z)) * sy, (2 * (y * z + x * w)) * sy, 0,
    (2 * (x * z + y * w)) * sz, (2 * (y * z - x * w)) * sz, (1 - 2 * (x * x + y * y)) * sz, 0,
    tx, ty, tz, 1];
}
const xf = (m, p) => [m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12], m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13], m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14]];

function pngSize(b) { return b.length > 24 && b.readUInt32BE(0) === 0x89504e47 ? [b.readUInt32BE(16), b.readUInt32BE(20)] : null; }
function jpgSize(b) {
  if (b[0] !== 0xff || b[1] !== 0xd8) return null;
  let i = 2;
  while (i < b.length - 9) {
    if (b[i] !== 0xff) { i++; continue; }
    const m = b[i + 1];
    if (m >= 0xc0 && m <= 0xcf && m !== 0xc4 && m !== 0xc8 && m !== 0xcc) return [b.readUInt16BE(i + 7), b.readUInt16BE(i + 5)];
    i += 2 + b.readUInt16BE(i + 2);
  }
  return null;
}

function inspect(file) {
  const r = { file, name: basename(file), exists: existsSync(file) };
  if (!r.exists) return r;
  const buf = readFileSync(file);
  r.bytes = statSync(file).size;
  if (buf.toString('ascii', 0, 4) !== 'glTF') { r.error = 'not a binary glTF (missing glTF magic)'; return r; }
  r.glbVersion = buf.readUInt32LE(4);
  let off = 12, json = null, bin = null;
  while (off < buf.length) {
    const len = buf.readUInt32LE(off), type = buf.toString('ascii', off + 4, off + 8);
    const data = buf.subarray(off + 8, off + 8 + len);
    if (type === 'JSON') json = JSON.parse(data.toString('utf8'));
    else if (type.startsWith('BIN')) bin = data;
    off += 8 + len;
  }
  if (!json) { r.error = 'no JSON chunk'; return r; }

  r.asset = json.asset || {};
  r.licenceMetadata = { copyright: json.asset?.copyright || null, extras: json.asset?.extras || null, sceneExtras: (json.scenes || []).map(s => s.extras).filter(Boolean) };
  r.extensionsUsed = json.extensionsUsed || []; r.extensionsRequired = json.extensionsRequired || [];
  r.counts = { scenes: (json.scenes || []).length, nodes: (json.nodes || []).length, meshes: (json.meshes || []).length, materials: (json.materials || []).length, textures: (json.textures || []).length, images: (json.images || []).length, animations: (json.animations || []).length, skins: (json.skins || []).length, cameras: (json.cameras || []).length };

  // world transforms + instancing
  const nodes = json.nodes || [];
  const world = new Array(nodes.length);
  const meshInstances = new Map();
  const visit = (i, parent) => {
    const m = mul(parent, local(nodes[i])); world[i] = m;
    if (nodes[i].mesh !== undefined) (meshInstances.get(nodes[i].mesh) || meshInstances.set(nodes[i].mesh, []).get(nodes[i].mesh)).push(i);
    (nodes[i].children || []).forEach(c => visit(c, m));
  };
  const scene = (json.scenes || [])[json.scene || 0];
  (scene ? scene.nodes : nodes.map((_, i) => i)).forEach(i => visit(i, I4()));

  const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
  let tris = 0, verts = 0, drawPrims = 0, noNormals = 0, noUV = 0, nonTri = 0;
  const matUse = new Map();
  for (const [mi, insts] of meshInstances) {
    for (const prim of json.meshes[mi].primitives) {
      drawPrims += insts.length;
      if (prim.mode !== undefined && prim.mode !== 4) nonTri++;
      const pos = json.accessors[prim.attributes.POSITION];
      if (!prim.attributes.NORMAL) noNormals++;
      if (!prim.attributes.TEXCOORD_0) noUV++;
      verts += pos.count * insts.length;
      tris += (prim.indices !== undefined ? json.accessors[prim.indices].count : pos.count) / 3 * insts.length;
      if (prim.material !== undefined) matUse.set(prim.material, (matUse.get(prim.material) || 0) + insts.length);
      if (pos.min && pos.max) {
        for (const ni of insts) for (let c = 0; c < 8; c++) {
          const p = xf(world[ni], [c & 1 ? pos.max[0] : pos.min[0], c & 2 ? pos.max[1] : pos.min[1], c & 4 ? pos.max[2] : pos.min[2]]);
          for (let k = 0; k < 3; k++) { if (p[k] < min[k]) min[k] = p[k]; if (p[k] > max[k]) max[k] = p[k]; }
        }
      }
    }
  }
  r.geometry = { triangles: Math.round(tris), vertices: verts, drawablePrimitives: drawPrims, primitivesWithoutNormals: noNormals, primitivesWithoutUV: noUV, nonTrianglePrimitives: nonTri };
  const size = max.map((v, k) => v - min[k]);
  r.bounds = isFinite(min[0]) ? {
    min: min.map(v => +v.toFixed(3)), max: max.map(v => +v.toFixed(3)), sizeMeters: size.map(v => +v.toFixed(3)),
    // pivot: where the model origin sits relative to the bounding box (0 = at min, 1 = at max) per axis
    originInBox: min.map((v, k) => (size[k] > 0 ? +((0 - v) / size[k]).toFixed(3) : 0)),
  } : null;

  // materials
  const mats = json.materials || [];
  r.materials = {
    total: mats.length, used: matUse.size,
    alphaBlend: mats.filter(m => m.alphaMode === 'BLEND').length, alphaMask: mats.filter(m => m.alphaMode === 'MASK').length,
    doubleSided: mats.filter(m => m.doubleSided).length,
    withBaseColorTexture: mats.filter(m => m.pbrMetallicRoughness?.baseColorTexture).length,
    withNormalMap: mats.filter(m => m.normalTexture).length,
    extensions: [...new Set(mats.flatMap(m => Object.keys(m.extensions || {})))],
  };

  // images
  const imgs = [];
  let texBytes = 0;
  for (const im of json.images || []) {
    const e = { name: im.name || '', mime: im.mimeType || (im.uri ? 'external:' + im.uri : '?') };
    if (im.bufferView !== undefined && bin) {
      const bv = json.bufferViews[im.bufferView];
      const data = bin.subarray(bv.byteOffset || 0, (bv.byteOffset || 0) + bv.byteLength);
      e.bytes = bv.byteLength;
      const wh = pngSize(data) || jpgSize(data);
      if (wh) { e.width = wh[0]; e.height = wh[1]; texBytes += wh[0] * wh[1] * 4 * 1.33; e.pow2 = (wh[0] & (wh[0] - 1)) === 0 && (wh[1] & (wh[1] - 1)) === 0; }
    } else if (im.uri) e.external = true;
    imgs.push(e);
  }
  r.images = imgs;
  r.textureMemoryMBUncompressedEstimate = +(texBytes / 1048576).toFixed(1);
  r.animations = (json.animations || []).map(a => a.name || '(unnamed)');

  // verdicts
  const issues = [];
  if (r.extensionsRequired.length) issues.push('requires glTF extensions: ' + r.extensionsRequired.join(', ') + ' (importer must support them)');
  if (r.extensionsUsed.includes('KHR_draco_mesh_compression')) issues.push('Draco-compressed meshes: needs an importer with Draco support (glTFast includes it)');
  if (r.extensionsUsed.some(e => /basisu|webp/.test(e))) issues.push('uses ' + r.extensionsUsed.filter(e => /basisu|webp/.test(e)).join('/') + ' textures');
  if (!r.bounds) issues.push('could not compute bounds (accessor min/max missing)');
  else {
    const m = Math.max(...size);
    if (m > 500) issues.push(`very large (${m.toFixed(0)} m longest side): likely wrong unit scale`);
    if (m < 0.2) issues.push(`very small (${m.toFixed(3)} m longest side): likely wrong unit scale`);
  }
  if (tris > 150000) issues.push(`high polygon count (${Math.round(tris)} tris): needs LODs / decimation for mobile`);
  if (noNormals) issues.push(`${noNormals} primitives lack normals`);
  if (imgs.some(i => i.width > 2048 || i.height > 2048)) issues.push('textures above 2048 px: compress/downscale for mobile');
  if (imgs.some(i => i.pow2 === false)) issues.push('non-power-of-two textures present');
  if (r.materials.alphaBlend) issues.push(`${r.materials.alphaBlend} alpha-blended materials (overdraw cost on mobile)`);
  if (drawPrims > 300) issues.push(`${drawPrims} drawable primitives: merge/atlas to cut draw calls`);
  if (!r.licenceMetadata.copyright && !r.licenceMetadata.extras) issues.push('NO licence/author metadata embedded: licence must be confirmed from the download source');
  r.issues = issues;
  return r;
}

const reports = files.map(inspect);
if (asJson) { console.log(JSON.stringify(reports, null, 2)); process.exit(0); }
for (const r of reports) {
  console.log('='.repeat(100)); console.log(r.name, r.exists ? `(${(r.bytes / 1048576).toFixed(1)} MB)` : '- FILE NOT FOUND');
  if (!r.exists || r.error) { console.log('  ' + (r.error || 'missing')); continue; }
  console.log(`  generator: ${r.asset.generator || '?'}   glTF ${r.asset.version}   copyright: ${r.licenceMetadata.copyright || '(none)'}`);
  if (r.licenceMetadata.extras) console.log('  extras:', JSON.stringify(r.licenceMetadata.extras).slice(0, 300));
  console.log('  extensions used:', r.extensionsUsed.join(', ') || '(none)', '| required:', r.extensionsRequired.join(', ') || '(none)');
  console.log('  counts:', JSON.stringify(r.counts));
  console.log('  geometry:', JSON.stringify(r.geometry));
  if (r.bounds) console.log(`  bounds (m): size ${r.bounds.sizeMeters.join(' x ')}  min ${r.bounds.min.join(',')}  max ${r.bounds.max.join(',')}  origin-in-box ${r.bounds.originInBox.join(',')}`);
  console.log('  materials:', JSON.stringify(r.materials));
  console.log(`  images: ${r.images.length}  est. uncompressed texture memory ${r.textureMemoryMBUncompressedEstimate} MB  ` + r.images.slice(0, 8).map(i => (i.width ? i.width + 'x' + i.height : '?')).join(' '));
  if (r.animations.length) console.log('  animations:', r.animations.join(', '));
  console.log('  ISSUES:'); (r.issues.length ? r.issues : ['none detected']).forEach(i => console.log('   - ' + i));
}
