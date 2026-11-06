---
name: unity-arch-ui-presentation
description: Architecture for separating game logic from visual interfaces using MVP or MVC patterns. Essential for complex menu systems.
---

# Sub-module: UI and Presentation Architecture

## 1. Separation of Concerns (MVP/MVC)

Game logic should NEVER depend on the UI.

* **Model:** Pure data (e.g., `int Score`). It is unaware of the UI's existence.
* **View:** The Unity component (`TextMeshPro`, `Image`). It only knows how to display data.
* **Presenter/Controller:** The bridge. Listens for changes in the Model and updates the View accordingly.
* **Why:** If you decide to switch from UGUI to UI Toolkit, you only need to rewrite the View. Your game's core remains intact.

---

## 2. UI Optimization

The UI is often the biggest bottleneck on mobile devices.

* **Avoid Raycast Target:** Disable it on images that don't need clicks. This saves unnecessary collision calculations every frame.
* **Canvas Grouping:** Split your UI into multiple Canvases based on their update frequency. Use one Canvas for static elements and another for dynamic ones (e.g., health bars).
