# Aetheria released-package witness

The warm-cache witness passes against `org.gamecult.eve.unity-scene` `0.3.15`
at commit `474cadac1c173e24b2c9b9ddde7b781059dbae4c`.

Evidence is in `artifacts/render-channel-witness-41`:

- `results.xml`: one passing PlayMode witness.
- `aetheria-daemon-world.png`: provider-authored 3D pilot view.
- `aetheria-daemon-map.png`: map-channel-only view.
- `witness-facts.json`: movement, targeting, action, receipts, and camera-mask facts.
- `runtime-witness.warm.json`: released package and screenshot hashes.

The cold CDN path is not proven. `artifacts/render-channel-witness-21/results.xml`
records a timeout while fetching the 13,006,384-byte bundle through batches of
32 `gamecult.mesh.cdn_artifact_chunk.v1` snapshot records. The bundle must move
through the intended mapped/network body transport; increasing the snapshot
timeout is not an accepted fix.
