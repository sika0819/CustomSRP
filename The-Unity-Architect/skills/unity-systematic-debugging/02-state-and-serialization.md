---
name: unity-debug-state-serialization
description: Diagnosing invisible bugs in the Unity Inspector. Guide for detecting serialization errors, corrupted Prefabs, and ScriptableObject issues.
---

# Sub-module: Object State and Serialization

## 1. The Inspector Lies (Sometimes)

Unity does not always synchronize the real state in memory with what you see in the Inspector UI.

*   **Inspector Debug Mode:** Provide instructions for the user to switch the Inspector to "Debug" mode via the three-dots menu (⋮).
*   **Why:** This reveals hidden private variables from other components that might be interfering without the user's knowledge.

---

## 2. Prefabs and Overrides

*   "Invisible" bugs often live in Overridden values on a Prefab instance that haven't been applied to the original asset.
*   Advise the user to check for the blue line indicator to the left of component fields in the Inspector.