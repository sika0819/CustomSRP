const fs = require('fs');
const path = require('path');

// ============================================================
//  RULES — Based on Skills and Best Practices
//  Each rule has: id, name, severity, message
//  and one of the following matching strategies:
//    type:"length"  — compares total lines
//    scope:"global" — regex over the entire file
//    scope:"hotpath"— regex only within Update/FixedUpdate/LateUpdate
//    scope:"method" — regex only within methods matching methodPattern
// ============================================================
const rules = [
    // ── ARCHITECTURE ─────────────────────────────────────────
    {
        id: "ARCH_GOD_CLASS",
        name: "God Class",
        type: "length",
        threshold: 400,
        severity: "WARNING",
        message: "The script exceeds 400 lines. Consider splitting responsibilities."
    },

    // ── GENERAL PERFORMANCE ──────────────────────────────────
    {
        id: "PERF_CRITICAL_SENDMESSAGE",
        name: "SendMessage Usage",
        scope: "global",
        regex: /\.SendMessage\s*\(/g,
        severity: "CRITICAL",
        message: "SendMessage is slow and unsafe. Use GetComponent, Interfaces, or Events."
    },
    {
        id: "PERF_HOTPATH_FIND",
        name: "Search in Hot Path",
        scope: "hotpath",
        regex: /(GetComponent|FindObjectOfType|FindWithTag|GameObject\.Find)\s*[(<]/g,
        severity: "CRITICAL",
        message: "Expensive search in Hot Path. Move to Awake/Start and cache in a private field."
    },
    {
        id: "PERF_HOTPATH_GC_STRINGS",
        name: "String Concatenation in Hot Path",
        scope: "hotpath",
        regex: /"\s*\+\s*\w|string\s*\+=/g,
        severity: "WARNING",
        message: "Possible GC allocation due to string concatenation in Hot Path. Use StringBuilder or $\"{}\""
    },
    {
        id: "PLAT_DEPRECATED_FIND",
        name: "Deprecated API (Unity 6)",
        scope: "global",
        regex: /FindObjectsOfType\b|FindObjectOfType\b/g,
        severity: "WARNING",
        message: "Deprecated method in Unity 6. Replace with FindFirstObjectByType / FindObjectsByType."
    },
    {
        id: "PERF_OVERLAPSPHERE_ALLOC",
        name: "OverlapSphere with Allocation",
        scope: "global",
        regex: /Physics\.OverlapSphere\s*\((?!.*NonAlloc)/g,
        severity: "CRITICAL",
        message: "Use Physics.OverlapSphereNonAlloc with a pre-allocated buffer to avoid GC during combat."
    },

    // ── VFX / PROJECTILES ────────────────────────────────────
    {
        id: "VFX_INSTANTIATE_IN_HIT",
        name: "VFX Instantiate without Object Pool",
        scope: "method",
        methodPattern: /(?:Hit|OnTrigger|OnCollision|OnImpact|Explode|Activate)/,
        regex: /Instantiate\s*\(/g,
        severity: "CRITICAL",
        message: "VFX instantiated directly in hit/trigger. Use UnityEngine.Pool.IObjectPool to avoid GC spikes during intense combat."
    },
    {
        id: "VFX_DESTROY_WITHOUT_POOL",
        name: "Direct Destroy (should be pool.Release)",
        scope: "method",
        methodPattern: /(?:Hit|OnTrigger|OnCollision|HitTarget|Activate)/,
        regex: /Destroy\s*\(\s*gameObject\s*\)/g,
        severity: "WARNING",
        message: "Destroy(gameObject) in hit logic. If this object is frequent, use Object Pool with pool.Release(gameObject)."
    },
    {
        id: "VFX_MOVEMENT_IN_UPDATE",
        name: "Projectile logic in Update",
        scope: "hotpath_update_only",
        regex: /MovePosition|transform\.position\s*\+=/g,
        severity: "WARNING",
        message: "Projectile movement in Update. All physics/positional logic should be in FixedUpdate."
    },

    // ── UI TOOLKIT ───────────────────────────────────────────
    {
        id: "UI_LAYOUT_ANIMATION",
        name: "Layout Property Animation (Expensive)",
        scope: "hotpath",
        regex: /style\.(left|top|right|bottom|width|height|margin|padding)\s*=/g,
        severity: "CRITICAL",
        message: "Modifying layout properties in Hot Path forces a full Layout Recalculation. Use style.translate or transform for animations."
    },
    {
        id: "UI_DISPLAY_TOGGLE_IN_UPDATE",
        name: "Display Toggle in Hot Path",
        scope: "hotpath",
        regex: /\.style\.display\s*=/g,
        severity: "WARNING",
        message: "Changing display:none/flex in Update is expensive (rebuild). Prefer style.visibility or move out of the hot path."
    }
];

// ============================================================
//  HELPERS
// ============================================================

/** Extracts blocks of a method whose name matches pattern */
function extractMethodBlocks(content, methodNamePattern) {
    const results = [];
    // Finds "void|int|bool|... MethodName(...) { ... }"
    const sig = /(?:private|public|protected|internal|override|virtual)?\s*\w[\w<>\[\]]*\s+(\w+)\s*\([^)]*\)\s*\{/g;
    let m;
    while ((m = sig.exec(content)) !== null) {
        const methodName = m[1];
        if (!methodNamePattern.test(methodName)) continue;
        // Extract body by counting braces
        let depth = 1;
        let i = m.index + m[0].length;
        const bodyStart = i;
        while (i < content.length && depth > 0) {
            if (content[i] === '{') depth++;
            else if (content[i] === '}') depth--;
            i++;
        }
        results.push({ name: methodName, body: content.substring(bodyStart, i - 1), bodyStart });
    }
    return results;
}

/** Extracts bodies of Update, FixedUpdate, LateUpdate */
function extractHotPathBodies(content) {
    return extractMethodBlocks(content, /^(Update|FixedUpdate|LateUpdate)$/);
}

/** Extracts bodies of Update only */
function extractUpdateOnlyBodies(content) {
    return extractMethodBlocks(content, /^Update$/);
}

/** Calculates line number from offset */
function lineAt(content, index) {
    return content.substring(0, index).split('\n').length;
}

// ============================================================
//  MAIN AUDITOR
// ============================================================
function auditScripts(dir) {
    if (!fs.existsSync(dir)) {
        console.error(`Error: Directory ${dir} does not exist.`);
        process.exit(1);
    }

    const files = fs.readdirSync(dir, { recursive: true });
    console.log(`🔍 Starting audit in: ${dir}\n`);

    let totalIssues = 0;

    files.forEach(file => {
        const fullPath = path.join(dir, file);
        if (!fs.statSync(fullPath).isFile() || !file.endsWith('.cs')) return;

        const content = fs.readFileSync(fullPath, 'utf8');
        const lines = content.split('\n');
        const basename = path.basename(file);
        let fileIssues = 0;

        // ── 1. Length rule
        rules.filter(r => r.type === 'length').forEach(rule => {
            if (lines.length > rule.threshold) {
                console.log(`  [${rule.severity}] ${basename} — ${rule.message} (${lines.length} lines)`);
                fileIssues++;
            }
        });

        // ── 2. Global rules
        rules.filter(r => r.scope === 'global').forEach(rule => {
            rule.regex.lastIndex = 0;
            let match;
            while ((match = rule.regex.exec(content)) !== null) {
                const ln = lineAt(content, match.index);
                console.log(`  [${rule.severity}] ${basename}:${ln} — ${rule.message}`);
                fileIssues++;
            }
        });

        // ── 3. Hot Path rules (Update/FixedUpdate/LateUpdate)
        const hotpathRules = rules.filter(r => r.scope === 'hotpath');
        if (hotpathRules.length > 0) {
            const bodies = extractHotPathBodies(content);
            bodies.forEach(({ name, body, bodyStart }) => {
                hotpathRules.forEach(rule => {
                    rule.regex.lastIndex = 0;
                    let match;
                    while ((match = rule.regex.exec(body)) !== null) {
                        const ln = lineAt(content, bodyStart + match.index);
                        console.log(`  [${rule.severity}] ${basename}:${ln} — ${rule.message} (in ${name})`);
                        fileIssues++;
                    }
                });
            });
        }

        // ── 4. Update-only rules (physics logic)
        const updateOnlyRules = rules.filter(r => r.scope === 'hotpath_update_only');
        if (updateOnlyRules.length > 0) {
            const bodies = extractUpdateOnlyBodies(content);
            bodies.forEach(({ name, body, bodyStart }) => {
                updateOnlyRules.forEach(rule => {
                    rule.regex.lastIndex = 0;
                    let match;
                    while ((match = rule.regex.exec(body)) !== null) {
                        const ln = lineAt(content, bodyStart + match.index);
                        console.log(`  [${rule.severity}] ${basename}:${ln} — ${rule.message} (in ${name})`);
                        fileIssues++;
                    }
                });
            });
        }

        // ── 5. Specific Method rules
        const methodRules = rules.filter(r => r.scope === 'method');
        if (methodRules.length > 0) {
            methodRules.forEach(rule => {
                const blocks = extractMethodBlocks(content, rule.methodPattern);
                blocks.forEach(({ name, body, bodyStart }) => {
                    rule.regex.lastIndex = 0;
                    let match;
                    while ((match = rule.regex.exec(body)) !== null) {
                        const ln = lineAt(content, bodyStart + match.index);
                        console.log(`  [${rule.severity}] ${basename}:${ln} — ${rule.message} (in ${name})`);
                        fileIssues++;
                    }
                });
            });
        }

        if (fileIssues > 0) {
            console.log(`  └─ ${fileIssues} issue(s) in ${basename}\n`);
            totalIssues += fileIssues;
        }
    });

    console.log(`\n${'─'.repeat(60)}`);
    if (totalIssues === 0) {
        console.log(`✅ Audit complete. No issues detected.`);
    } else {
        console.log(`⚠️  Audit complete. ${totalIssues} total issue(s) found.`);
        console.log(`   → Run 'node execution/unity-doctor.js' for a detailed report.`);
    }
}

// Run on project scripts
const scriptsPath = path.resolve(__dirname, '../Assets/_Project/Scripts');
auditScripts(scriptsPath);
