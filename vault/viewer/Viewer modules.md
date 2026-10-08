---
title: Viewer modules — who owns what
status: current
created: 2026-10-08
---

*↑ [[vault/Pluto Home|Home]] › [[vault/viewer/Viewer map|Viewer map]]*

To change one thing, read its file and the contract it uses. A module never reaches into another's internals. It goes through the calls listed under **Uses**.

## Model-level modules

| File | Owns | Uses |
|---|---|---|
| `scripts/models.js` | The loaded models (**model contexts**) and which one is active. It also holds the primary context, an adapter over the viewer globals. | — (others register with it) |
| `scripts/modelMesh.js` | One file drawn as reference geometry: shells and beams under one `THREE.Group`, with group colours, eyes, x-ray, model colour and picking. | three.js, the geometry builders, the shaders |
| `scripts/overlays.js` | The model rail (rows, active selection, show / x-ray / colour / rot / zoom / remove), "Add overlay…", file pairing and placement at true coordinates. | `FEAModels`, `FEAModelMesh`, `FEAFeatures.attach / detach / groupOfIn / elemOffFor` |
| `scripts/features.js` | The Groups tab and the features sidecar, **per model** (state in `ctx.feat`). | Only the active model context (see below) |

## The model context

The full contract is in the header of `models.js`. Its parts:

- **Data:**
  - `shellModel()`: the shell domain view, with its nodes.
  - `beamView()`
- **Painting:**
  - `paint(fam, cat)`: write each element's group index.
  - `setGroupLook(on, palette, count)`: group painting on or off, with the palette.
  - `applyVisibility()`: re-apply the group eyes.
- **Node markers:** `markerParent()`, the object they are added to. It carries the model's placement.
- **Look:** `look { visible, xray, color }`, changed through `setVisible`, `setXray` and `setColor`. The primary has no `setColor`: it keeps its field colouring when group painting is off.
- **Groups state:** `feat`, owned by `features.js`.

To make a new feature work on every model, write it against this interface. Don't add one more special case for "overlay vs primary".

## Primary-only for now (model rail stage 3)

These modules still use the viewer globals directly, so they act on the main model only:

- load cases, the legend and the field (`viewer.js`)
- the beam panel (`viewerBeams.js`)
- section cuts (`sectionCut.js`)
- predicates (`predicates.js`)

**`FEAFeatures` API calls for "the main model" vs "the active model":**

- **Main model:** the calls those modules make: `refresh`, `ensureEnvelope`, `envelope`, `markDirty`, `elemOff`, `groupOf`, `writeVis`, `worldOffset`, and loading a model's sidecar.
- **Active model:** Save (`fileName`, `exportJson`, `markSaved`, `isDirty`, `activeEnvelope`).

## Tests

- `node viewer/tests/test_groups.js` loads `models.js` and then `features.js`. Run all `test_*.js` after a change.
- **Browser check:** serve the repo (`python -m http.server`) and open `viewer/index.html?bin=…&features=…&ov=<bin>,<features>`.
