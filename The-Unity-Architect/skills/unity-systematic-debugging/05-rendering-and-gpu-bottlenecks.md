---
name: unity-debug-rendering-gpu
description: Identifying visual bottlenecks. Distinguishing between Vertex Bound, Pixel Bound, and Batching issues.
---

# Sub-module: Rendering and GPU Bottlenecks

## 1. Identify the Source: CPU vs. GPU

*   Utilize the Unity **Frame Debugger**.
*   **Draw Calls:** If the number of Draw Calls exceeds 200 on mobile or 2000 on PC, the bottleneck is likely **Batching**.
*   **Why:** Every Draw Call is a command the CPU must prepare for the GPU. Reducing them (via Static/Dynamic Batching or GPU Instancing) is the fastest way to recover FPS.

---

## 2. Shaders and Overdraw

*   Enable the **Overdraw** visualization mode in the Scene View.
*   If the screen appears bright white, there are too many overlapping particles or transparent layers. Reducing particle density or sorting layers is more effective than code optimization in this scenario.
*   **Shader Complexity:** Check the "Stats" window in the Game view for "Batches" and "SetPass calls." High SetPass calls indicate frequent shader or material switching.