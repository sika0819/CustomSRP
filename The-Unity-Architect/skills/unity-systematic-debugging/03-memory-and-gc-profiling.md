---
name: unity-debug-memory-gc
description: Advanced guide for the Unity Profiler. Identifying memory leaks, excessive GC Alloc, and CPU bottlenecks.
---

# Sub-module: Memory and GC Profiling

## 1. The Profiler is the Only Real Diagnosis

NEVER guess a performance issue based on "lag feel."

*   **GC Alloc (Garbage Collector):** Look for the light blue (teal) bars in the CPU Profiler. Identify which script is allocating memory every frame.
*   **Deep Profile:** Warn the user that "Deep Profile" massively slows down the Editor but is necessary to find the exact method in deep hierarchies.

---

## 2. Leaks (Memory Leaks)

*   **Unsubscribed Events:** The #1 culprit for leaks in C#. Ensure every `OnEnable` has a corresponding unsubscription in `OnDisable`.
*   **Why:** A hanging reference prevents the GC from cleaning the object, accumulating RAM until the application crashes (especially on mobile).