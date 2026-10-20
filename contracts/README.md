# Application contract v1

[`application.schema.json`](application.schema.json) defines application data only. AG-UI owns messages, tool calls, interrupts and approval responses. Native Harness state may coexist with the `scene` key; the application does not filter out other Harness capabilities.

| Definition | Meaning |
|---|---|
| `SceneSnapshot` | Authoritative scene with `schemaVersion: 1`, revision, objects and selected/highlighted ids |
| `EditRequest` | Arguments of native approval-gated `edit_objects`: `ids`, `expectedRevision`, `edit` |
| `OperationReceipt` | Result keyed by native invocation identity, affected ids and old/new revision |
| `ApplicationError` | Safe error code/message, retryability and correlation id |
| `AgentState` | Required `scene`; optional `receipt`, `error`, `provider: {name, model}`, `traceId`; native extra state allowed |

## Invariants

All numbers must be finite, including values received through in-process calls rather than JSON. Positions are centers in generic units, with Y up, bounded to [-1000, 1000]. Dimensions are full extents in [0.1, 100]. Spheres require x=y=z; cylinders require x=z, with y as height. Object ids are unique. Selection and highlight ids must exist in the scene.

A scene contains 1-64 objects. An edit targets 1-16 distinct existing ids and includes at least one non-null valid `color` or `position`. Either field may be absent or null, meaning unchanged; `{}` and an edit with both fields null are rejected. No unknown edit fields are accepted. A non-null position is absolute and applies equally to every target. Resolve relative requests by reading current properties before making the edit call. Geometry, dimensions, ids, names and tags are not editable. Preserve exact native arguments in approval responses, including nulls; preview projects null as unchanged rather than rewriting the call.

Preview is a projection of the exact native pending tool arguments onto the current scene. It is not a persisted preparation or a committed state. The UI must not apply preview values to authoritative objects. At execution, the backend revalidates ownership, native approval, ids, values and expected revision under its session lock. A committed edit increments revision exactly once; denial, cancellation, stale arguments and replay cannot apply an edit. A receipt for a non-committed outcome does not advance revision. The backend owns persistence and replay prevention.

JSON Schema covers structure and numeric bounds. Cross-field invariants above require semantic validation. Clients must not replace server scene state or treat an arbitrary caller-supplied thread id as authorization.

## Original synthetic fixtures

- [`fixtures/seed.scene.json`](fixtures/seed.scene.json): 12 objects, revision 0. Blue cylinders are exactly `cylinder-01` and `cylinder-02`.
- [`fixtures/move.request.json`](fixtures/move.request.json): move `box-01` from (-4, 1, -3) to (-4, 3, -3), preserving color and all other objects.
- [`fixtures/move.receipt.json`](fixtures/move.receipt.json): committed result, revision 0 to 1. Its operation id is an illustrative native invocation identity.

These fixtures are deterministic application examples. Native transport compatibility must be checked against the actual installed adapters; do not infer it from application schema version 1.
