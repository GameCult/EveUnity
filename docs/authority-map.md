# EveUnity Authority Map

## Objective

Lower provider-authored Eve surfaces into a playable Unity scene without
importing provider product code into the generic runtime.

## Owner

EveUnity owns Unity scene projection, `GameObject` lifecycle, camera and input
drivers, asset-provider hooks, package release, runtime tests, and captures.

## Inputs

- CultMesh rendezvous endpoint
- CultMesh Verse catalog
- `gamecult.eve.provider_advertisement.v1`
- `gamecult.eve.surface.v1`
- provider asset-manifest documents
- provider command receipts
- semantic render-channel inclusion and exclusion policy
- Unity input and local presentation assets

## Outputs

- disposable Unity scene projection
- `gamecult.eve.command_invocation.v1` intents sent through the advertised boundary
- runtime capability, test, release, and capture evidence
- owner-controlled PlayMode capture from `TestProject`

## Derived State

`ActiveWorld`, scene objects, transforms, markers, camera pose, native
render-channel layer mappings, asset caches, presentation counters, and receipt
display state are projections. They are not world truth.

## Forbidden Writers

Unity transforms, input drivers, camera rigs, scene sinks, asset caches, and
pending receipts may not mutate or simulate provider world state. Plugin
projection shells may not implement Sai, Norn, or TeX semantics. Portable
surfaces may not carry Unity layer numbers or camera culling masks.

## Shared Paths

Initial connect discovers a Verse endpoint and selects a provider surface by
semantic kind. Refresh, reconnect, and terminal-receipt reconciliation consume
that provider's surface documents. Movement, focus, target, and action input all
become `gamecult.eve.command_invocation.v1` through the provider-advertised
command boundary.

## Cut Line

Aetheria retains its daemon, authored surfaces, assets, command acceptance,
receipts, adapter, and provider conformance scenarios. Eve retains contracts
and conformance policy. EveUnity retains only generic Unity projection and its
runtime lifecycle.

## Capture Proofs

`TestProject` proves that a clean Unity project can install CultLib, Eve
contracts, and EveUnity without Aetheria or Brokkr assemblies. Its world-smoke
capture is the generic control witness. The Aetheria product witness gives the
same client only a rendezvous endpoint, then proves Verse discovery, provider
selection, asset loading, movement, commands, receipts, and rendered capture
against a separately running daemon.
