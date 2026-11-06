---
name: unity-arch-modular-glue-code
description: Techniques for connecting components without creating rigid interdependencies. Covers [RequireComponent] and explicit dependency management.
---

# Sub-module: Modular Systems and "Glue" Code

## 1. Explicit vs. Implicit Dependencies

The most common error is assuming a component exists without validating it, leading to the dreaded `NullReferenceException`.

* **Use `[RequireComponent]`:** Forces Unity to add the necessary component automatically. **Why:** It documents requirements directly in the code and prevents runtime errors.
* **Simple Dependency Injection:** Prefer passing references via the Inspector or through an Init method/constructor instead of using `GameObject.Find()`.

---

## 2. Prefabs and Nesting

Modular architecture lives within Prefabs.

* Advise the user to split complex objects into **Nested Prefabs**. For example, an `Enemy` can have a `HealthBar` prefab and a `VisualModel` prefab.
* This allows parallel work on different parts of a system without locking the main file.
