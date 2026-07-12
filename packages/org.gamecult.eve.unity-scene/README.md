# Eve Unity Scene Lowering

Provider-agnostic Unity scene lowering for `gamecult.eve.surface.v1`
interactive worlds.

Providers implement the surface-document, asset-manifest, command-sink, and
receipt-source ports. `EveUnityPlayableWorldClientBootstrap` wires those ports
to the generic scene host, input driver, camera rig, and `GameObject` sink.

`EveUnityCultMeshPlayableWorldProvider` is the runtime composition root for a
networked client. Give it one Verse rendezvous endpoint and it discovers an
advertised `interactive-world`, constructs the generic CultMesh transport, and
provides every bootstrap port. Provider, surface, and Verse IDs are optional
selection filters; they are not product knowledge required by the client.

The package never imports provider product assemblies.

Retained provider feedback is lowered without becoming Unity authority.
`EveUnityFeedbackPresenter` emits each new `feedback.event` identity once, and
`EveUnityShotReceiptPresenter` does the same for `shot.receipt` trajectories.
The generic `EveUnityShotTrajectoryRenderer` renders provider-authored origin,
endpoint, duration, and hit outcome, then expires the visual locally. It never
uses Unity collision to infer damage or report a hit back to the provider.

`fields.surface` projection consumes the plugin-owned
`org.gamecult.eve.plugin-fields` contracts. `EveFieldsSplatRasterizer` owns the
Unity structured-buffer and RenderTexture lowering without importing provider
document classes.
