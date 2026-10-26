// Lists the largest meshes of a GLB so oversized "ground plates", skyboxes or unit-scale problems can be found.
import { readFileSync } from 'node:fs';
const file = process.argv[2];
const buf = readFileSync(file);
let off = 12, json;
while (off < buf.length) { const len = buf.readUInt32LE(off), type = buf.toString('ascii', off + 4, off + 8); if (type === 'JSON') json = JSON.parse(buf.subarray(off + 8, off + 8 + len).toString('utf8')); off += 8 + len; }
const I4 = () => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
const mul = (a, b) => { const o = new Array(16).fill(0); for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) for (let k = 0; k < 4; k++) o[c * 4 + r] += a[k * 4 + r] * b[c * 4 + k]; return o; };
const local = n => { if (n.matrix) return n.matrix; const [x, y, z, w] = n.rotation || [0, 0, 0, 1], [sx, sy, sz] = n.scale || [1, 1, 1], [tx, ty, tz] = n.translation || [0, 0, 0];
  return [(1 - 2 * (y * y + z * z)) * sx, (2 * (x * y + z * w)) * sx, (2 * (x * z - y * w)) * sx, 0, (2 * (x * y - z * w)) * sy, (1 - 2 * (x * x + z * z)) * sy, (2 * (y * z + x * w)) * sy, 0, (2 * (x * z + y * w)) * sz, (2 * (y * z - x * w)) * sz, (1 - 2 * (x * x + y * y)) * sz, 0, tx, ty, tz, 1]; };
const xf = (m, p) => [m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12], m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13], m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14]];
const nodes = json.nodes, rows = [];
const visit = (i, parent, path) => {
  const m = mul(parent, local(nodes[i])); const n = nodes[i]; const p = path + '/' + (n.name || i);
  if (n.mesh !== undefined) {
    const min = [1e9, 1e9, 1e9], max = [-1e9, -1e9, -1e9]; let tris = 0;
    for (const prim of json.meshes[n.mesh].primitives) {
      const a = json.accessors[prim.attributes.POSITION]; tris += (prim.indices !== undefined ? json.accessors[prim.indices].count : a.count) / 3;
      for (let c = 0; c < 8; c++) { const q = xf(m, [c & 1 ? a.max[0] : a.min[0], c & 2 ? a.max[1] : a.min[1], c & 4 ? a.max[2] : a.min[2]]); for (let k = 0; k < 3; k++) { min[k] = Math.min(min[k], q[k]); max[k] = Math.max(max[k], q[k]); } }
    }
    rows.push({ path: p, size: max.map((v, k) => v - min[k]), center: max.map((v, k) => (v + min[k]) / 2), tris: Math.round(tris), mat: json.meshes[n.mesh].primitives.map(pr => (json.materials[pr.material] || {}).name).join('|') });
  }
  (n.children || []).forEach(c => visit(c, m, p));
};
json.scenes[json.scene || 0].nodes.forEach(i => visit(i, I4(), ''));
const ext = r => Math.max(...r.size);
rows.sort((a, b) => ext(b) - ext(a));
console.log(file.split(/[\\/]/).pop(), '- meshes:', rows.length, ' root scale/rot:', JSON.stringify(nodes[json.scenes[0].nodes[0]].scale), JSON.stringify(nodes[json.scenes[0].nodes[0]].rotation));
const big = rows.filter(r => ext(r) > 100);
console.log('meshes larger than 100 m:', big.length, ' of ', rows.length);
for (const r of rows.slice(0, 8)) console.log(`  ${ext(r).toFixed(0).padStart(6)} m  tris ${String(r.tris).padStart(6)}  size ${r.size.map(v => v.toFixed(0)).join('x')}  centre ${r.center.map(v => v.toFixed(0)).join(',')}  ${r.path.slice(-70)}  [${r.mat.slice(0, 40)}]`);
// bounds ignoring giant pieces
for (const cut of [100, 300]) {
  const keep = rows.filter(r => ext(r) <= cut);
  if (!keep.length) continue;
  const min = [1e9, 1e9, 1e9], max = [-1e9, -1e9, -1e9];
  for (const r of keep) for (let k = 0; k < 3; k++) { min[k] = Math.min(min[k], r.center[k] - r.size[k] / 2); max[k] = Math.max(max[k], r.center[k] + r.size[k] / 2); }
  console.log(`  bounds ignoring meshes > ${cut} m (${keep.length} meshes, ${keep.reduce((s, r) => s + r.tris, 0)} tris):`, max.map((v, k) => (v - min[k]).toFixed(1)).join(' x '), 'm');
}
