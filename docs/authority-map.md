# EveUnity Authority Map

## Objective

Lower provider-authored Eve surfaces into a playable Unity scene without
importing provider product code into the generic runtime.

## Owner

EveUnity owns Unity scene projection, `GameObject` lifecycle, camera and input
drivers, asset-provider hooks, package release, runtime tests, and captures.

## Inputs

- CultMesh rendezvous endpoint
- CultMesh Verse catalog
- `gamecult.eve.provider_advertisement.v1`
- `gamecult.eve.surface.v1`
- provider asset-manifest documents
- runtime-selected asset-variant metadata, including native rendering-program bindings
- generic field-volume lifecycle declarations and logical texture/scalar/matrix ports
- generic viewport-to-texture scale bindings between logical vector and texture ports
- provider command receipts
- semantic render-channel inclusion and exclusion policy
- Unity input and local presentation assets

## Outputs

- disposable Unity scene projection
- `gamecult.eve.command_invocation.v1` intents sent through the advertised boundary
- runtime capability, test, release, and capture evidence
- owner-controlled PlayMode capture from `TestProject`

## Derived State

`ActiveWorld`, scene objects, transforms, markers, camera pose, native
render-channel layer mappings, resolved native shader ports/passes, asset caches,
per-volume raymarch targets, temporal-history targets, previous camera matrices,
presentation counters, and receipt display state are projections. They are not
world truth. Temporal targets reset when the volume program, node, or render size
changes and can never become provider state.

Camera-relative field viewports have one generic resolver. The advertised
viewport frame owns its extent, spatial-cell scale, raster texels per cell, and
snap policy; fog volumes and stateless particle programs consume the identical
projected `EveFieldsViewport`. URP TAA state is likewise derived from the
world's neutral `temporal-reprojection.v1` camera contract and is restored on
release.

Particle materials may consume advertised presentation-only inputs through
logical native metadata: provider-owned textures, viewport-to-texture scale,
and the current render-frame index. The runtime frame index owns temporal
coverage only; it cannot drive particle identity, phase, position, or any
daemon-visible state.

## Forbidden Writers

Unity transforms, input drivers, camera rigs, scene sinks, asset caches, and
pending receipts may not mutate or simulate provider world state. Plugin
projection shells may not implement Sai, Norn, or TeX semantics. Portable
surfaces may not carry Unity layer numbers, camera culling masks, shader property
names, shader keywords, or native pass indices. Those bindings belong to the
selected runtime asset variant. A native volume program that advertises a
temporal pass must also advertise its current-sample, history,
previous-view-projection, and reset-history ports; partial temporal programs fail
closed.

Viewport-sized shader inputs are likewise presentation state. A provider may
relate a logical vector port to a logical native texture port through
`viewportTextureScaleBindings`; EveUnity derives the vector from the active
camera viewport and resolved texture dimensions on every render. A provider
literal is not allowed to impersonate those runtime dimensions, and an
unresolvable relation fails closed.

The provider selects camera semantics and framing from its authoritative mode.
`planar.top-down-follow.v1` derives a downward presentation from the advertised
target and framing values. `perspective.entity-forward-follow.v1` derives its
view direction from the presented entity rotation and applies the same generic
distance, screen-position, damping, and lens contract. When the provider also
advertises `aim.convergence-point.v1`, the camera and aim marker consume one
derived convergence point from the `aim.presentation` node. The native camera
rotation and position are solved together so the optical axis reaches that
point while the followed entity remains at the advertised screen coordinate.
EveUnity owns only this native projection. It cannot collapse distinct provider
modes into one camera opinion or infer a product camera from entity kind.

## Shared Paths

Initial connect discovers a Verse endpoint and selects a provider surface by
semantic kind. Refresh, reconnect, and terminal-receipt reconciliation consume
that provider's surface documents. Movement, focus, target, and action input all
become `gamecult.eve.command_invocation.v1` through the provider-advertised
command boundary.

## Cut Line

Aetheria retains its daemon, authored surfaces, assets, command acceptance,
receipts, adapter, and provider conformance scenarios. Eve retains contracts
and conformance policy. EveUnity retains only generic Unity projection and its
runtime lifecycle.

## Capture Proofs

`TestProject` proves that a clean Unity project can install CultLib, Eve
contracts, and EveUnity without Aetheria or Brokkr assemblies. Its world-smoke
capture is the generic control witness. The Aetheria product witness gives the
same client only a rendezvous endpoint, then proves Verse discovery, provider
selection, asset loading, movement, commands, receipts, and rendered capture
against a separately running daemon.
