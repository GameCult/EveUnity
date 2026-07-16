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

`IEveUnityPresentedEntityRegistry` exposes each committed SoA generation as
immutable entity facts plus presentation transforms, addressable by Eve entity
id or source index. Camera, selection, and provider UI adapters consume that
registry instead of reconstructing provider gameplay objects. It owns
presentation lookup only; simulation state and commands remain provider-owned.

World surfaces may exclude semantic render channels through the
`excludedRenderChannels` world-root property. Runtime asset variants map those
channel names to Unity layers with `renderChannel.<channel>.unityLayer`
metadata. Surfaces never publish Unity layer numbers or camera masks: providers
own which semantic content belongs in a view, while EveUnity owns the native
layer mapping and subtracts only those layers from the camera's configured
culling mask.

World surfaces may also advertise a presentation-only directional key light
through `keyLightDirection`, `keyLightColor`, and `keyLightIntensity`.
EveUnity lowers those generic values into a transient Unity light and removes
it when the world camera lease ends. The provider owns the lighting design;
the light never becomes physics, visibility, or gameplay authority.

Retained provider feedback is lowered without becoming Unity authority.
`EveUnityFeedbackPresenter` emits each new `feedback.event` identity once, and
`EveUnityShotReceiptPresenter` does the same for `shot.receipt` trajectories.
The generic `EveUnityShotTrajectoryRenderer` renders provider-authored origin,
endpoint, duration, and hit outcome, then expires the visual locally. It never
uses Unity collision to infer damage or report a hit back to the provider.
Providers may publish a non-spatial `combat.presentation` component beside the
SoA body view. `EveUnityCombatPresentationRenderer` lowers its selected target,
contact visibility, lock progress, meter ratios, semantic roles, and timing
into reticle, lock, shield/hull, and hit-marker visuals. Hit markers require a
new deduplicated shot receipt from the advertised controlled source to the
advertised selection with authoritative applied or shield-absorbed damage.
SoA remains the only transform authority; the presentation component never
duplicates bodies or computes combat outcomes.
Continuous `beam.presentation` nodes bind provider-authored effect assets to
the current presented-entity generation. The generic beam renderer follows the
advertised source transform, applies provider-owned power to particle emission,
and reconciles asset lifecycles by semantic role. It never raycasts, applies
force, infers contact, collects cargo, or emits gameplay receipts.
The reusable `LightningCompute` renderer and its shader/material bundle live in
this runtime package with their original Unity GUIDs. Provider asset bundles may
therefore reference the effect without importing Aetheria gameplay code.

`fields.surface` projection consumes the plugin-owned
`org.gamecult.eve.plugin-fields` contracts. `EveFieldsSplatRasterizer` owns the
Unity structured-buffer and RenderTexture lowering without importing provider
document classes.

`field.volume3d` surfaces name logical texture ports, feature names, quality,
and authored scalar/vector parameters. Concrete Unity shader properties,
keywords, and pass indices belong to the selected `unity-scene` asset variant
under `unity.volume.*` metadata. The lowerer fails closed when that native
program descriptor is absent or incomplete. This keeps provider shader ABIs
out of Eve's semantic surface while allowing a provider-owned shader to consume
portable Eve Fields documents. Volume composition runs as a generic URP render
pass before post-processing so the provider-advertised profile grades both the
native scene and the provider volume through the same camera pipeline.

Camera-relative field consumers share `viewportAnchor`, `span`,
`cellWorldSize`, `viewportSnapLayer`, and `viewportSnapTexels` as one viewport
frame contract. Volume and particle lowerers resolve that frame through the
same generic path, so a snapped stateless particle lattice cannot drift from
the field textures it samples. A `world.scene3d` may also request
`temporal-reprojection.v1`; EveUnity lowers its neutral history blend, jitter,
quality, and sharpening values to URP TAA and restores the previous camera
state when the world releases the camera.

`field.particles3d` may advertise provider material textures, viewport-relative
texture scales, and a logical render-frame index port. These are presentation
inputs: EveUnity binds the selected provider assets and current display frame
without assigning product meaning to the shader. Stateless simulation time and
particle positions remain provider-document inputs.

Providers may advertise per-layer resolution scales, mipmap use, and filtering
through `layerTargetDescriptors`; the Unity lowerer applies those native target
requirements without assigning gameplay or product meaning to the layer keys.
`documentFloatBindings` maps standard document scalars to logical float ports as
`source=port,scale,offset`. This lets animated native programs consume
provider-authoritative simulation time without making the client clock an
authority.
Native volume programs may advertise a `cameraToWorld` matrix port when their
procedural passes require an explicit camera transform instead of relying on
render-pipeline globals.
