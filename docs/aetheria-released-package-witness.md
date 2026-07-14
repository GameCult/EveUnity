# Aetheria released-package witness

The warm-cache witness passes against `org.gamecult.eve.unity-scene` `0.3.18`
at commit `ed21479e467faea8ff624f2d2f5a7d2fecf913f4` and
`org.gamecult.cultlib` `1.0.13` at commit
`feb5c71513e71d681699f462fe3682b3168c6f73`.

Evidence is in `artifacts/render-channel-witness-content-drain-cold-2`:

- `results.xml`: one passing PlayMode witness.
- `aetheria-daemon-world.png`: provider-authored 3D pilot view.
- `aetheria-daemon-map.png`: map-channel-only view.
- `witness-facts.json`: movement, targeting, action, receipts, and camera-mask facts.
- `runtime-witness.warm.json`: released package commits and screenshot hashes.

The warm run reused the body promoted by the preceding cold content transfer.
It reconciled movement, explicit targeting, and action receipts. The pilot
camera excluded all seven renderers assigned to the provider's `map` channel;
the map-only camera rendered 2,664 changed glyph pixels. Visual inspection
confirms provider-authored ships in the pilot view without map glyphs, and only
the colored provider-authored glyphs in the map view.

Cold content delivery now succeeds through the managed `cultmesh.content.v1`
session under the unchanged 300-second deadline. The run in
`artifacts/render-channel-witness-content-drain-cold-3` began with an empty
asset cache and atomically promoted:

- path: `asset-cache/0d5b575a18ecb2e8f4f1c309a7fa009262bdbb2ff671c14a4bf610d5052685dd.body`
- bytes: `13,006,384`
- SHA-256: `0d5b575a18ecb2e8f4f1c309a7fa009262bdbb2ff671c14a4bf610d5052685dd`
- partial files after completion: zero

The full cold gameplay witness is not yet a pass. During the roughly 19 seconds
of authoritative simulation needed to deliver and load the body, the witness
world can advance until its initial non-player targeting candidates are gone.
The released client then correctly reports a generation containing no explicit
target and the witness stops before screenshots. The warm run proves the
generic gameplay and camera contracts; the cold run proves content delivery.
Closing the combined cold proof requires an explicit readiness/lifecycle
contract or a stable witness-world scenario. A renderer fallback or a larger
timeout would conceal the ownership problem and is not accepted.
