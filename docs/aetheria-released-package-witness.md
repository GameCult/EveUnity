# Aetheria released-package witness

## Current result

The released-package witness passes in separate cold-lowering and warm-gameplay
profiles with:

- `org.gamecult.eve.unity-scene` `0.3.51`, commit
  `21ac39e7bdfca9d81a0b7d89834f7e9da60aaad6`;
- `org.gamecult.eve.plugin-fields` `0.2.3`, commit
  `c5a4a75c1b727499b16c2dae1895f29e2a9f72f0`;
- `org.gamecult.eve.surface` `0.2.2`, commit
  `140e1bd963a0033e66777a3b2c5fe6e9c97dfe32`;
- `org.gamecult.eve.unity-uitoolkit` `0.1.1`, commit
  `4d0cbe0185bdc4fc65eb63503a7c5cb578539669`;
- `org.gamecult.cultlib` `1.0.14`, commit
  `4b7162022a8976f7941b5a7a69acf50f1b6d532b`;
- the generic `ReleaseConsumerProject` client connected directly to the
  Aetheria daemon.

Current celestial-package evidence is in
`artifacts/aetheria-daemon-celestial-cold`; the retained cold and warm witness
documents distinguish transfer from gameplay. The warm PlayMode test
passed and recorded provider-owned reconciled movement, look, targeting,
tractor press, tractor release, contact-gated cargo collection, and action
receipts. The daemon-owned look direction reached the SoA body rotation,
and the generic aim presentation rendered at the advertised 50-unit minimum
convergence distance. The advertised skybox material and reflection cubemap
resolved from the provider bundle as their required native types and became the
active camera rig's leased Unity environment.
The witness supplies no client-authored light. Aetheria advertises the key-light
direction, color, and intensity; the generic camera rig lowers that contract to
the only live directional light (`0.75` intensity).
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
four layers, and composited `91` pilot-camera frames through daemon frame `643`.
The provider variant advertises the fossil shader's raymarch, temporal-history,
and composite passes plus their logical ports. EveUnity allocates and resets the
history targets generically; a partial temporal ABI fails closed.

The two PNGs below are deliberate camera-channel captures, not a composited
screen capture, so they prove world/map isolation rather than HUD pixels. The
native cockpit proof is the asserted UI Toolkit visual tree recorded as
`cockpitProgressCount: 7` in the witness facts. The same run records completed
daemon lock progress `1.0`; Unity does not manufacture or smooth that value.

Camera-channel facts:

- provider-authored player renderers: `13`, including the lowered tractor effect;
- pilot changed pixels: `225,548`;
- pilot average luminance: `0.119513`;
- pilot bright pixels: `97,617`;
- map-channel renderers: `10`;
- map changed pixels: `4,723`;
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
Aetheria or celestial branch; release `0.3.51` fixes the generic prefab wrapper
so semantic instance scale composes with, rather than overwrites, provider-local
scale.

Visual inspection confirms that map glyphs are absent from the pilot frame and
present in the map-only frame. This is transport, authority, field-production,
and lowering proof, not visual parity: the current pilot capture contains the
blue fog field and provider geometry, but nearby ships remain too dark and the
celestial composition lacks the fossil capture's readable close body. Release
`0.3.51` includes generic lowering for Eve Fields `0.2.3`'s
`AnimatedRadialCosine` source alongside `PowerPulse`, dither-scale,
temporal-history, and finite-look-at semantics. Aetheria publishes the fossil
global animated simplex/cellular/ambient producer stack, body-owned radial wave
frequency, separate negative `gravity.height` and positive
`fog.surface_height`, and the authored `ARPG.unity` zone-brush extent. The
witness records player Y `-76.7574` and camera Y `-94.3347`; Unity does not
sample terrain to invent either value. The four rasterized field layers and HDR
raymarch/history targets are populated, so the remaining failure is in native
presentation/composition or provider art calibration rather than missing state
delivery. Color, lighting, patch structure, readable world geometry, and
gravity-wave visual parity remain open. EveUnity must not compensate with
Aetheria-specific lighting or fog rules.
Substance is not part of this path; later texture baking belongs in Blender.
Aetheria currently bundles ambientCG's 1K `Metal012` color, normal, and
metalness maps under CC0. The same pre-generated maps now replace dead
Substance archive sub-assets on the brushed aluminium, tinted car paint,
cockpit, steel, black-metal, and radiator materials. The released witness
resolves textured native URP materials in `10` distinct player-material slots,
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
The warm gameplay run begins with no pickup. A daemon-authoritative shot destroys
the deterministic salvage target and creates one provider-owned pickup carrying
a canonical generated cargo item. Held tractor input produces a Ymir Begin
contact fact; the daemon consumes that fact once, removes the pickup once,
changes cargo from `0` to `1`, and emits one `pickup.collected` feedback event
whose identity begins with `ymir-fact:`. Aetheria's Ymir body
mapping excludes stations and non-ship world bodies from pickup collision facts,
so ambient station contact cannot become a cargo writer. Capacity rejection and
its `cargo-capacity` reason are covered by the daemon smoke, not claimed as a
released-client live scenario here.
Effect tuning remains provider art work; the runtime must not compensate with
an Aetheria-specific beam.

Primary artifacts:

- `results.xml`: one passing PlayMode witness;
- `runtime-witness.json`: package, content, receipt, aim, combat, and
  camera facts;
- `aetheria-daemon-world.png`: pilot-camera capture;
- `aetheria-daemon-map.png`: map-only capture.

## Cold delivery proof

The `cold-start-lowering` profile starts with zero bodies and zero partials. It
receives the `55,529,775` byte Unity bundle through the managed
`cultmesh.content.v1` session, verifies SHA-256
`5cb34be9b29f533ddfea89a439f43170c3691538b2dba9578d1f96ff91cc0b34`,
atomically promotes one `.body`, and leaves zero partials under the unchanged
300-second deadline. Including a clean provider-bundle build, the full witness
took `263.477` seconds; the Unity test took `48.083` seconds. It then lowered
provider assets, movement, environment, four field layers across `125`
composites, and both camera channels. The pilot camera
excluded map objects and the map camera included them.

Cold asset acquisition and transient gameplay are deliberately separate proof
profiles. Starting Unity before the provider means download time is not a
stable lifetime for a boot-seeded enemy or pickup. The cold profile proves
delivery and lowering; `full-session-gameplay` starts a fresh warm authoritative
session and proves combat, destruction-created loot, Ymir contact collection,
and exactly-once receipts. Neither profile claims negotiated mapped/zero-copy
CDN delivery: the released path is the managed network content session and
still copies/fragments bytes in process.
