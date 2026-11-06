---
name: unity-issue-slicer
description: Technical task breakdown for Unity. Translates the GDD into independent, vertical Issues to ensure modular, component-based implementation.
---

# Skill: Unity Issue Slicer (Task Architect)

The final step of the design pipeline. Use ONLY when the GDD/PRD has been approved. Your mission is to break down the document into a list of technical, independent, and executable Issues. This skill ensures the code is built modularly (Component-Based) and not as a monolith.

---

## ⚖️ The Iron Laws

1. **Tracer Bullets (Vertical Slices):** Each Issue must deliver an end-to-end functional path through all layers (Input -> Logic -> Visual Feedback). NEVER split work horizontally (e.g., "Make all data scripts" then "Make all UI").
2. **Component Independence:** Each task should represent an atomic Unity component. If a task requires modifying 5 scripts, it is poorly sliced.
3. **Definition of Done (DoD):** Each Issue must include a clear, testable acceptance criterion (e.g., "The cube turns red upon receiving damage").
4. **Logical Order:** Tasks must be numbered by dependency order. An Issue must clearly state which other Issue blocks it.

---

## 🛠️ The Backlog (Task List)

Generate a list of Issues using the following format:

### [Issue #X] - [Task Name]
*   **Type:** `[Editor]` (Requires human configuration of Prefabs/References in Unity) or `[Script]` (Pure C# logic that the AI can implement autonomously).
*   **Description:** Brief summary of the end-to-end functionality to be built.
*   **Blocked By:** None, or the ID of the Issue that must be finished first.
*   **Affected Component(s):** Suggested class names.
*   **Acceptance Criteria:** How do we know it works in Play Mode?

---

## 🛑 STRICT SYSTEM DIRECTIVE

Once the list is generated, you must ask the user which task they want to start with. **Only at this moment**, and referring to a specific task, are you allowed to activate the `unity-architecture-and-best-practices` skill to begin coding.

*"Here is the technical breakdown. I have sliced the functionality into [X] independent components to keep the architecture clean. Which of these Issues would you like to start implementing?"*
