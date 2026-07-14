# Aetheria released-package witness

The warm-cache witness passes against `org.gamecult.eve.unity-scene` `0.3.17`
at commit `979eee3f8e327c904d25d2d3091a9a6db977842d` and
`org.gamecult.cultlib` `1.0.12` at commit
`26b4d1eadc4d0b1fe7a5ffb29a64373576544447`.

Evidence is in `artifacts/render-channel-witness-content-warm`:

- `results.xml`: one passing PlayMode witness.
- `aetheria-daemon-world.png`: provider-authored 3D pilot view.
- `aetheria-daemon-map.png`: map-channel-only view.
- `witness-facts.json`: movement, targeting, action, receipts, and camera-mask facts.
- `runtime-witness.warm.json`: released package commits and screenshot hashes.

The warm cache was seeded from the previously hash-verified bundle and renamed
to CultMesh's content-addressed `.body` cache shape. This proves released-client
loading and lowering, not cold delivery.

The cold CDN path remains failed. The 2026-07-14 run in
`artifacts/render-channel-witness-content-cold` used the managed
`cultmesh.content.v1` session and created the resumable 13,006,384-byte partial
body, but transferred only about 5.3 MB before the fixed 300-second witness
deadline. Snapshot chunk batching is no longer involved. The remaining fault is
the sequential one-chunk request path over RUDP; increasing the witness timeout
is not an accepted fix.
