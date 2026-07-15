# Aetheria released-package witness

## Current result

The warm-cache released-package witness passes with:

- `org.gamecult.eve.unity-scene` `0.3.31`, commit
  `140e1bd963a0033e66777a3b2c5fe6e9c97dfe32`;
- `org.gamecult.eve.surface` `0.2.2`, from the same commit;
- `org.gamecult.eve.unity-uitoolkit` `0.1.1`, commit
  `4d0cbe0185bdc4fc65eb63503a7c5cb578539669`;
- `org.gamecult.cultlib` `1.0.13`, commit
  `feb5c71513e71d681699f462fe3682b3168c6f73`;
- the generic `ReleaseConsumerProject` client connected directly to the
  Aetheria daemon.

Evidence is in `artifacts/aetheria-daemon-native-0326-warm`. The PlayMode test
passed and recorded provider-owned reconciled movement, look, targeting,
tractor press, tractor release, and action receipts. The daemon-owned look direction reached the SoA body rotation,
and the generic aim presentation rendered at the advertised 50-unit minimum
convergence distance. The advertised skybox material and reflection cubemap
resolved from the provider bundle as their required native types and became the
active camera rig's leased Unity environment.
The witness supplies no client-authored light. Aetheria advertises the key-light
direction, color, and intensity; the generic camera rig lowers that contract to
the only live directional light (`0.75` intensity).
Aetheria also advertises the original `ARPG.unity` follow-camera composition:
`70` unit distance, `60` degree vertical field of view, target screen position
`0.66,0.55`, and position damping `2`. The generic camera rig lowers those
values without Aetheria-specific camera code.
Aetheria's pilot surface also publishes a transparent cockpit overlay. The
released generic UI Toolkit lowerer produced seven native `ProgressBar` elements
for hull, shield, capacitor, weapon cooldown, target lock, and target state. Daemon frame,
player, and command diagnostics remain available in the document but lower
with `display: none` in the pilot presentation. The runnable generic-client
launcher mounts the same advertised document and forwards its typed commands;
it contains no Aetheria gameplay or client-authored world light.

The two PNGs below are deliberate camera-channel captures, not a composited
screen capture, so they prove world/map isolation rather than HUD pixels. The
native cockpit proof is the asserted UI Toolkit visual tree recorded as
`cockpitProgressCount: 7` in the witness facts. The same run records completed
daemon lock progress `1.0`; Unity does not manufacture or smooth that value.

Camera-channel facts:

- provider-authored player renderers: `13`, including the lowered tractor effect;
- pilot changed pixels: `230,105`;
- pilot average luminance: `0.004590`;
- pilot bright pixels: `1,620`;
- map-channel renderers: `11`;
- map changed pixels: `5,187`;
- native cockpit progress bars: `7`;
- daemon tractor power at capture: `1.0` after the held input completed its
  authored ramp;
- daemon tractor power after the advertised release: `0`;
- provider tractor particle systems: `1`;
- the pilot camera excludes the advertised map layer;
- the player prefab's embedded layer-14 map icon contributes exactly `0` pilot
  pixels;
- the map camera renders exactly the advertised map layer.

Visual inspection confirms that map glyphs are absent from the pilot frame and
present in the map-only frame. Restoring the authored ARPG camera in place of
the copied TestScene tuning increased hull-renderer pixel coverage by roughly
four times and bright pilot pixels from `502` to `1,319`. The pilot presentation
remains dark. Renderer-isolation facts prove that the hull draws under the
provider-owned light, so the next native-art work is available pre-generated
provider textures, not a client-side lighting fallback.
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
Effect tuning remains provider art work; the runtime must not compensate with
an Aetheria-specific beam.

Primary artifacts:

- `results.xml`: one passing PlayMode witness;
- `runtime-witness.warm.json`: package, content, receipt, aim, combat, and
  camera facts;
- `aetheria-daemon-world.png`: pilot-camera capture;
- `aetheria-daemon-map.png`: map-only capture.

## Cold delivery is not currently proven

The current `46,131,569` byte Unity bundle (SHA-256
`a4b26fb234ae7d82eda26fb342d7f77a236e3bc1107420175eb1c626785db212`)
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
