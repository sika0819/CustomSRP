---
name: unity-debug-scientific-method
description: The core of systematic debugging. Guide for building telemetry logs, isolating symptoms, and conducting controlled experiments.
---

# Sub-module: Scientific Method and Telemetry Logs

## 1. The Log as a Measurement Instrument

In Unity, a log is not just a sentence; it is a data point.

*   **Request Context:** A log without a timestamp or frame ID is useless. Instruct the user to include `Time.frameCount` in their logs if the bug is time-dependent.
*   **Conditional Logs:** To avoid console spam, suggest using conditional `Debug.Log` calls or dedicated debug flags.
*   **Why:** Saturating the console with thousands of messages per second freezes the Unity Editor and hides the real error behind I/O bottlenecks.

---

## 2. Scientific Isolation

*   **Controlled Experiment:** Before changing code, request the disabling of all suspicious GameObjects except the core of the problem.
*   If the bug disappears when system X is disabled, the culprit is the interaction between X and Y, not necessarily the code in Y alone.