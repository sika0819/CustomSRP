/**
 * unity-project-graph.js
 * Analyzes the Unity project without opening the Editor.
 * Builds a dependency graph between scenes, prefabs, scripts, and assets.
 *
 * Output: project-graph.json  +  project-graph-report.md
 * Usage:  node execution/unity-project-graph.js
 * Flags:  --missing   Only show broken GUIDs
 *         --json      Only export JSON (no markdown)
 *         --scene     Also analyze .unity scenes
 */

'use strict';
const fs = require('fs');
const path = require('path');

// ── Config ───────────────────────────────────────────────────────────────────
// Framework lives in The-Unity-Architect/; Unity project root is its parent.
const FRAMEWORK_ROOT = path.resolve(__dirname, '..');
const ROOT = path.resolve(FRAMEWORK_ROOT, '..');
const ASSETS = path.join(ROOT, 'Assets');
const OUT_JSON = path.join(FRAMEWORK_ROOT, 'project-graph.json');
const OUT_MD = path.join(FRAMEWORK_ROOT, 'project-graph-report.md');
const ARGS = new Set(process.argv.slice(2));
const ONLY_MISSING = ARGS.has('--missing');
const NO_MD = ARGS.has('--json');
const SCAN_SCENES = ARGS.has('--scene');
// --project-only: first-party folders (this repo has no Assets/_Project)
const PROJECT_ONLY = ARGS.has('--project-only');
const FIRST_PARTY = [
    path.join(ASSETS, 'GameMain'),
    path.join(ASSETS, 'CustomSRP'),
    path.join(ASSETS, 'WanosAudio'),
    path.join(ASSETS, 'Scripts'),
    path.join(ASSETS, 'Editor'),
    path.join(ASSETS, 'Scenes'),
].filter(p => fs.existsSync(p));
const SCAN_ROOTS = PROJECT_ONLY ? FIRST_PARTY : [ASSETS];
const SKIP_DIR_NAMES = new Set([
    'Library', 'Temp', 'Packages', 'node_modules', '__pycache__',
    '.git', 'StreamingAssets',
]);


function findUnityBuiltInPackageRoots() {
    const roots = [];
    const hubEditors = '/Applications/Unity/Hub/Editor';
    let version = null;
    try {
        const pv = fs.readFileSync(path.join(ROOT, 'ProjectSettings', 'ProjectVersion.txt'), 'utf8');
        const m = pv.match(/m_EditorVersion:\s*(\S+)/);
        if (m) version = m[1];
    } catch { /* ignore */ }

    const candidates = [];
    if (version) {
        candidates.push(path.join(hubEditors, version, 'Unity.app/Contents/Resources/PackageManager/BuiltInPackages'));
    }
    // Fallback: newest Hub editor that has BuiltInPackages
    try {
        if (fs.existsSync(hubEditors)) {
            for (const name of fs.readdirSync(hubEditors)) {
                candidates.push(path.join(hubEditors, name, 'Unity.app/Contents/Resources/PackageManager/BuiltInPackages'));
            }
        }
    } catch { /* ignore */ }

    for (const c of candidates) {
        if (fs.existsSync(c) && !roots.includes(c)) roots.push(c);
    }
    return roots;
}

// ── Step 1: Build GUID → asset map from Assets + Packages + Unity BuiltInPackages ──
function buildGuidMap() {
    const map = {};     // guid → { type, path, name }
    // Built-in packages (ugui, etc.) live in the Editor install, not project Packages/
    const scanRoots = [ASSETS, path.join(ROOT, 'Packages'), ...findUnityBuiltInPackageRoots()];
    const metaFiles = scanRoots.flatMap(r => walkFiles(r, f => f.endsWith('.meta')));
    for (const metaPath of metaFiles) {
        if (metaPath.includes(`${path.sep}__pycache__`) || path.basename(metaPath).startsWith('__pycache__')) {
            continue;
        }
        let content;
        try {
            content = fs.readFileSync(metaPath, 'utf8');
        } catch {
            continue;
        }
        const guidMatch = content.match(/^guid:\s*([a-f0-9]{32})/m);
        if (!guidMatch) continue;
        const guid = guidMatch[1];
        const assetPath = metaPath.slice(0, -5); // strip .meta
        if (!fs.existsSync(assetPath)) continue;
        const ext = path.extname(assetPath).toLowerCase();
        const name = path.basename(assetPath);
        let type = ext.slice(1) || 'unknown';
        if (ext === '.cs') type = 'script';
        else if (ext === '.prefab') type = 'prefab';
        else if (ext === '.unity') type = 'scene';
        else if (ext === '.asset') type = 'asset';
        else if (ext === '.mat') type = 'material';
        else if (ext === '.anim') type = 'animation';
        map[guid] = { guid, type, path: path.relative(ROOT, assetPath), name };
    }
    return map;
}

// ── Step 2: Parse a YAML Unity file for MonoBehaviour → script references ────
function parseUnityFile(filePath, guidMap) {
    const content = fs.readFileSync(filePath, 'utf8');
    const fileName = path.basename(filePath);
    const relPath = path.relative(ROOT, filePath);

    const scriptRefs = [];

    // Split into YAML documents. Only MonoBehaviours (type 114) contain C# scripts.
    // Format: "--- !u!114 &12345"
    const blocks = content.split(/(?=^--- )/m);
    for (const block of blocks) {
        if (!block.startsWith('--- !u!114 ')) continue;  // only MonoBehaviours

        const scriptMatch = block.match(/m_Script:\s*\{fileID:\s*\d+,\s*guid:\s*([a-f0-9]{32})/);
        if (!scriptMatch) continue;

        const guid = scriptMatch[1];
        // Skip Unity built-in scripts
        if (/^0{32}$/.test(guid) || guid.startsWith('0000000000000000')) continue;

        const resolved = guidMap[guid];
        const existing = scriptRefs.find(r => r.guid === guid);
        if (!existing) {
            scriptRefs.push({ guid, resolved: resolved || null, missing: !resolved });
        }
    }

    // Extract unique GameObject names (m_Name fields)
    const gameObjects = [];
    const goRegex = /m_Name:\s*(.+)/g;
    let m;
    while ((m = goRegex.exec(content)) !== null) {
        const name = m[1].trim();
        if (name && name !== '{}' && name.length < 80) gameObjects.push(name);
    }

    return {
        file: relPath,
        name: fileName,
        type: path.extname(filePath).slice(1),
        gameObjects: [...new Set(gameObjects)].slice(0, 20),
        scriptRefs,
        missingScripts: scriptRefs.filter(r => r.missing),
    };
}

// ── Step 3: Build full project graph ─────────────────────────────────────────
function walkMany(roots, filter) {
    return roots.flatMap(root => walkFiles(root, filter));
}

function buildGraph(guidMap) {
    const extensions = ['.prefab', '.asset'];
    if (SCAN_SCENES) extensions.push('.unity');

    const files = walkMany(SCAN_ROOTS, f => extensions.some(e => f.endsWith(e)));

    const nodes = [];

    for (const f of files) {
        try {
            const node = parseUnityFile(f, guidMap);
            if (ONLY_MISSING && node.missingScripts.length === 0) continue;
            nodes.push(node);
        } catch (e) {
            // skip binary or unparseable files
        }
    }

    // Also index scripts themselves
    const scripts = walkMany(SCAN_ROOTS, f => f.endsWith('.cs') && !f.endsWith('.meta'));

    const scriptIndex = scripts.map(f => ({
        file: path.relative(ROOT, f),
        name: path.basename(f),
        type: 'script',
        guid: (() => {
            const meta = f + '.meta';
            if (!fs.existsSync(meta)) return null;
            const c = fs.readFileSync(meta, 'utf8');
            const gm = c.match(/^guid:\s*([a-f0-9]{32})/m);
            return gm ? gm[1] : null;
        })(),
    }));

    return { nodes, scriptIndex, guidMap };
}

// ── Step 4: Generate Markdown report ─────────────────────────────────────────
function generateReport(graph) {
    const { nodes, scriptIndex } = graph;
    const now = new Date().toLocaleString();
    const lines = [];

    lines.push('# 🗂️ Unity Project Graph Report');
    lines.push(`\n> Generated on **${now}** by \`unity-project-graph.js\`\n`);

    // Summary
    const allMissing = nodes.flatMap(n => n.missingScripts.map(r => ({ ...r, file: n.file })));
    lines.push('## 📊 Summary\n');
    lines.push(`| Type | Count |`);
    lines.push(`|:-----|:---------|`);
    lines.push(`| Prefabs analyzed | ${nodes.filter(n => n.type === 'prefab').length} |`);
    lines.push(`| Assets analyzed | ${nodes.filter(n => n.type === 'asset').length} |`);
    lines.push(`| Scenes analyzed | ${nodes.filter(n => n.type === 'scene').length} |`);
    lines.push(`| Project scripts | ${scriptIndex.length} |`);
    lines.push(`| 🔴 Broken scripts (missing) | **${allMissing.length}** |`);
    lines.push('');

    // Missing scripts (top priority)
    if (allMissing.length > 0) {
        lines.push('## 🔴 Broken Scripts (Missing References)\n');
        lines.push('> These files reference scripts that no longer exist. Use `GameObjectUtility.RemoveMonoBehavioursWithMissingScript` to clean them.\n');
        lines.push('| File | Broken GUID |');
        lines.push('|:--------|:----------|');
        for (const m of allMissing) {
            lines.push(`| \`${path.basename(m.file)}\` | \`${m.guid}\` |`);
        }
        lines.push('');
    } else {
        lines.push('## ✅ No broken scripts found\n');
    }

    if (!ONLY_MISSING) {
        // Per-file dependency tree
        lines.push('---\n');
        lines.push('## 📦 Dependency Tree by File\n');

        for (const node of nodes) {
            lines.push(`### \`${node.name}\``);
            lines.push(`**Path:** \`${node.file}\`  `);
            lines.push(`**Type:** ${node.type}  `);

            if (node.gameObjects.length > 0) {
                lines.push(`**GameObjects:** ${node.gameObjects.slice(0, 10).map(g => `\`${g}\``).join(', ')}${node.gameObjects.length > 10 ? ' ...' : ''}`);
            }

            if (node.scriptRefs.length > 0) {
                lines.push('\n**Referenced Scripts:**');
                for (const ref of node.scriptRefs) {
                    if (ref.missing) {
                        lines.push(`  - 🔴 MISSING \`${ref.guid}\``);
                    } else {
                        lines.push(`  - ✅ \`${ref.resolved.name}\` (\`${ref.resolved.path}\`)`);
                    }
                }
            }

            lines.push('');
        }

        // Script index
        lines.push('---\n');
        lines.push('## 📝 Script Index\n');
        lines.push('| Script | GUID | Path |');
        lines.push('|:-------|:-----|:-----|');
        for (const s of scriptIndex.sort((a, b) => a.name.localeCompare(b.name))) {
            lines.push(`| \`${s.name}\` | \`${s.guid || 'N/A'}\` | \`${s.file}\` |`);
        }
    }

    lines.push('\n---\n_Generated by `execution/unity-project-graph.js`. To regenerate: `node execution/unity-project-graph.js`_');
    return lines.join('\n');
}

// ── Utilities ─────────────────────────────────────────────────────────────────
function walkFiles(dir, filter) {
    const results = [];
    function walk(d) {
        if (!fs.existsSync(d)) return;
        let entries;
        try {
            entries = fs.readdirSync(d, { withFileTypes: true });
        } catch {
            return;
        }
        for (const entry of entries) {
            const full = path.join(d, entry.name);
            if (entry.isDirectory()) {
                if (SKIP_DIR_NAMES.has(entry.name)) continue;
                walk(full);
            } else if (filter(full)) {
                results.push(full);
            }
        }
    }
    walk(dir);
    return results;
}

// ── Main ──────────────────────────────────────────────────────────────────────
console.log('🗂️  Unity Project Graph\n' + '─'.repeat(50));
console.log(`📂 Project root: ${ROOT}`);
console.log(`📂 Scope: ${PROJECT_ONLY ? 'first-party only (--project-only)' : 'Full Assets/'}`);
console.log('📂 Reading project GUIDs (Assets + Packages)...');

const guidMap = buildGuidMap();
console.log(`   ${Object.keys(guidMap).length} assets indexed`);

console.log('🔍 Analyzing prefabs and assets...');
const graph = buildGraph(guidMap);
console.log(`   ${graph.nodes.length} files analyzed`);

// Write JSON
const jsonOut = {
    generated: new Date().toISOString(),
    summary: {
        total_nodes: graph.nodes.length,
        missing_scripts: graph.nodes.flatMap(n => n.missingScripts).length,
        scripts: graph.scriptIndex.length,
    },
    nodes: graph.nodes,
    scripts: graph.scriptIndex,
};
fs.writeFileSync(OUT_JSON, JSON.stringify(jsonOut, null, 2), 'utf8');
console.log(`✅ JSON exported: ${OUT_JSON}`);

if (!NO_MD) {
    const md = generateReport(graph);
    fs.writeFileSync(OUT_MD, md, 'utf8');
    console.log(`✅ Report exported: ${OUT_MD}`);
}

// Console summary of missing
const missing = graph.nodes.filter(n => n.missingScripts.length > 0);
if (missing.length > 0) {
    console.log(`\n🔴 Broken scripts found in ${missing.length} file(s):`);
    for (const node of missing) {
        console.log(`   • ${node.name}`);
        for (const r of node.missingScripts) {
            console.log(`     Broken GUID: ${r.guid}`);
        }
    }
} else {
    console.log('\n✅ No broken scripts found');
}
console.log('');
