---
name: unity-debug-dots-ecs
description: Diagnosing multithreading and Entity systems (DOTS/ECS). Guide for dealing with Race Conditions and Burst Compiler issues.
---

# Sub-module: DOTS and ECS Diagnostics

## 1. Multithreading Debugging

Standard `Debug.Log` is dangerous and often restricted within Jobs.

*   **Race Conditions:** Explain that if two threads attempt to write to the same data simultaneously, the result is non-deterministic (random).
*   Use `[ReadOnly]` attributes in Jobs whenever possible to allow the compiler to optimize and avoid write-dependency conflicts.
*   **Safety Checks:** Ensure Unity's Safety Checks are enabled in the Editor to catch concurrent write violations before they cause crashes.

---

## 2. Burst Compiler

*   If a bug only occurs in a Build but not in the Editor, disable the **Burst Compiler** temporarily to see if the issue persists.
*   Use `[BurstCompile]` only on jobs that strictly adhere to High-Performance C# (HPC#) to avoid compilation errors that might fallback to slower, non-Burst paths silently.