# Aetheria released-package witness

## Current result

The warm-cache released-package witness passes with:

- `org.gamecult.eve.unity-scene` `0.3.39`, commit
  `35ff288aabc03130a61228579f1b09bac4345b5c`;
- `org.gamecult.eve.plugin-fields` `0.2.0`, commit
  `382a23d8f8a07e4b5eef5f81f84655861a858367`;
- `org.gamecult.eve.surface` `0.2.2`, commit
  `140e1bd963a0033e66777a3b2c5fe6e9c97dfe32`;
- `org.gamecult.eve.unity-uitoolkit` `0.1.1`, commit
  `4d0cbe0185bdc4fc65eb63503a7c5cb578539669`;
- `org.gamecult.cultlib` `1.0.13`, commit
  `feb5c71513e71d681699f462fe3682b3168c6f73`;
- the generic `ReleaseConsumerProject` client connected directly to the
  Aetheria daemon.

Evidence is in `artifacts/aetheria-daemon-gravity-fog-forward`. The PlayMode test
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
field of view, target screen position `0.64,0.81`, zero position damping, and
`1`-`4096` clip planes. The generic camera rig lowers those values without
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
four layers, and composited `22` pilot-camera frames from daemon frame `226`.
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
- pilot changed pixels: `230,400`;
- pilot average luminance: `0.1889651`;
- pilot bright pixels: `13,302`;
- map-channel renderers: `11`;
- map changed pixels: `4,213`;
- native cockpit progress bars: `7`;
- daemon tractor power at capture: `1.0` after the held input completed its
  authored ramp;
- daemon tractor power after the advertised release: `0`;
- provider tractor particle systems: `1`;
- pickup entities before/after collection: `1` / `0`;
- provider `pickup.collected` events: exactly `1`;
- collected item and quantity: `scrap-metal`, `1`;
- authoritative cargo quantity before/after: `0` / `1`;
- the pilot camera excludes the advertised map layer;
- the player prefab's embedded layer-14 map icon contributes exactly `0` pilot
  pixels;
- the map camera renders exactly the advertised map layer.

Visual inspection confirms that map glyphs are absent from the pilot frame and
present in the map-only frame. Correcting the fossil camera mode removed the
false top-down dark disk, but the active volume now resolves as a nearly uniform
saturated orange horizon rather than the fossil's blue gravity-shaped fog sea.
This is transport and lowering proof, not visual parity. The first concrete ABI
divergence is that the fossil derives dither sampling coordinates from the
render target and dither texture dimensions while the current surface supplies
zeroes. That lifecycle input must be lowered generically; EveUnity must not
compensate with Aetheria-specific lighting or fog rules.
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
The same released run begins with one provider-owned pickup in the entity SoA.
Held tractor input produces a Ymir Begin contact fact; the daemon consumes that
fact once, removes the pickup once, changes cargo from `0` to `1`, and emits one
`pickup.collected` feedback event carrying the exact delta. Aetheria's Ymir body
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

## Cold delivery is not currently proven

The current `46,412,252` byte Unity bundle (SHA-256
`bc45d84b54666e813bfccca12496ba4a3533ed08896dfc57de726749574fa796`)
times out from an empty cache. Its bytes are
still transported through batched snapshot records rather than the intended
mapped/network body transport. Increasing the Unity timeout would hide the
transport fault and is not an accepted fix.

A cold witness may be claimed only when an empty cache receives the bundle over
the intended body transport, atomically promotes the hash-addressed `.body`,
leaves no `.partial` file, and completes the same released-client assertions.

`artifacts/render-channel-witness-gravity-cold-4` records a historical 0.3.18
cold run. It remains useful regression evidence, but it does not describe the
health of the current transport path.
