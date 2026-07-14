# Aetheria released-package witness

The combined cold-cache witness passes against the released generic runtime:

- `org.gamecult.eve.unity-scene` `0.3.18`, commit
  `ed21479e467faea8ff624f2d2f5a7d2fecf913f4`
- `org.gamecult.cultlib` `1.0.13`, commit
  `feb5c71513e71d681699f462fe3682b3168c6f73`

Evidence is in `artifacts/render-channel-witness-gravity-cold-4`. The run used
`ReleaseConsumerProject`, resolved both dependencies from tagged Git URLs, and
finished under the unchanged 300-second Unity limit.

The cache began with zero `.body` and `.partial` files. Managed
`cultmesh.content.v1` delivery atomically promoted exactly one body:

- path: `asset-cache/0d5b575a18ecb2e8f4f1c309a7fa009262bdbb2ff671c14a4bf610d5052685dd.body`
- bytes: `13,006,384`
- SHA-256: `0d5b575a18ecb2e8f4f1c309a7fa009262bdbb2ff671c14a4bf610d5052685dd`
- final partial files: zero

Snapshot delivery indexes the artifact manifest only. Aetheria's legacy
snapshot `asset_blob` schema, byte injection, path resolver, and Electron client
API are deleted; managed content sessions are the sole bundle-body transport.

The released generic client instantiated the provider-authored prefab rather
than its primitive fallback. Movement, explicit targeting, and action each
reached a provider-owned `reconciled` receipt with matching command,
provider/surface identity, receipt identity, owner, authority, and observed
source version.

Camera-channel facts:

- provider-authored player renderers: `12`
- pilot changed pixels: `6,265`
- map-channel renderers: `11`
- map changed pixels: `3,413`
- pilot camera excludes the advertised map layer
- map camera culls exactly the advertised map layer

Visual inspection confirms provider-authored ship geometry in the pilot view
without map glyphs, and only provider-authored glyphs in the map view.

Primary artifacts:

- `results.xml`: one passing PlayMode witness
- `runtime-witness.cold.json`: package, content, receipt, camera, and hash facts
- `aetheria-daemon-world.png`: fresh pilot-camera capture
- `aetheria-daemon-map.png`: fresh map-only capture
