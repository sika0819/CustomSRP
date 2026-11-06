---
name: unity-arch-clean-code-conventions
description: Naming standards and encapsulation for Unity. Defines Struct vs. Class usage and ensures the Unity Inspector remains professional.
---

# Sub-module: Clean Code, Conventions, and Unity Naming

**Purpose:** Ensure code is readable, maintainable, and professional, following industry standards for Unity and C#.

---

## 1. Naming and Code Aesthetics

In collaborative projects, consistency is more important than personal preference.

* **Classes & Methods:** PascalCase (e.g., `PlayerStats`, `TakeDamage()`).
* **Private Variables:** camelCase with an underscore prefix (e.g., `_currentHealth`). **Why:** Quick identification of class variables vs local variables in long methods.
* **Public Variables/Properties:** PascalCase (e.g., `MaxHealth`).
* **Constants & Enums:** PASCAL_CASE or PascalCase per team standard, but never magic numbers.

---

## 2. Encapsulation and the Unity Inspector

Unity balances C# encapsulation with design flexibility. Navigate this trade-off intelligently.

* **Senior Secret:** Prefer `[SerializeField] private` over `public`. **Why:** Exposes variables to designers in the Inspector without letting other scripts manipulate them haphazardly.
* **Read-only variables in Inspector:** For informational fields (like "IsGrounded"), use `[field: SerializeField] public bool IsGrounded { get; private set; }`.

---

## 3. Classes vs. Structs in Unity

This choice massively affects memory and performance (GC Alloc).

* **Use Classes (Reference Types):** For objects with identity (Player, Enemy, Manager).
* **Use Structs (Value Types):** For small, pure data containers created thousands of times (e.g., `DamageData`, `InventorySlot`). **Why:** Structs live on the Stack and don't generate Garbage Collector pressure, optimizing FPS.

---

## 4. The Inspector is Part of the Code

A messy Inspector causes human error in level design.

* Use organization attributes: `[Header("Settings")]`, `[Space]`, `[Tooltip("Field description")]`.
* **Forbidden:** Leaving public variables without tooltips if their function isn't obvious. The tooltip is living documentation for the designer.
