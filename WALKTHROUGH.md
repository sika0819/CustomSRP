# 🚀 The Unity Architect — Getting Started Guide

Welcome to the **v1.3.1** of the most disciplined AI framework for Unity development. This guide will help you leverage the full power of the "Grill-Me-With-Docs" pipeline and the Brain Wiki.

## 1. Installation

Run the following command in your project root:
```bash
npx the-unity-architect
```
*Alternatively, use `python install.py` if you have the repo cloned.*

### What happened?
- A centralized folder `The-Unity-Architect/` was created.
- **Critical:** Your AI's instructions were updated. If you use **Antigravity, Cursor, or Windsurf**, the framework injected rules into your `agents.md`, `.cursorrules`, or `CLAUDE.md`.

## 2. Your First Step: The "Ingestion" (For Existing Projects)

If your project is already advanced, do not start building features immediately. You must **ground** the AI in your current architecture.

> [!IMPORTANT]
> **Run this command first:**
> *"I have just installed The Unity Architect. Run a full `unity-doctor.js` scan and a `unity-project-graph.js` analysis. Then, use the **Trigger 3 (Manual Librarian)** to create the initial Index and Lore in the Wiki based on what you see in my folders and scripts."*

This allows the AI to:
1. Identify your current namespaces.
2. Understand your UI system (UI Toolkit vs uGUI).
3. Populate the **Brain Wiki** so future "Grill-Me" sessions are context-aware.

## 3. Creating Your First Feature (The Pipeline)

When you are ready to build something new (e.g., a "Grappling Hook"), follow the **Relentless Pipeline**:

1. **The Request:** *"I want to add a Grappling Hook system."*
2. **The "Grill-Me" Phase:** The AI will stop you. It will ask questions about physics, ranges, and cooldowns. It will create a `CONTEXT.md` (Glossary).
3. **The GDD:** Once the design is solid, the AI generates a Feature Design Document.
4. **The Wiki Update:** The AI will automatically archive this in `The-Unity-Architect/Wiki/Features/`.
5. **Implementation:** Only now will the AI write the code, following the **Unity 6** standards and the **Ubiquitous Language** you defined.

## 4. Maintenance and Audits

- **Before a Build:** Run `node The-Unity-Architect/execution/unity-doctor.js` to catch GC spikes and UI performance issues.
- **When Stuck:** Run `python The-Unity-Architect/execution/parse_editor_log.py` to get a clean summary of the errors without the clutter.
- **Stay Synced:** If you change a system manually, tell the IA: *"Update the wiki with my changes to the Combat system."*

---
*Remember: Discipline is the only way to beat technical debt in Gamedev. Build it correctly from the start.*
