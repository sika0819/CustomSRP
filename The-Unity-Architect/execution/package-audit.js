const fs = require('fs');
const path = require('path');

const rules = [
    {
        id: "SAFETY_SYSTEM_IO",
        name: "File System Access",
        regex: /System\.IO/g,
        severity: "CRITICAL",
        message: "System.IO usage detected. Verify if it is necessary (e.g., caching) or malicious."
    },
    {
        id: "SAFETY_SYSTEM_NET",
        name: "Network Access",
        regex: /System\.Net/g,
        severity: "CRITICAL",
        message: "System.Net usage detected. Potential unauthorized external communication."
    },
    {
        id: "SAFETY_OPEN_URL",
        name: "URL Opening",
        regex: /Application\.OpenURL/g,
        severity: "WARNING",
        message: "Opens external URLs. Verify the destination."
    },
    {
        id: "COMPAT_BUILTIN_SHADER",
        name: "Built-in Pipeline Shader (Legacy)",
        regex: /CGPROGRAM|UnityCG\.cginc/g,
        extension: '.shader',
        severity: "WARNING",
        message: "Built-in type shader detected. In URP it will look PINK. Requires conversion."
    },
    {
        id: "COMPAT_URP_SHADER",
        name: "URP Shader",
        regex: /HLSLPROGRAM|Packages\/com\.unity\.render-pipelines\.universal/g,
        extension: '.shader',
        severity: "INFO",
        message: "URP-compatible shader detected."
    },
    {
        id: "PERF_GETCOMPONENT_UPDATE",
        name: "GetComponent in Update",
        regex: /void\s+(Update|FixedUpdate|LateUpdate)\s*\(.*\)[\s\S]*?GetComponent/g,
        severity: "WARNING",
        message: "GetComponent in Update methods. Performance impact, better to cache in Awake/Start."
    }
];

function audit(dir) {
    if (!fs.existsSync(dir)) {
        console.error(`Error: Directory not found: ${dir}`);
        process.exit(1);
    }

    console.log(`\n🛡️  Starting Security and Compatibility Audit in: ${dir}\n`);
    let findings = 0;

    const walk = (currentDir) => {
        const files = fs.readdirSync(currentDir);
        for (const file of files) {
            const fullPath = path.join(currentDir, file);
            if (fs.statSync(fullPath).isDirectory()) {
                walk(fullPath);
            } else {
                checkFile(fullPath);
            }
        }
    };

    const checkFile = (filePath) => {
        const ext = path.extname(filePath);
        if (ext !== '.cs' && ext !== '.shader') return;

        const content = fs.readFileSync(filePath, 'utf8');
        rules.forEach(rule => {
            if (rule.extension && rule.extension !== ext) return;

            let match;
            rule.regex.lastIndex = 0;
            while ((match = rule.regex.exec(content)) !== null) {
                const lines = content.substring(0, match.index).split('\n');
                const lineNum = lines.length;
                console.log(`[${rule.severity}] ${path.relative(dir, filePath)}:${lineNum} - ${rule.message}`);
                findings++;
            }
        });
    };

    walk(dir);
    console.log(`\n✅ Audit finished. Total findings: ${findings}\n`);
}

const target = process.argv[2] || './Assets/Hovl Studio';
audit(path.resolve(target));
