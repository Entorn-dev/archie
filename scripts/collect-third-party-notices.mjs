import { execFileSync } from "node:child_process"
import { cpSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs"
import { homedir } from "node:os"
import { basename, dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const releaseRoot = resolve(process.argv[2] ?? "")
if (!process.argv[2] || !releaseRoot.startsWith(repoRoot)) {
  throw new Error("Usage: collect-third-party-notices.mjs <release-stage-under-repository>")
}

const licensesRoot = join(releaseRoot, "licenses")
const npmRoot = join(licensesRoot, "npm")
const nugetRoot = join(licensesRoot, "nuget")
rmSync(npmRoot, { recursive: true, force: true })
rmSync(nugetRoot, { recursive: true, force: true })
mkdirSync(npmRoot, { recursive: true })
mkdirSync(nugetRoot, { recursive: true })

collectNpmNotices()
collectNuGetNotices()

function collectNpmNotices() {
  const raw = execFileSync("pnpm", ["--dir", join(repoRoot, "web"), "licenses", "list", "--prod", "--json"], { encoding: "utf8" })
  const byLicense = JSON.parse(raw)
  const packages = Object.entries(byLicense)
    .flatMap(([license, entries]) => entries.flatMap((entry) => entry.paths.map((path) => ({ ...entry, license, path }))))
    .map((entry) => ({ ...entry, version: JSON.parse(readFileSync(join(entry.path, "package.json"), "utf8")).version }))
    .sort(comparePackages)

  const inventory = ["Bundled npm package licenses", "============================", ""]
  for (const entry of packages) {
    const files = noticeFiles(entry.path)
    if (files.length === 0) throw new Error(`${entry.name}@${entry.version} supplies no license or notice file`)

    const copied = files.map((source) => {
      const target = `${safeName(entry.name)}-${entry.version}-${basename(source)}`
      cpSync(source, join(npmRoot, target))
      return target
    })
    inventory.push(`${entry.name} ${entry.version}`, `License: ${entry.license}`, `Source: ${entry.homepage ?? "not declared"}`, `Files: ${copied.join(", ")}`, "")
  }
  writeFileSync(join(npmRoot, "README.txt"), `${inventory.join("\n")}\n`)
}

function collectNuGetNotices() {
  const dependencies = new Map()
  for (const depsFile of findFiles(releaseRoot, (name) => name.endsWith(".deps.json"))) {
    const manifest = JSON.parse(readFileSync(depsFile, "utf8"))
    for (const [key, metadata] of Object.entries(manifest.libraries ?? {})) {
      if (metadata.type !== "package") continue
      const separator = key.lastIndexOf("/")
      dependencies.set(key.toLowerCase(), { name: key.slice(0, separator), version: key.slice(separator + 1) })
    }
  }

  const packageRoot = process.env.NUGET_PACKAGES ?? join(homedir(), ".nuget", "packages")
  const inventory = ["Bundled NuGet package licenses", "==============================", ""]
  for (const entry of [...dependencies.values()].sort(comparePackages)) {
    const sourceRoot = join(packageRoot, entry.name.toLowerCase(), entry.version.toLowerCase())
    const nuspec = readdirSync(sourceRoot).find((name) => name.endsWith(".nuspec"))
    if (!nuspec) throw new Error(`${entry.name} ${entry.version} supplies no NuGet manifest`)

    const metadata = readFileSync(join(sourceRoot, nuspec), "utf8")
    const expression = metadata.match(/<license type="expression">([^<]+)<\/license>/)?.[1]
    const projectUrl = metadata.match(/<projectUrl>([^<]+)<\/projectUrl>/)?.[1]
    const files = noticeFiles(sourceRoot, 2)
    if (!expression && files.length === 0) throw new Error(`${entry.name} ${entry.version} supplies neither a license expression nor a license file`)

    const copied = files.map((source) => {
      const target = `${safeName(entry.name)}-${entry.version}-${basename(source)}`
      cpSync(source, join(nugetRoot, target))
      return target
    })
    inventory.push(`${entry.name} ${entry.version}`, `License: ${expression ?? "see supplied file"}`, `Source: ${projectUrl ?? "not declared"}`, `Files: ${copied.join(", ") || "none supplied; see SPDX expression"}`, "")
  }
  writeFileSync(join(nugetRoot, "README.txt"), `${inventory.join("\n")}\n`)
}

function noticeFiles(root, maxDepth = 1) {
  return findFiles(root, (name) => /^(license|copying|notice|third[-_. ]party)/i.test(name), maxDepth)
}

function findFiles(root, predicate, maxDepth = Number.POSITIVE_INFINITY, depth = 0) {
  const files = []
  for (const name of readdirSync(root).sort()) {
    const path = join(root, name)
    const stat = statSync(path)
    if (stat.isFile() && predicate(name)) files.push(path)
    else if (stat.isDirectory() && depth < maxDepth) files.push(...findFiles(path, predicate, maxDepth, depth + 1))
  }
  return files
}

function safeName(name) {
  return name.replace(/^@/, "").replaceAll("/", "-")
}

function comparePackages(left, right) {
  return left.name.localeCompare(right.name) || left.version.localeCompare(right.version)
}
