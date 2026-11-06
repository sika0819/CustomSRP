---
name: unity-arch-combat-vfx
description: Best practices for decoupling combat systems, animations, and visual effects. Covers hitboxes and safe animation events.
---

# Sub-module: Combat and VFX Decoupling

## 1. Animations as Feedback, Not Logic

A common mistake is tying attack damage to the exact frame of an animation. If the animation changes, the combat system breaks.

*   **Animation Events:** Use with caution. Prefer having logic call the animation and manage its own impact timer if technical precision is critical.
*   **Visual Feedback:** The combat script should only call `PlayParticle()`; it shouldn't configure the particle's color or size. Leave those details to the visual system.

---

## 2. Hitboxes and Triggers

*   Use **Layer-based Collision** to filter what can hit what. It is much more efficient than checking `if (tag == "Player")` on every collision.
*   Clearly distinguish between **Logic Colliders** (hitboxes) and **Physics Colliders** (environment collision).