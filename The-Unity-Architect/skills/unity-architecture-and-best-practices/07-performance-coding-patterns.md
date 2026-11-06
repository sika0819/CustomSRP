---
name: unity-arch-performance-patterns
description: Micro-architecture for high performance. Guide to avoiding GC.Alloc, optimizing Updates, and using NonAlloc APIs.
---

# Sub-module: Performance Coding Patterns

## 1. Memory Management (Garbage Collector)

In Unity, the GC is your enemy. Lag spikes are often caused by memory cleanup.

*   **Reference Caching:** NEVER use `GetComponent` or `Find` inside `Update`. Cache them in `Awake` or `Start`.
*   **Strings:** Avoid string concatenation in `Update` (e.g., for a currency counter). Use `StringBuilder` or only update the text when the value actually changes.

---

## 2. Update Optimization

*   Not every system needs to run at 60+ FPS.
*   **Time-slicing:** Distribute heavy calculations across multiple frames or use `Coroutine` / `Async` for tasks that don't affect immediate physics.
*   **Update Manager:** For projects with thousands of objects, consider a centralized Manager that calls a custom tick function instead of using thousands of native `Update()` methods. This reduces the C++ to C# context-switch overhead.