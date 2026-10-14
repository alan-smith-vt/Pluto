// Headless test for triBvh.js: the BVH pick must agree with a brute-force test of every triangle
// (random soup, random rays), honour skip(), and stay fast on a few million triangles.
//   node viewer/tests/test_tribvh.js            (add "big" for the 5.5 M triangle timing)
const path = require('path');
const B = require(path.join(__dirname, '..', 'scripts', 'triBvh.js'));

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };

let seed = 12345;
const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; };

function soup(n, span, size) {
  const P = new Float32Array(n * 9);
  for (let i = 0; i < n; i++) {
    const cx = rnd() * span, cy = rnd() * span, cz = rnd() * span;
    for (let v = 0; v < 3; v++) {
      P[i * 9 + v * 3] = cx + (rnd() - 0.5) * size;
      P[i * 9 + v * 3 + 1] = cy + (rnd() - 0.5) * size;
      P[i * 9 + v * 3 + 2] = cz + (rnd() - 0.5) * size;
    }
  }
  return P;
}

function brute(P, O, D, skip) {
  let best = Infinity, bt = -1;
  const n = P.length / 9;
  for (let tr = 0; tr < n; tr++) {
    if (skip && skip(tr)) continue;
    const o = tr * 9;
    const e1 = [P[o + 3] - P[o], P[o + 4] - P[o + 1], P[o + 5] - P[o + 2]];
    const e2 = [P[o + 6] - P[o], P[o + 7] - P[o + 1], P[o + 8] - P[o + 2]];
    const p = [D[1] * e2[2] - D[2] * e2[1], D[2] * e2[0] - D[0] * e2[2], D[0] * e2[1] - D[1] * e2[0]];
    const det = e1[0] * p[0] + e1[1] * p[1] + e1[2] * p[2];
    if (det === 0) continue;
    const s = [O[0] - P[o], O[1] - P[o + 1], O[2] - P[o + 2]];
    const u = (s[0] * p[0] + s[1] * p[1] + s[2] * p[2]) / det;
    if (u < 0 || u > 1) continue;
    const q = [s[1] * e1[2] - s[2] * e1[1], s[2] * e1[0] - s[0] * e1[2], s[0] * e1[1] - s[1] * e1[0]];
    const w = (D[0] * q[0] + D[1] * q[1] + D[2] * q[2]) / det;
    if (w < 0 || u + w > 1) continue;
    const t = (e2[0] * q[0] + e2[1] * q[1] + e2[2] * q[2]) / det;
    if (t > 0 && t < best) { best = t; bt = tr; }
  }
  return bt < 0 ? null : { tri: bt, t: best };
}

function rays(k, span) {
  const out = [];
  for (let i = 0; i < k; i++) {
    const O = [-span * 0.5 + rnd() * span * 2, -span * 0.5 + rnd() * span * 2, -span];
    const T = [rnd() * span, rnd() * span, rnd() * span];
    out.push([O, [T[0] - O[0], T[1] - O[1], T[2] - O[2]]]);
  }
  out.push([[span / 2, span / 2, -span], [0, 0, 1]]);   // axis-parallel
  return out;
}

// 1. agreement with brute force
{
  const P = soup(20000, 100, 3), bvh = B.build(P);
  let agree = 0, hits = 0;
  const rs = rays(400, 100);
  for (const [O, D] of rs) {
    const a = B.raycast(bvh, O, D, null), b = brute(P, O, D, null);
    if (b) hits++;
    if ((!a && !b) || (a && b && Math.abs(a.t - b.t) < 1e-9)) agree++;   // same distance (the soup has exact duplicates)
  }
  assert(agree === rs.length, `BVH = brute force on ${rs.length} rays (${hits} hits), agreed ${agree}`);

  // 2. skip: hide every even triangle
  const skip = t => t % 2 === 0;
  let agree2 = 0;
  for (const [O, D] of rs) {
    const a = B.raycast(bvh, O, D, skip), b = brute(P, O, D, skip);
    if ((!a && !b) || (a && b && Math.abs(a.t - b.t) < 1e-9)) agree2++;
    if (a && a.tri % 2 === 0) { agree2 = -1e9; }
  }
  assert(agree2 === rs.length, 'skip(): hidden triangles never returned, matches brute force');
}

// 3. empty and single-triangle inputs
assert(B.raycast(B.build(new Float32Array(0)), [0, 0, -1], [0, 0, 1], null) === null, 'empty soup: no hit');
{
  const P = Float32Array.from([0, 0, 0, 1, 0, 0, 0, 1, 0]);
  const h = B.raycast(B.build(P), [0.2, 0.2, -5], [0, 0, 1], null);
  assert(h && h.tri === 0 && Math.abs(h.t - 5) < 1e-6, 'single triangle: hit at t = 5');
}

// 4. timing
{
  const n = process.argv[2] === 'big' ? 5500000 : 1000000;
  const P = soup(n, 1000, 2);
  let t0 = Date.now();
  const bvh = B.build(P);
  const tb = Date.now() - t0;
  const rs = rays(1000, 1000);
  t0 = Date.now();
  for (const [O, D] of rs) B.raycast(bvh, O, D, null);
  const tr = (Date.now() - t0) / rs.length;
  console.log(`info ${n} triangles: build ${tb} ms, ${tr.toFixed(3)} ms per ray`);
  assert(tr < 5, 'pick under 5 ms per ray');
}

console.log(fails ? fails + ' failure(s)' : 'all passed');
process.exit(fails ? 1 : 0);
