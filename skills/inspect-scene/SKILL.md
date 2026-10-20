---
name: inspect-scene
description: Inspect synthetic scene objects and identify matches without changing geometry.
---

Use list_objects, optionally limited by shape, to read actual object names, colors and tags.
Choose ids from those results rather than guessing color codes or object identifiers.
Use get_objects for exact properties of known ids.
Selection and highlighting do not change object colors or positions.
If a description matches several objects, ask which objects the user intends.
