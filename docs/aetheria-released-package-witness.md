# Aetheria released-package witness

## Current result

The warm-cache released-package witness passes with:

- `org.gamecult.eve.unity-scene` `0.3.25`, commit
  `58d87c628a261751439584263c18e93a7d8d8708`;
- `org.gamecult.cultlib` `1.0.13`, commit
  `feb5c71513e71d681699f462fe3682b3168c6f73`;
- the generic `ReleaseConsumerProject` client connected directly to the
  Aetheria daemon.

Evidence is in `artifacts/aetheria-daemon-native-0325-warm`. The PlayMode test
passed and recorded provider-owned reconciled movement, look, targeting, and
action receipts. The daemon-owned look direction reached the SoA body rotation,
and the generic aim presentation rendered at the advertised 50-unit minimum
convergence distance.

Camera-channel facts:

- provider-authored player renderers: `12`;
- pilot changed pixels: `4,967`;
- map-channel renderers: `10`;
- map changed pixels: `4,766`;
- the pilot camera excludes the advertised map layer;
- the map camera renders exactly the advertised map layer.

Visual inspection confirms that map glyphs are absent from the pilot frame and
present in the map-only frame. The current material fallback is visually dark;
this is authority, package, asset, command, and camera-channel evidence, not a
claim that the native art pass is complete.

Primary artifacts:

- `results.xml`: one passing PlayMode witness;
- `runtime-witness.warm.json`: package, content, receipt, aim, combat, and
  camera facts;
- `aetheria-daemon-world.png`: pilot-camera capture;
- `aetheria-daemon-map.png`: map-only capture.

## Cold delivery is not currently proven

The current 13 MB Unity bundle times out from an empty cache. Its bytes are
still transported through batched snapshot records rather than the intended
mapped/network body transport. Increasing the Unity timeout would hide the
transport fault and is not an accepted fix.

A cold witness may be claimed only when an empty cache receives the bundle over
the intended body transport, atomically promotes the hash-addressed `.body`,
leaves no `.partial` file, and completes the same released-client assertions.

`artifacts/render-channel-witness-gravity-cold-4` records a historical 0.3.18
cold run. It remains useful regression evidence, but it does not describe the
health of the current transport path.
