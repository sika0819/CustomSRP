/**
 * unity-doctor.js
 * VFX and UI Toolkit guidelines enforcer.
 * Generates a detailed report in "doctor-report.md".
 *
 * Usage: node execution/unity-doctor.js
 */

const fs = require('fs');
const path = require('path');

// ── Configuration ────────────────────────────────────────────
// Framework lives in The-Unity-Architect/; Unity project root is its parent.
const FRAMEWORK_ROOT = path.resolve(__dirname, '..');
const PROJECT_ROOT = path.resolve(FRAMEWORK_ROOT, '..');
const REPORT_PATH = path.join(FRAMEWORK_ROOT, 'doctor-report.md');

// First-party C# roots (this repo has no Assets/_Project scaffold).
const SCRIPT_ROOTS = [
    path.join(PROJECT_ROOT, 'Assets', 'GameMain'),
    path.join(PROJECT_ROOT, 'Assets', 'CustomSRP'),
    path.join(PROJECT_ROOT, 'Assets', 'WanosAudio'),
    path.join(PROJECT_ROOT, 'Assets', 'Scripts'),
    path.join(PROJECT_ROOT, 'Assets', 'Editor'),
].filter(p => fs.existsSync(p));

const SKIP_DIR_NAMES = new Set([
    'Library', 'Temp', 'Packages', 'node_modules', '__pycache__',
    '.git', 'StreamingAssets', 'Plugins',
]);

// ── Doctor Rules ─────────────────────────────────────────────
const DOCTOR_RULES = [
    // VFX / Projectiles
    {
        id: 'VFX-001',
        category: 'VFX / Projectiles',
        severity: 'CRITICAL',
        title: 'VFX Instantiate without Object Pool',
        detail: '`Instantiate()` in hit/trigger methods causes GC spikes during intense combat.',
        fix: 'Implement `UnityEngine.Pool.ObjectPool<T>` or `IObjectPool<T>`. Call `pool.Get()` when activating and `pool.Release()` when deactivating.',
        skill: 'unity-vfx-systems/SKILL.md §2 — Memory Management and Performance',
        scope: 'method',
        methodPattern: /(?:Hit|OnTrigger|OnCollision|OnImpact|Explode|Activate)/,
        regex: /Instantiate\s*\(/g,
    },
    {
        id: 'VFX-002',
        category: 'VFX / Projectiles',
        severity: 'CRITICAL',
        title: 'Direct Destroy in hit logic (missing pool.Release)',
        detail: '`Destroy(gameObject)` in a projectile/VFX guarantees GC pressure. In a game with frequent abilities, this produces frame drops.',
        fix: 'Replace `Destroy(gameObject)` with `pool.Release(this.gameObject)`. The object is deactivated and recycled.',
        skill: 'unity-vfx-systems/SKILL.md §2 — Mandatory Object Pooling',
        scope: 'method',
        methodPattern: /(?:Hit|OnTrigger|OnCollision|HitTarget|Activate)/,
        regex: /Destroy\s*\(\s*gameObject\s*\)/g,
    },
    {
        id: 'VFX-003',
        category: 'VFX / Projectiles',
        severity: 'CRITICAL',
        title: 'OverlapSphere with allocation — missing NonAlloc',
        detail: '`Physics.OverlapSphere` returns a new array on every call → GC allocation.',
        fix: 'Use `Physics.OverlapSphereNonAlloc(center, radius, _buffer)` with a pre-allocated `Collider[]` as a private field.',
        skill: 'unity-vfx-systems/SKILL.md §2 — Physics Optimization',
        scope: 'method',
        methodPattern: /(?:Hit|OnTrigger|OnCollision|HitTarget|Activate)/,
        regex: /Physics\.OverlapSphere\s*\(/g,
    },

    // UI Toolkit
    {
        id: 'UI-001',
        category: 'UI Toolkit',
        severity: 'CRITICAL',
        title: 'Layout property modification in Hot Path',
        detail: 'Modifying `width`, `height`, `margin`, or `padding` via code forces a full Layout Recalculation on the CPU.',
        fix: 'Use `style.translate`, `style.rotate`, or `style.scale` for animations. These are processed on the GPU.',
        skill: 'unity-ui-toolkit/SKILL.md §2 — GPU Animations',
        scope: 'method',
        methodPattern: /(?:Update|FixedUpdate|LateUpdate|OnTimerTick)/,
        regex: /\.style\.(?:width|height|margin|padding|top|left|right|bottom)\s*=/g,
    },
    {
        id: 'UI-002',
        category: 'UI Toolkit',
        severity: 'WARNING',
        title: 'Opacity used for hiding (CPU/GPU waste)',
        detail: '`style.opacity = 0` keeps the element in the render and layout pipeline.',
        fix: 'Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.',
        skill: 'unity-ui-toolkit/SKILL.md §2 — Smart Hiding',
        scope: 'global',
        regex: /\.style\.opacity\s*=\s*0/g,
    },
];

// ── Engine ───────────────────────────────────────────────────

function getFiles(dir, allFiles) {
    allFiles = allFiles || [];
    let entries;
    try {
        entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
        return allFiles;
    }
    for (const entry of entries) {
        if (SKIP_DIR_NAMES.has(entry.name)) continue;
        const name = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            getFiles(name, allFiles);
        } else if (entry.name.endsWith('.cs')) {
            allFiles.push(name);
        }
    }
    return allFiles;
}

function analyzeFile(filePath) {
    const content = fs.readFileSync(filePath, 'utf8');
    const lines = content.split('\n');
    const fileName = path.basename(filePath);
    const findings = [];

    DOCTOR_RULES.forEach(rule => {
        if (rule.scope === 'global') {
            let match;
            while ((match = rule.regex.exec(content)) !== null) {
                const lineNum = content.substring(0, match.index).split('\n').length;
                findings.push({ rule, line: lineNum, file: fileName });
            }
        } else if (rule.scope === 'method') {
            // Basic method extraction (requires matched braces)
            const methodRegex = new RegExp(`void\\s+\\w*${rule.methodPattern.source}\\w*\\s*\\(.*?\\)\\s*{`, 'g');
            let methodMatch;
            while ((methodMatch = methodRegex.exec(content)) !== null) {
                const startIdx = methodMatch.index;
                let braceCount = 1;
                let endIdx = startIdx + methodMatch[0].length;
                
                while (braceCount > 0 && endIdx < content.length) {
                    if (content[endIdx] === '{') braceCount++;
                    if (content[endIdx] === '}') braceCount--;
                    endIdx++;
                }

                const methodBody = content.substring(startIdx, endIdx);
                let bodyMatch;
                while ((bodyMatch = rule.regex.exec(methodBody)) !== null) {
                    const lineNum = content.substring(0, startIdx + bodyMatch.index).split('\n').length;
                    findings.push({ rule, line: lineNum, file: fileName });
                }
            }
        }
    });

    return findings;
}

function runDoctor() {
    console.log('🏛️  The Unity Architect — Running Diagnostics...');
    console.log(`📂 Project root: ${PROJECT_ROOT}`);

    if (SCRIPT_ROOTS.length === 0) {
        console.error('Error: No first-party script roots found under Assets/.');
        process.exit(1);
    }

    const files = SCRIPT_ROOTS.flatMap(root => getFiles(root));
    console.log(`📂 Scanning ${SCRIPT_ROOTS.length} roots → ${files.length} .cs files`);
    let allFindings = [];

    files.forEach(f => {
        const fileFindings = analyzeFile(f).map(finding => ({
            ...finding,
            rel: path.relative(PROJECT_ROOT, f),
        }));
        allFindings = allFindings.concat(fileFindings);
    });

    // Generate Report
    let report = `# Unity Architect — Diagnostics Report\n\n`;
    report += `Generated on: ${new Date().toLocaleString()}\n`;
    report += `Project root: \`${PROJECT_ROOT}\`\n`;
    report += `Roots: ${SCRIPT_ROOTS.map(r => `\`${path.relative(PROJECT_ROOT, r)}\``).join(', ')}\n`;
    report += `Total files scanned: ${files.length}\n`;
    report += `Total issues found: ${allFindings.length}\n\n`;

    if (allFindings.length === 0) {
        report += `## ✅ No critical issues found. Your architecture is clean.\n`;
    } else {
        const sorted = allFindings.sort((a, b) => a.rule.severity === 'CRITICAL' ? -1 : 1);

        report += `## ⚠️ Findings\n\n`;
        sorted.forEach(f => {
            report += `### [${f.rule.severity}] ${f.rule.title}\n`;
            report += `- **File:** \`${f.rel || f.file}\` (Line ${f.line})\n`;
            report += `- **Issue:** ${f.rule.detail}\n`;
            report += `- **Fix:** ${f.rule.fix}\n`;
            report += `- **Skill Reference:** ${f.rule.skill}\n\n`;
        });
    }

    fs.writeFileSync(REPORT_PATH, report);
    console.log(`✅ Diagnostics complete. Report saved to: ${REPORT_PATH}`);
    console.log(`   Issues: ${allFindings.length}`);
}

runDoctor();
