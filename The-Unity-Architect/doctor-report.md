# Unity Architect — Diagnostics Report

Generated on: 11/5/2026, 9:51:21 PM
Project root: `/Users/sika/Desktop/code/BoluoProjectNew/CustomSRP`
Roots: `Assets/GameMain`, `Assets/CustomSRP`, `Assets/WanosAudio`, `Assets/Scripts`, `Assets/Editor`
Total files scanned: 469
Total issues found: 14

## ⚠️ Findings

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/Boot/BootEarlyCover.cs` (Line 140)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/Boot/BootEarlyCover.cs` (Line 150)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/Boot/BootLoadingService.cs` (Line 598)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/Services/DevicePresetShotHelper.cs` (Line 205)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/Services/DevicePresetShotHelper.cs` (Line 218)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/UI/Forms/IslandMainForm.cs` (Line 188)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/UI/Forms/IslandMainForm.cs` (Line 1011)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/UI/Forms/LoadingForm.cs` (Line 318)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/UI/Forms/LoadingForm.cs` (Line 326)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/UI/Forms/LoadingForm.cs` (Line 335)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/UI/Forms/LoadingForm.cs` (Line 350)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/UI/Forms/PresetForm.cs` (Line 1401)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/UI/Forms/PresetForm.cs` (Line 1403)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

### [WARNING] Opacity used for hiding (CPU/GPU waste)
- **File:** `Assets/GameMain/UI/UiPreview.cs` (Line 160)
- **Issue:** `style.opacity = 0` keeps the element in the render and layout pipeline.
- **Fix:** Use `style.display = DisplayStyle.None` to remove from layout, or `style.visibility = Visibility.Hidden` to keep space but stop rendering.
- **Skill Reference:** unity-ui-toolkit/SKILL.md §2 — Smart Hiding

