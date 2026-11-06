# ADR Format

Architectural Decision Records (ADRs) live in `The-Unity-Architect/Wiki/ADR/` and use sequential numbering: `0001-slug.md`, `0002-slug.md`, etc.

## Template

```markdown
# {Short title of the decision}

**Context:** {What problem were we solving?}
**Decision:** {What did we decide to do?}
**Rationale:** {Why did we choose this over alternatives? (Trade-offs)}
**Status:** {proposed | accepted | deprecated | superseded}
```

## When to create an ADR?
The 3-point rule must be met:
1. **Hard to reverse:** Changing it later would cost weeks of refactoring.
2. **Surprising without context:** Someone will read the code and think "why didn't they use X?".
3. **Result of a real trade-off:** There were valid options and we chose one with clear consequences.

## Examples of what qualifies:
- Network architecture choice (Mirror vs Netcode).
- Use of ScriptableObjects as a static database vs remote JSON.
- Communication pattern (Events vs Direct References).
- Deliberate deviations from standards: "No ORM used for mobile performance".
