---
name: unity-arch-core-design-patterns
description: Senior implementation of design patterns adapted for Unity. Covers Events, Singletons, and State Machines.
---

# Sub-module: Core Design Patterns for Unity

---

## 1. Event-Driven Communication (Decoupling)

The most important pattern to avoid "Spaghetti Code."

* **Observer Pattern:** Prefer `System.Action` or `UnityEvent` for systems to report to each other without direct knowledge.
* **Why:** If the `Player` emits an `OnDeath` event, the `UIManager` and `AudioManager` can react without the `Player` script needing references to them. This allows you to delete the audio system without breaking player compilation.

---

## 2. The Singleton Dilemma

Singletons are powerful but dangerous tools.

* **Allowed Use:** Persistent, unique Managers that don't store volatile state (e.g., `GameManager`, `SoundManager`).
* **Forbidden Use:** As a "trash bag" for global variables.
* **Senior Refinement:** Consider **ScriptableObject-based Architecture** (Event Channels) as an alternative to Singletons for cross-scene communication.

---

## 3. State Machines

Fundamental for AI, character controllers, and game flows.

* For simple systems, use an `enum` and a `switch`.
* For complex systems, use the **State Pattern** (class-per-state).
* **Refinement:** Always log the previous and new state in debug logs to track broken transitions.

---

## 🛑 THE ARCHITECT'S GOLDEN RULE

Do not choose the "smartest" pattern; choose the simplest one that meets the requirement. Over-engineering is as dangerous as lack of architecture. Always explain to the user why a specific pattern is the best choice for their current use case.
