# Where the LCD screen geometry actually lives

This is the investigation that sets the architecture, written down because the conclusion
is counter-intuitive and the wrong version of it cost the Grid Schematics 2 prototype a lot
of hand-calibration.

Evidence gathered with `ilscan` (the Mono.Cecil scanner in the RTT Camera repo) against the
shipped SE2 assemblies, plus direct reads of the shipped content files.

## The prototype's blocker was misdiagnosed

The GS2 prototype recorded the problem as "the raycast line intersect point couldn't be
resolved for the LCD panels". That is not what was happening. GS2 resolves a ray against a
panel two different ways, and both work:

- `CursorAim.TryHitPanel` — a slab test against the block's grid AABB, in grid space.
- `RayProber.ProbeSurface` — a real `IPhysics.CastRay`, with `IPhysics` obtained by
  reflecting `GridDamageReceiverComponent._physics`.

What is missing is not the intersection. It is **where the screen sits on the block** — the
plane the screen occupies, the rectangle it covers, and which way its U/V run. Without that,
an exact hit point cannot be turned into a surface coordinate.

That gap is what the prototype's fudge factors are: `GlassDepth = 0.10f` "tuned on-glass",
`UvMode` swap/flip bits, `CalU0..CalV1` "measured on-glass", and a six-click, two-standpoint
calibration that brute-forces 400 candidate plane depths (`Calibration.FinishV2`). Each one
is a hand-measurement of a number the engine declines to hold.

## Why the engine does not hold it

`Keen.Game2.Simulation.WorldObjects.CubeBlocks.Lcd.LcdPanelSurface` is:

```
field StringId               MeshPartName
field Vector2I               Resolution
field PBRMaterialDefinition  DefaultScreenMaterial
field Single                 AspectRatio
field Boolean                UseOnlineTexture
field String                 DefaultDisplayName
```

A **name**, and no geometry.

The only reader of `MeshPartName` anywhere in the shipped assemblies is
`LcdPanelSurfaceRenderComponent::UpdateMaterialReplacements()`. It builds
`MeshPartMaterialPair`s keyed by `ResourceHandle<ModelAsset>` and hands them to
`BlockRenderComponent::SetMaterialReplacements`.

So the engine's whole LCD mechanism is: **swap a material onto a named mesh part, and let
the model's own UVs place the image.** It never computes a screen quad, which is why there
is no screen quad to read.

Confirming that the CPU side really is empty:

- `Keen.VRage.Core.Model.Data.MeshData/Part` is `{ MeshPartId PartId, MeshMaterialId Material,
  Int32 IndicesCount }` — a draw-range descriptor. No vertices, no bounds. Geometry is GPU-side.
- `ModelMeshPartsExtractor` looks promising by name but is `IBlobExtractor` +
  `IDesignTimeResourceLocator` — content pipeline, not a runtime path.

**This part of the blocker is real.** There is no runtime API that will hand over the screen
rectangle.

## The data does exist — in the content files

Block definitions ship as plain JSON. `…_LcdMultiPanelDefinition.def` carries everything
except the geometry, directly:

| Block | MeshPartName | Resolution | AspectRatio |
|---|---|---|---|
| LCD flat 1.5m/0.5m | `LCDScreen_Off` | 512×512 | 1.0 |
| LCD flat 2.5m | `Fracture_16_Hide-LCDScreen_Off` | 512×512 | 1.0 |
| Corner LCD 2.5m | `Fracture_16_Hide-LCDScreen_Off` | 512×128 | 5.0 |
| Wide LCD 1.5m | `LCDScreen_Off` | 512×512 | 1.33 |

Two things worth noting. Resolution is *not* the visual shape — a wide LCD is a square
render target displayed through aspect 1.33, so square pixels are not a safe assumption
anywhere in this API. And the same mesh part name recurs across blocks, so the part name
alone is not a catalog key; the block subtype is.

The geometry is in the `.vrm` models beside them (869 under `Content/Blocks`).

## Reading a `.vrm`: what works and what does not

`Keen.VRage.Core.Model.ModelImporter` in **VRage.Core.dll** is a public, CPU-side model
reader with exactly the surface needed: `ImportData(Stream, string, tags)`,
`GetTagData()`, and tags including `TAG_VERTICES`, `TAG_TEXCOORDS0`, `TAG_INDICES`,
`TAG_MESH_PARTS`, `TAG_BOUNDING_BOX`. `ReadMeshParts` yields `MeshPartInfo`
= `{ StringId PartId, StringId Material, List<int> Indices }`.

**Reading it outside the game does not work, and it is worth knowing why before trying.**
It was tried:

- `.vrm` files begin with magic `VR3B` — the same container as SE2 saves, not a raw tag stream.
- `ModelImporter.ImportData(Stream, …)` copies the stream verbatim and calls `LoadTagData`
  at position 0, which immediately expects `ReadString()` then a string array whose first
  element contains `"Version:"`. Against a raw `.vrm` it throws
  `FormatException: Too many bytes in what should have been a 7-bit encoded integer`.
- `ImportData(FileHandle, …)` is not a smarter path — it is a two-line wrapper that calls
  `FileHandle.OpenRead` and delegates to the same Stream overload.
- The payload is compressed, not merely offset: the ASCII `Version:` and `LCDScreen`
  markers appear nowhere in the file.
- There is no extracted blob cache on disk to shortcut to. `%AppData%\SpaceEngineers2`
  holds only settings, saves and a shader cache.

So an offline baker would have to reimplement the VR3B container and its compression. That
is a real reverse-engineering project with an ongoing maintenance cost every time the format
moves.

## The route taken — superseded, see the next section

The plan below was to **bake the catalog from inside the game**, where the VFS already decodes
VR3B, and ship the result as data. That is still the fallback, but it turned out to be
unnecessary for stock blocks: the screen placement is already in the block definition. Keep
reading.

1. A dev-only bake command walks the `LcdMultiPanelDefinition`s, and for each block subtype
   loads its model through the engine's own file handles, selects the mesh part named by the
   definition, and takes that part's vertices and UV0.
2. Fit a quad to the part: origin, two edge vectors, normal, and a planar residual so a
   curved screen is visible rather than silently wrong.
3. Write `catalog/lcd-catalog.json`, review it, commit it.
4. At runtime the API only ever does a dictionary lookup and a projection. No model parsing,
   no importer, no calibration for stock blocks.

Calibration survives only as the fallback for modded panels with no catalog entry — which is
where it belongs, and it is now the exception rather than the standard path.

## The screen placement is in the definition all along

Chasing the model path turned up something better. `…_BlockModelDefinition.def` carries a list
of **dummies** — named transform-plus-scale markers baked into the block — and every LCD block
has one named `LcdPanel`:

| Block | Dummy scale | Ratio | Declared AspectRatio |
|---|---|---|---|
| LCD flat 2.5m | 2.5 x 2.5 x 0.2 | 1.0 | 1.0 |
| LCD flat 0.5m | 0.5 x 0.5 x 0.2 | 1.0 | 1.0 |
| Corner LCD 2.5m | 2.5 x 0.5 x 0.5 | **5.0** | **5** |
| Wide LCD 1.5m | 1.5 x 1.25 x 0.2 | 1.2 | 1.33 |

The corner LCD is the one that settles it. A 2.5 x 0.5 box is a ratio of exactly 5.0, matching
a declared aspect of 5 that nothing else in the file would predict.

And it is reachable at runtime, which is the part that matters. `BlockModelComponent.Definition`
derives from `ModelComponentDefinition`, which exposes
`DummiesByType : ListDictionaryReader<DummyTypeDefinition, ModelDummy>`. (It does not appear on
`BlockModelComponentDefinition`'s own member list — it is inherited, which is exactly the kind of
absence that is easy to mistake for "not there".) `ModelDummy` is
`{ Name, RelativeTransformWithEulerHint Transform, Vector3 Scale, DummyTypeDefinition Type }` with
`GetMatrix()` returning `Matrix.CreateFromTransformScale(Orientation, Position, Scale)` — a full
TRS for a unit box.

So the screen's position, orientation and size come out of the definition directly. **No model
parsing, no VR3B, no bake step, and no catalog for stock blocks.**

### What is not yet confirmed

The wide LCD's 1.2 against a declared 1.33 is unexplained. Either the visible glass is inset
within the dummy box, or the declared aspect is a rounded 4:3 the geometry does not honour.
Separately, the same dummy feeds an interaction collider — `DummyShapedEntityDetectorComponent`
takes `_halfExtents` from it — and a detection volume usually carries margin over the thing it
detects. Both point the same way: the box may be a little larger than the glass.

That is why the catalog survives as an **override** rather than being deleted. If measurement
shows a consistent inset, it is stored per block type as a correction — a much smaller problem
than deriving the quad by hand, and measured once per block type rather than once per placement.

The `LcdPanel` dummy is matched by **name**, not by its type GUID (`b15d441f-…`). The name is
stable and legible in the logs; the GUID would be one more magic constant to keep in step with
the game.

## Cross-checks and incidental findings

- `SweepQueryHit` carries `Position`, `Normal`, `SubCollider`, `MaterialId` and `Fraction`.
  A physics cast is therefore a good independent check that a baked quad is right, and a
  usable fallback for a modded panel before the user calibrates it. Whether the block
  collider actually models the recessed screen — rather than a plain box — is untested, so
  it is a cross-check and not the primary source.
- `Resolution` comes free from the definition. GS2 hand-maintains a `VectorLcd.PanelRes`
  map; that map is unnecessary and can be deleted once a consumer moves onto this API.
- The quad must be stored in **model space**, not relative to the block's grid AABB. A
  catalog entry is per block subtype, and an AABB-relative offset changes with the block's
  rotation in the grid — which is precisely why the prototype's calibration is per placement
  rather than per block type. See below for the transform that makes this work.

## Getting into model space

This was briefly written up as an open question — "`CubeBlockComponent.AABB` gives cells, not
an orientation" — on the basis that the GS2 prototype works in grid space off the AABB and
calibrates per placement. That was too quick a conclusion: GS2 had already solved it, in
`BlockShapes.BasisOf`, and the engine exposes the transform more directly still.

`CubeBlockComponent` carries a `ChildTransformComponent` whose `ChildTransform` is a
`RelativeTransform` — `{ Vector3 Position, Quaternion Orientation }`, the block relative to
its grid. So the whole chain is engine-provided, with no basis matrix assembled by hand:

```
world  --WorldTransform.TransformInv(grid.GetWorldTransform(Vector3I.Zero))-->  grid-local metres
       --RelativeTransform.TransformInv(childTransform)-->                      block model space
```

Directions use the `…DirectionInv` variants at both steps. Applying the positional inverse to
a direction gives a cursor that drifts with distance from the grid origin — correct near the
origin, visibly wrong at the far end of a large ship, which is a nasty shape of bug to chase.

Two details worth keeping:

- **`Base6Directions.GetVector(Direction)`** already maps a direction to a vector.
  `BlockShapes.DirVec` switches on `dir.ToString()`, which allocates on a per-block path;
  there is no need to carry that over.
- **`IntegerOrientation` derives `Right` itself** (`get_Right()` via `GetOppositeDirection`),
  so the manual `Up × Forward` cross product in `BasisOf` is not needed either.

`BlockFrame.OrientationDisagreementDegrees` cross-checks the quaternion against the block's
`IntegerOrientation`, which is exact by construction. They must agree; if they ever do not,
the assumption that `ChildTransform` is block-relative-to-grid is wrong — an intermediate
hierarchy node would do it — and the failure mode is a cursor that looks plausibly placed and
is consistently off, which is exactly the kind of thing that survives casual testing.
