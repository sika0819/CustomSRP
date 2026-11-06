---
name: unity-gdd-writer
description: Design documentation for Unity features. Consolidates the validated design from the Grill phase into a structured "Contract of Truth" (PRD/GDD) and updates the project Wiki.
---

# Skill: Unity GDD Writer (Design Architect)

The second step of the pipeline. Use ONLY when the feature design has been validated through `unity-quest-giver`. Your goal is to consolidate all extracted knowledge into a structured document that serves as the "Contract of Truth" for implementation.

---

## ⚖️ The Iron Laws

1. **Living Document:** The generated GDD/PRD must be saved in the repository (e.g., `The-Unity-Architect/Wiki/Features/[FeatureName].md`).
2. **Clarity Over Complexity:** Use tables and diagrams (Mermaid) where necessary to explain logical flows.
3. **Zero Code:** Do NOT include C# snippets or specific file paths in the GDD. These become outdated quickly. Define interfaces and behaviors, not implementations.
4. **User Validation:** The document must end with a request for approval/signature from the user.

---

## 📋 Mandatory Document Structure

Each generated GDD must follow this structure:

### 1. Feature Summary (Elevator Pitch)
*   **Name:**
*   **Objective:** What problem does it solve or what fun does it add?
*   **Scope:** What does this feature *not* do (Boundaries)?

### 2. Business Rules (Game Logic)
*   **Main Flow:** Step-by-step player experience.
*   **States:** List of possible states (e.g., Idle, Charging, Cooldown).
*   **Failure Conditions:** What stops the process?

### 3. Technical Requirements
*   **Required Components:** List of theoretical components (e.g., `InputHandler`, `MovementController`).
*   **Configuration:** Parameters that must be exposed in the Inspector (e.g., `Speed`, `JumpForce`).
*   **Dependencies:** Other systems it must communicate with (e.g., `InventorySystem`, `AudioManager`).

### 4. Edge Cases and Exceptions
*   Record of the critical points resolved during the "Grill" phase.

### 5. Out of Scope
*   **Crucial for avoiding Feature Creep:** Explicitly list what will **NOT** be built in this iteration, even if it seems related.

---

## 🛑 STRICT SYSTEM DIRECTIVE

Your main output in this phase is a formatted Markdown block that the user must copy or that you must write to a new file.

Once generated, you must execute **Trigger: Wiki Sync**:
*   Update `The-Unity-Architect/Wiki/Index.md` with a link to the new GDD.
*   Update `The-Unity-Architect/Wiki/Log.md` recording the creation of this new feature.
*   If the GDD introduces new domain concepts, create/update the corresponding files in `The-Unity-Architect/Wiki/Lore/` or `The-Unity-Architect/Wiki/Systems/`.

Finally, ask: *"I have updated the project Wiki. Does this GDD accurately reflect what we want to build? If so, let's proceed to task breakdown with `unity-issue-slicer`."*
