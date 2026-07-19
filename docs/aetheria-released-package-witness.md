# Aetheria released-package witness

## Current integration gate

The generic `ReleaseConsumerProject` is pinned to released Git packages:

- `org.gamecult.eve.unity-scene` `0.3.91`, commit
  `c93f09342f4c14e0607a47038df6b8e2e99cbf03`, and
  `org.gamecult.eve.surface` `0.2.4`, commit
  `e08fa08335f99e9edddeb706912eecfad07cb281`;
- `org.gamecult.eve.plugin-fields` `0.2.3`, commit
  `c5a4a75c1b727499b16c2dae1895f29e2a9f72f0`;
- `org.gamecult.eve.unity-uitoolkit` `0.1.1`, commit
  `4d0cbe0185bdc4fc65eb63503a7c5cb578539669`;
- `org.gamecult.cultlib` `1.0.32`, commit
  `d018bb1fb5a8ce73da6f5579e2f475750d901d8b`.

The hand-run integration gate is the warm released-package witness:

```powershell
pwsh -File .\scripts\run-aetheria-daemon-world-witness.ps1 `
  -CacheState warm `
  -AssetCacheDirectory E:\Projects\Aetheria\Aetheria.Unity\Build\AssetCache `
  -OutputDirectory artifacts\dockyard-trade-warm-8 `
  -SkipAssetBundleBuild
```

It connects directly to the Aetheria daemon and must produce one passing
PlayMode result, a pilot capture, a map-only capture, and typed witness facts.
The current assertions require provider-owned assets, movement, camera-channel
isolation, combat, destruction-created loot, daemon-owned XZ proximity
collection or capacity refusal, held tractor input, and authoritative receipts.
They do not require or accept Ymir contact identity as the owner of looting.
The explicit prime flag rebuilds the provider bundle and installs that exact
content-hashed body into the local warm cache. This is convenient local setup,
not CDN-transfer evidence. On later runs, use `-SkipAssetBundleBuild` and omit
the prime flag when neither provider assets nor their catalog changed.

Cold transfer is a passing released-package integration gate as of
`artifacts/cold-release-0391`. The run starts with zero bodies and zero partials,
transfers and verifies the `56,204,750`-byte provider bundle through the
dedicated `cultmesh.content.v1` session, atomically promotes one content-hashed
body with no partial left behind, and grants Unity a `SharedFileMapping` lease
over that verified file. The Unity test completes in 54.8 seconds and the full
clean import/build/test wrapper completes in 149.6 seconds under the unchanged
300-second deadline. Pilot/map channel separation and 65,536 Stardust particles
remain proven after the cold load.

This proves that bulk asset bytes no longer depend on snapshot records. It does
not claim remote zero-copy: the current remote content session still copies and
fragments bounded chunks before the verified local file becomes mapped. Network
body streaming and copy-volume profiling remain transport work.

The July 19 released-package run in `artifacts/dockyard-trade-warm-8` passes the
full-session gate. The generic client lowers the provider-owned bundle through
`SharedFileMapping`, docks at the daemon-authored 12-berth Zenith Dockyard,
buys and sells one typed item, undocks, moves, targets, aims, runs a held tractor
beam, destroys the sole proof hostile, and collects the resulting loot exactly
once through daemon-owned proximity. All eight retained witness receipts are
provider-owned and reconciled. The run records 65,536 Stardust particles, 147
field composites/dispatches/draws, and both camera-channel invariants. The pilot
capture contains the 3D fog/Stardust/ship frame and no map icons; the map capture
contains the map icons against its map background. The pilot image remains
visually overexposed and is integration evidence, not final art approval.

The July 19 transport-boundary run in `artifacts/demanded-fields-warm-6`
passed its released-consumer PlayMode test and produced both camera captures.
The provider published the requested Fields viewport only after CultMesh
observed the client's exact record/schema demand. Entity state used the
same-machine mapped SoA body, while the one-second Eve surface remained a
separate reactive control/presentation stream. The facts record 65,536
Stardust particles, 145 field composites and draws, pilot/map camera isolation,
movement, combat, exactly-once destruction-loot collection, tractor activation,
and a reconciled held-input release. Terminus attention had paused simulation,
so the released target latch coexists honestly with frozen ramped beam power
`0.84`; the generic client does not manufacture a visual zero.

The wrapper currently rejects this otherwise passing run after Unity exits
because its full-session gate requires docked purchase/sale fields that the
generic capture test does not yet emit. Consequently this run is transport and
gameplay evidence, not a completed full-session readiness witness, and no
`runtime-witness.json` was issued.

## Historical witness ledger

An earlier released-package cold world witness passed with:

- `org.gamecult.eve.unity-scene` `0.3.63`, commit
  `3135d24bb84f40719b121b51634811c85af0a7af`;
- `org.gamecult.eve.plugin-fields` `0.2.3`, commit
  `c5a4a75c1b727499b16c2dae1895f29e2a9f72f0`;
- `org.gamecult.eve.surface` `0.2.2`, commit
  `140e1bd963a0033e66777a3b2c5fe6e9c97dfe32`;
- `org.gamecult.eve.unity-uitoolkit` `0.1.1`, commit
  `4d0cbe0185bdc4fc65eb63503a7c5cb578539669`;
- `org.gamecult.cultlib` `1.0.15`, commit
  `419053ebe2325848051c4f4d8ba352cd4286c424`;
- the generic `ReleaseConsumerProject` client connected directly to the
  Aetheria daemon.

The cold `0.3.59` witness in
`artifacts/aetheria-daemon-adaptive-exposure-cold` resolves the tagged package,
transfers and lowers the 56,170,916-byte provider bundle
`34dd156038c9c55b96136e10805f35f2bc7f2aa4e8dda8a519e57aa5a83329c8`, and
passes in 53.3 seconds. EveUnity lowers Aetheria's advertised historical
histogram exposure as a provider-agnostic camera semantic; the bundled static
volume profile remains neutral and is no longer a competing exposure owner.
Average pilot-frame luminance fell from `0.8716` in the restored-scattering
camera witness to `0.5096`, recovering visible cloud structure. The capture is
still cyan and visibly posterized, so temporal/color parity remains open.
Map-channel isolation also passes: the pilot camera excludes the 11 map
renderers and the map camera includes them.

The subsequent released `0.3.61` warm witness in
`artifacts/aetheria-daemon-temporal-bootstrap-warm` restores two temporal
details from the fossil renderer as explicit provider-program semantics. The
fog history projects the previous view through the current projection, and the
first history frame uses the provider-advertised `ultra` quality before
settling to the scene's serialized `high` quality. Average luminance remains
effectively unchanged while mean neighboring-pixel variation falls from
`0.0332` to `0.0174` and Laplacian high-frequency energy falls from `0.1050`
to `0.0531`. This proves the temporal history is accumulating and that its
bootstrap seed mattered. The remaining cyan/tonemap mismatch is still open.

The cold HD witness in `artifacts/aetheria-daemon-fog-alpha-cold-hd` then
transfers the rebuilt 56,168,970-byte provider bundle
`a829e57731fbe08f4564fa131468148e99f6b4204708ad3e64498885532b5091`
through `SharedFileMapping` and captures after 128 temporal composites at
1280x720. It restores the historical fog history ABI: the temporal pass stores
density directly in alpha, so the composite consumes that density directly
instead of decoding it as the raymarch pass's packed distance/density payload.
The flat grey opacity slabs disappear. The released client test passes in 52.7
seconds with 238 fog composites and Stardust draws; the pilot camera still
excludes map renderers and the map camera includes them. Remaining visual work
is color/composition parity, not broken fog opacity or an unsettled witness.

The released `0.3.62` warm HD witness in
`artifacts/aetheria-daemon-hdr-grading-warm-hd` lowers the provider's
`hdr-before-tonemap.v1` grading-space contract. This restores the fossil
profile's HDR ordering—grading before ACES—rather than inheriting the released
consumer project's default LDR grading after tonemapping. EveUnity restores the
consumer's prior pipeline mode when the camera is released and fails closed
when the requested grading space cannot be lowered.

The asserted rerun in `artifacts/aetheria-daemon-hdr-grading-asserted-warm-hd`
also completes the live destruction-loot path on the same released client. One
daemon destruction produces the canonical pickup; tractor power reaches `1`
and returns to `0`; one Ymir `begin` fact produces exactly one
`pickup.collected` event; cargo changes from `0` to `1`; and no client pickup
command participates in the transaction. The event identity begins
`ymir-fact:` and contains the retained Box3D session, step, contact episode,
and begin-fact identity.

The released `0.3.63` warm HD witness in
`artifacts/aetheria-daemon-temporal-projection-0363-warm-hd` makes the native
volume history projection convention an explicit provider-program semantic.
Aetheria advertises the non-render-target projection convention used by its
historical `Graphics.Blit` temporal pass; the generic URP lowerer applies that
convention without naming Aetheria or interpreting fog. The witness captures
the naturally rendered camera target after 359 volume composites instead of
issuing a second out-of-band `Camera.Render()`. Package tests pass 105/105 and
the released gameplay witness still proves movement, combat, destruction loot,
one Ymir contact collection, held tractor input, and camera-channel isolation.
The remaining visual controls (fog parameters, stellar tint, ambient intensity,
histogram exposure, grading, and temporal settings) remain provider-owned
levers; this proof does not claim a final art grade.

The released `0.3.63` warm rejection witness in
`artifacts/aetheria-daemon-cargo-rejection-0363-warm` selects the daemon-owned
`cargo-capacity-rejection-proof` scenario. The controlled ship begins with all
generated cargo bays filled from typed item volume and bay capacity. Combat
creates the salvage pickup normally; player Ymir Begin contacts emit
`pickup.rejected` with `cargo-capacity`, preserve cargo at `5` items, and leave
the pickup live. The final run observed two later player recontacts with two
distinct `ymir-fact:` identities and no player collection event. The generic
witness correlates feedback through advertised SoA `SourceIndex` and feedback
`TargetEntityIndex`; events from other ships are not attributed to the pilot.

Current released-package evidence is in
`artifacts/aetheria-daemon-mapped-body-cold`; despite that retained directory
name, `runtime-witness.warm.json` is the current passing run. The warm PlayMode test
passed and recorded provider-owned reconciled movement, look, targeting,
tractor press, tractor release, contact-gated cargo collection, and action
receipts. The fingerprinted April 2021 catalog recovery now restores the
distinct common `Longinus`, rare `LonginusX`, and `Djinni` rows plus the missing
turn-thruster equipment needed to outfit ordinary hulls. This run generated the
player as a common directional-thruster `Longinus`; the other six presented
ships were `Djinni`, and no `LonginusX` appeared. The player moved 14.34 world
units and completed its authoritative hardware-driven look before combat.
The daemon-owned look direction reached the SoA body rotation,
and the generic aim presentation rendered at the advertised 50-unit minimum
convergence distance. Aetheria derives ambient color from daemon-owned stellar
`LightColor` values and advertises `studio2` only as the reflection cubemap; the
gravity-fog raymarch owns the visible frame. The released lowerer records flat
ambient mode, color `(1.46,1.2556,0.8468)`, ambient-probe energy `4.6354`, and
custom reflection texture `studio2`. No skybox asset or key light is advertised,
and the generic camera rig records zero live directional intensity. The earlier
invented `0.75` light and false skybox authority were deleted rather than retained
as material-conversion compensators.
Aetheria also advertises the original undocked `ARPG.unity` Third Person Rig:
entity-forward perspective follow, `30` unit distance, `60` degree vertical
field of view, canonical bottom-origin target screen position `0.64,0.19`, zero position damping, and
`1`-`4096` clip planes. Its generic `aim.convergence-point.v1` relation makes
the optical axis reach the daemon-published aim point while preserving that
entity framing. The generic camera rig lowers both constraints without
Aetheria-specific camera code. The distinct `70` unit Docked Rig no longer
incorrectly owns the flight view.
Aetheria's pilot surface also publishes a transparent cockpit overlay. The
released generic UI Toolkit lowerer produced seven native `ProgressBar` elements
for hull, shield, capacitor, weapon cooldown, target lock, and target state. Daemon frame,
player, and command diagnostics remain available in the document but lower
with `display: none` in the pilot presentation. The runnable generic-client
launcher mounts the same advertised document and forwards its typed commands;
it contains no Aetheria gameplay or client-authored world light.

The same surface publishes a `field.volume3d` node over four canonical Eve
Fields splat layers. Its surface contract contains logical ports such as
`surfaceHeight`, `patch`, and `tint`; it contains no Unity `_Nebula*` property
names or pass indices. The selected `unity-scene` provider asset variant owns
that concrete shader ABI through `unity.volume.*` metadata. The released
generic lowerer resolved the provider shader and dither texture, rasterized all
 four layers, and composited `195` pilot-camera frames through daemon frame `491`.
The provider variant advertises the fossil shader's raymarch, temporal-history,
and composite passes plus their logical ports. EveUnity allocates and resets the
history targets generically; a partial temporal ABI fails closed.

The two PNGs below are deliberate camera-channel captures, not a composited
screen capture, so they prove world/map isolation rather than HUD pixels. The
native cockpit proof is the asserted UI Toolkit visual tree recorded as
`cockpitProgressCount: 7` in the witness facts. The same run records completed
daemon lock progress `1.0`; Unity does not manufacture or smooth that value.

Camera-channel facts:

- provider-authored player renderers: `7` active, with no embedded shield renderer;
- pilot changed pixels: `217,323`;
- pilot average luminance: `0.1322155`;
- pilot bright pixels: `101,510`;
- map-channel renderers: `10`;
- map changed pixels: `3,179`;
- native cockpit progress bars: `7`;
- daemon tractor power at capture: `1.0` after the held input completed its
  authored ramp;
- daemon tractor power after the advertised release: `0`;
- provider tractor particle systems: `1`;
- pickup entities before/after destruction and collection: `0` / `0`;
- provider `pickup.collected` events: exactly `1`;
- collected item and quantity: one canonical `aetheria.item_definition:*` key,
  quantity `1`;
- authoritative cargo quantity before/after: `0` / `1`;
- the pilot camera excludes the advertised map layer;
- the player prefab's embedded layer-14 map icon contributes exactly `0` pilot
  pixels;
- the map camera renders exactly the advertised map layer.

The daemon also publishes the current zone's celestial presentation through the
same generic entity SoA generation as ordinary world entities. The current warm
witness records eleven sun/planet/gas-giant rows and eighteen asteroid rows.
Every row resolved a provider asset with enabled native renderers, and seven
celestial instances intersected the pilot frustum. These identities are
presentation-only and cannot receive gameplay commands. EveUnity contains no
Aetheria or celestial branch; release `0.3.55` includes the generic prefab wrapper
so semantic instance scale composes with, rather than overwrites, provider-local
scale.

Provider prefab construction now removes the fossil shield renderer embedded in
each ship. That renderer's custom shader performs collision-local dither clipping;
flattening it to ordinary opaque URP Lit had produced the large black ellipsoids
previously mistaken for celestial bodies. Shield state remains daemon truth and
shield impacts use the separately advertised receipt-driven effect. Material
conversion also preserves source lighting models: the map icon's unlit source
now becomes `Universal Render Pipeline/Unlit`, so map glyphs remain readable
without resurrecting the false scene light.

Visual inspection confirms that map glyphs are absent from the pilot frame and
present in the map-only frame. This is transport, authority, field-production,
and lowering proof, not visual parity: the current pilot capture contains the
blue fog field and the provider's magenta-and-gold Longinus hull with no opaque
shield ellipsoid. The celestial composition still lacks the fossil capture's
readable close body. Release
`0.3.57` includes generic lowering for Eve Fields `0.2.3`'s
`AnimatedRadialCosine` source alongside `PowerPulse`, dither-scale,
temporal-history, and finite-look-at semantics. Aetheria publishes the fossil
global animated simplex/cellular/ambient producer stack, body-owned radial wave
frequency, separate negative `gravity.height` and positive
`fog.surface_height`, and the authored `ARPG.unity` zone-brush extent. The
  witness records player Y `-101.5035` and camera Y `-84.7369`; Unity does not
sample terrain to invent either value. The four rasterized field layers and HDR
raymarch/history targets are populated, so the remaining failure is in native
presentation/composition or provider art calibration rather than missing state
delivery. Color, lighting, patch structure, readable world geometry, and
gravity-wave visual parity remain open. EveUnity must not compensate with
Aetheria-specific lighting or fog rules.

The `0.3.57` run also closes two camera-contract omissions. Aetheria advertises
the fossil pilot camera's neutral temporal-reprojection values; the released
generic lowerer applies URP TAA with `0.99` history blend, `0.1` jitter scale,
high quality, and zero sharpening. Fog and Stardust now consume one generic
camera-relative viewport-frame resolver rather than projecting the same splat
document independently. The live witness fails unless their snapped grid
centers are identical; the latest run resolved both to `(-6,-6)` after movement.
The provider HLSL now consumes Eve Fields' positive world-axis viewport directly;
it no longer retains the two-axis compensation once required by the deleted
fossil gravity camera. An asymmetric GPU fixture fails unless positive-world X
samples positive texture X, while the existing one-cell remap proof still keeps
705 overlapping world particles bit-identical.
The capture is still visibly rough and is not a visual-parity claim.

The fossil Stardust material used a screen-space coverage mask that changes on
every rendered frame and depends on TAA to reconstruct stable subpixel stars.
The earlier URP port had replaced that with a fixed alpha threshold and
additive blending. Aetheria now advertises the provider dither texture,
viewport scale, and logical render-frame port; generic EveUnity binds them
without knowing their product meaning. The live witness resolves those native
ports from provider metadata and checks the actual material instance. The
provider bundle source gate requires the temporal clip, overwrite blend, and
depth write and rejects `_AlphaClip`.

Substance is not part of this path; later texture baking belongs in Blender.
Aetheria currently bundles ambientCG's 1K `Metal012` color, normal, and
metalness maps under CC0. The same pre-generated maps now replace dead
Substance archive sub-assets on the brushed aluminium, tinted car paint,
cockpit, steel, black-metal, and radiator materials. The released witness
  resolves textured native URP materials in `8` distinct player-material slots,
up from `3`, without adding another texture payload. This is temporary provider
art with recorded provenance, not a replacement texture pipeline.

The tractor proof is structural, not yet a native-look victory lap. The daemon
publishes `beam.presentation` and advertises `pilot.scoop` as a
`button-hold.v1` value action. The generic client routes scalar `1` on press and
scalar `0` on release through the advertised `SetTractorPower` operation. Both
commands receive provider-owned reconciled receipts, and the daemon ramps the
published power from `1.0` at capture to exactly `0` after release. The provider
build removes the fossil's embedded tractor object from player/ship prefabs, so
the standalone `beam.presentation` prefab is the only tractor renderer. Its
cyan/yellow dotted band is visible in the pilot capture and absent from the map
capture.
The current `0.3.72` warm gameplay run is retained in
`artifacts/aetheria-daemon-0372-warm5`. It begins with no pickup. A
daemon-authoritative shot destroys the deterministic salvage target and creates
one provider-owned pickup carrying a canonical generated cargo item. Held
tractor input causes Ymir-owned attraction; collection itself is an
authoritative XZ proximity transaction and does not depend on a contact fact.
The daemon removes the pickup once, changes cargo from `0` to `1`, and emits one
stable `zone:0:pickup:0:collected` feedback event. No client pickup command or
Ymir contact receipt owns cargo mutation.

The same run records 709 URP field-volume composites, 65,536 stateless field
particles, and 11 map-channel renderers. The pilot camera excludes every map
renderer and the map camera includes them. The serialized consumer renderer now
contains `EveUnityRendererFeature`; the configuration utility marks
`UniversalRendererData` dirty before saving so batch and editor cameras use the
same URP scheduling path. The captures are integration evidence, not a final art
grade: the pilot frame remains overexposed and the hull reads nearly black.
Effect tuning remains provider art work; the runtime must not compensate with
an Aetheria-specific beam.

Primary artifacts:

- `results.xml`: one passing PlayMode witness;
- `runtime-witness.json`: package, content, receipt, aim, combat, and
  camera facts;
- `aetheria-daemon-world.png`: pilot-camera capture;
- `aetheria-daemon-map.png`: map-only capture.

## Cold delivery status

Cold CDN transfer is part of the passing readiness gate. The released `0.3.91`
run in `artifacts/cold-release-0391` began with zero cached bodies and partials,
received only the typed manifest through snapshot state, transferred bounded
content chunks through the managed CultMesh content session, and atomically
promoted one `56,204,750`-byte body named by SHA-256
`f2c8acf938998703799e909cbf0c6e3ed9ad5d6f83b708a0b7b6e76ee33f7160`.
It left zero partials and selected `SharedFileMapping` over the verified file.

The cold-only failure exposed by `0.3.90` was a bootstrap ownership race, not a
bulk snapshot transfer: live SoA subscriptions began before the blocking cold
asset acquisition, so their retained capability expired before Unity pumped it.
`0.3.91` resolves the surface first, completes provider-asset acquisition, and
only then starts live subscriptions. A fresh current generation therefore owns
the first lowered frame. No retry timer or stale-body reinterpretation exists.

This proves cold acquisition and local mapped reuse, not provider-to-client
network zero-copy, shared-memory delivery, or GPU zero-copy. Managed remote
chunks are still copied and fragmented before the transfer owner commits the
mapped file; network-body streaming and copy-volume profiling remain open.
