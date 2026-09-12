// validate-preset.cjs — parse each installed preset the way DSH's agent-presets
// discovery does, so a malformed composition is caught before a session fails.
//
// Mirrors dsh-agent-presets: the composition must be a top-level list of rows,
// each carrying a plugin `name`, and preset.yml must yield name/description.
const fs = require("node:fs");
const path = require("node:path");

const ROOT = process.argv[2] || path.join(process.env.USERPROFILE, ".dsh", ".agent-presets");

// The loader's YAML parser, taken from the installed harness so we validate
// with the same library DSH itself uses.
let yaml;
try {
  yaml = require(path.join(process.env.USERPROFILE, ".dsh", "profiles", "node_modules", "js-yaml"));
} catch (e) {
  console.error("could not load js-yaml:", e.message);
  process.exit(2);
}

// DSH's loader registers a `!!js` tag for JavaScript expressions (the base
// bundle's own compositions use it, e.g. `!!js process.platform === 'win32'`).
// Without declaring it, js-yaml rejects every composition the harness accepts.
const JsType = new yaml.Type("tag:yaml.org,2002:js", {
  kind: "scalar",
  construct: (data) => ({ __js: data }),
});

function entryListProblem(rows, at = "") {
  if (!Array.isArray(rows)) return at === "" ? "not a top-level list" : `group ${at} not a list`;
  for (const [i, row] of rows.entries()) {
    const label = at === "" ? `row ${i + 1}` : `${at} row ${i + 1}`;
    if (typeof row !== "object" || row === null || Array.isArray(row)) return `${label} is not a map`;
    if (typeof row.name !== "string" || row.name === "") return `${label} names no plugin`;
    if (row.group === true) {
      const nested = entryListProblem(row.config, label);
      if (nested) return nested;
    }
  }
  return null;
}

let failures = 0;
const SCHEMA = yaml.DEFAULT_SCHEMA.extend([JsType]);
const dirs = fs.readdirSync(ROOT, { withFileTypes: true })
  .filter((d) => d.isDirectory() && d.name.startsWith("msys2-"))
  .map((d) => d.name)
  .sort();

if (dirs.length === 0) {
  console.error("no msys2-* preset directories found under " + ROOT);
  process.exit(1);
}

for (const name of dirs) {
  const dir = path.join(ROOT, name);
  const composition = path.join(dir, "agent.cordis.yml");
  const metadata = path.join(dir, "preset.yml");
  const shim = path.join(dir, "msys2_shell_shim.exe");
  const problems = [];

  // Composition
  let rows;
  try {
    const raw = fs.readFileSync(composition, "utf8");
    if (raw.charCodeAt(0) === 0xfeff) problems.push("composition starts with a BOM");
    rows = yaml.load(raw, { schema: SCHEMA });
  } catch (e) {
    problems.push("composition did not parse: " + e.message);
  }
  if (rows !== undefined) {
    const shape = entryListProblem(rows);
    if (shape) problems.push("composition shape: " + shape);
  }

  // Metadata
  let meta;
  try {
    meta = yaml.load(fs.readFileSync(metadata, "utf8"), { schema: SCHEMA });
    if (typeof meta?.name !== "string" || meta.name.trim() === "") problems.push("preset.yml has no name");
    if (typeof meta?.description !== "string" || meta.description.trim() === "") problems.push("preset.yml has no description");
  } catch (e) {
    problems.push("preset.yml did not parse: " + e.message);
  }

  // Shim present and non-empty
  try {
    if (fs.statSync(shim).size === 0) problems.push("shim is empty");
  } catch {
    problems.push("shim missing");
  }

  // The shell row must point at the shim and keep the pwsh tool enabled.
  const flat = rows || [];
  const shell = flat.find((r) => r.id === "pwsh-sandbox");
  const tool = flat.find((r) => r.id === "tool-pwsh");
  if (!shell) problems.push("no pwsh-sandbox row");
  else {
    if (shell.disabled === true) problems.push("pwsh-sandbox is disabled");
    const configured = shell.config?.pwshPath;
    if (!configured) problems.push("pwsh-sandbox has no pwshPath");
    else if (!/msys2_shell_shim\.exe$/.test(configured)) problems.push("pwshPath does not point at the shim: " + configured);
    else if (!fs.existsSync(configured)) problems.push("pwshPath does not exist on disk: " + configured);
  }
  if (!tool) problems.push("no tool-pwsh row");
  else if (tool.disabled === true) problems.push("tool-pwsh is disabled");

  const policy = flat.find((r) => r.id === "sandbox-policy");
  if (policy?.config?.mode !== "danger-full-access") problems.push("sandbox-policy is not danger-full-access");

  if (problems.length) {
    failures++;
    console.log("FAIL " + name);
    for (const p of problems) console.log("       " + p);
  } else {
    console.log("ok   " + name + "  (" + flat.length + " rows, " + (meta?.name || "?") + ")");
  }
}

console.log();
if (failures) {
  console.log(failures + " preset(s) INVALID");
  process.exit(1);
}
console.log("all " + dirs.length + " presets valid and discoverable");
