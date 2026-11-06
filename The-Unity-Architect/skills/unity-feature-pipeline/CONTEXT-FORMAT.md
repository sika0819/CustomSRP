# CONTEXT.md Format

This file is the **Ubiquitous Language Glossary** for the project. It contains NO implementation details, only definitions of domain terms and their relationships.

## Structure

```markdown
# {Context Name / Project Name}

{One or two sentences describing what this context is and why it exists.}

## Language (Glossary)

**TermName**:
{A concise one-sentence definition.}
_Avoid_: {Forbidden or ambiguous terms}

## Relationships

- A **[Entity A]** owns one or more **[Entity B]**
- An **[Event X]** is triggered by **[Entity C]**

## Example Dialogue

> **Dev:** "When a Player collects a PowerUp, is the object destroyed?"
> **Architect:** "No — the PowerUp is disabled and returned to the ObjectPool."

## Resolved Ambiguities

- "Trigger" was used for both Animations and Physics — resolution: we will use "AnimEvent" and "ColliderTrigger" to distinguish them.
```

## Rules
- **Be Opinionated:** If multiple words exist for the same concept, pick the best one and forbid the others.
- **Tight Definitions:** Maximum one sentence. Define what it IS, not what it does.
- **Clear Relationships:** Use bold for defined terms.
- **Domain Only:** Do not include general programming concepts (e.g., "class", "array", "timeout"). Only concepts unique to the game/system belong here.
