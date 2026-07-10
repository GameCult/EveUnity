# Eve Unity Scene Lowering

Provider-agnostic Unity scene lowering for `gamecult.eve.surface.v1`
interactive worlds.

Providers implement the surface-document, asset-manifest, command-sink, and
receipt-source ports. `EveUnityPlayableWorldClientBootstrap` wires those ports
to the generic scene host, input driver, camera rig, and `GameObject` sink.

The package never imports provider product assemblies.

