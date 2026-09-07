import ELK from "elkjs/lib/elk.bundled.js"
import { performance } from "node:perf_hooks"

const nodeCount = 500
const nodes = Array.from({ length: nodeCount }, (_, index) => ({ id: `node:${index}`, width: 210, height: 96 }))
const edges = []
for (let index = 0; index < nodeCount && edges.length < 1_500; index += 1) {
  for (const offset of [1, 2, 7, 31]) {
    if (edges.length < 1_500 && index + offset < nodeCount) edges.push({ id: `edge:${index}:${offset}`, sources: [`node:${index}`], targets: [`node:${index + offset}`] })
  }
}

const times = []
for (let run = 0; run < 20; run += 1) {
  const started = performance.now()
  await new ELK().layout({
    id: "root",
    layoutOptions: { "elk.algorithm": "mrtree", "elk.direction": "RIGHT" },
    children: nodes,
    edges,
  })
  times.push(performance.now() - started)
}
times.sort((left, right) => left - right)
const p95 = times[Math.ceil(times.length * 0.95) - 1]
console.log(JSON.stringify({ nodes: nodes.length, edges: edges.length, runsMs: times.map((time) => Number(time.toFixed(1))), p95Ms: Number(p95.toFixed(1)) }))
if (p95 >= 3_000) process.exitCode = 1
