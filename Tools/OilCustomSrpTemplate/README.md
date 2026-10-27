# Hub template: 3D CustomSRP Oil NPR

Packs a **slim** Unity Hub project template from this repo’s oil-painting Custom SRP (no GameMain / GameFramework / island content).

## Pack & install

```bash
python3 Tools/OilCustomSrpTemplate/pack.py
# optional: also appear beside URP in Editor ProjectTemplates (may prompt sudo)
python3 Tools/OilCustomSrpTemplate/pack.py --also-editor-templates
```

Writes Hub layout:

```text
~/Unity user templates/3d-customsrp-oil-npr/
  3d-customsrp-oil-npr.tgz
  package.json
```

That folder is your Hub **Settings → Templates** path.

Then:

1. **Fully quit** Unity Hub (Cmd+Q) and reopen.
2. **New project** → Editor **6000.6.4f1** → search **3D CustomSRP Oil NPR**.
3. Or open the **Templates** page → filter **Custom**.

## Why it might not show

Hub filters custom templates with **exact** equality:

`package.json "unity"` === selected editor version

| Bad | Good |
|-----|------|
| `"unity": "6000.6"` | `"unity": "6000.6.4f1"` |

`pack.py` reads `ProjectSettings/ProjectVersion.txt` so this stays in sync. Override with `--unity 6000.6.4f1` if needed.

## What’s in the template

| Include | Notes |
|---------|--------|
| `Assets/CustomSRP` | Runtime / Shaders / Materials / Settings / Textures / Empty scene |
| `Assets/TemplateStubs` | Tiny `Game.*` + `Log` so the pipeline compiles alone |
| Project Settings | Linear color space, CustomRenderPipelineAsset assigned |

| Exclude | Why |
|---------|-----|
| GameMain / Terrain / GameFramework | Game-specific |
| Moorea + SRP tutorial scenes | Size |
| Full OilAtmosphereDriver / island bake tools | Hard GameMain coupling |
