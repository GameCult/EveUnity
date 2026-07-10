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
