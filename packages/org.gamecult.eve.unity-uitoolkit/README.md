# Eve UI Toolkit Lowering

This package lowers `gamecult.eve.surface.v1` retained surface documents into
Unity UI Toolkit `VisualElement` trees.

It owns native projection only. Providers still own truth, accepted state,
style token values, and command effects through CultMesh/CultNet. Unknown
component kinds degrade to inert containers instead of gaining local semantics.

Plugin semantics enter through plugin ABI sidecars and advertisements, not
through Unity. This package includes first-party projection adapters for the
`sai.vn` projection surface (`vn.stage`, dialogue panels, action rails, and
story command requests), the `norn.graph` projection surface (`embed.norn`),
and the `tex.math` source-fallback projection surface (`embed.tex`,
`tex.inline`, and `tex.block`). Sai still owns story state and command
semantics. Norn still owns graph layout and graph semantics. TeX still owns
parsing, typesetting, baseline metrics, and cached render semantics. Unity only
owns native projection of already-declared plugin capabilities and emits
`gamecult.eve.command.v1` requests.

This package lives in the Eve repository as the shared Unity lowering target.
Aetheria and other Unity consumers should import it from Eve instead of
carrying local copies.

Nested CultUI regions use component `EmbeddedDocuments`. Pass an
`EmbeddedDocumentResolver` through `EveUiToolkitSurfaceOptions`; the lowerer
mounts the resolved child surface under the slot while preserving the child
document's command surface id.

Discovery and test coverage for this feature live in the shared Eve parity
matrix: `../../tools/parity/parity-manifest.json` names `embeddedDocuments` and
`../../web/fixtures/cultui-embedded-surface.json` is the canonical fixture.
Unity consumers should keep verifier coverage that builds a parent surface with
a resolver-backed child, then checks for the embedded visual element rather than
copying child state into a local adapter.

The runtime capability manifest is
`eve-runtime-capability.json`. Its lifecycle section records the current split
evidence:

- release: typed UPM release request contract, UPM `.tgz` artifact smoke, and
  incubating package identity;
- test: package-owned EditMode tests run through EveUnity's clean `TestProject`
  consumer in Unity batchmode;
- capture: typed capture request contract and pending Unity editor or
  batchmode PNG artifact.

The release stage also declares the UPM release contract:
`org.gamecult.eve.unity-uitoolkit` is released from this package root, reads
its version from `package.json`, and uses tag pattern
`eveunity-uitoolkit-v{version}` once the tag is cut from EveUnity.
`tools/eveunity/eveunity-release-contract.mjs` builds a
`gamecult.eve.runtime_release_request.v1` request from the runtime capability
manifest and UPM package manifest. The request names the package version, tag,
artifact path, dependency set, required package dependency owners, and target
`GameCult/EveUnity` repository without pretending Eve has published the tagged
release.
`tools/eveunity/eveunity-release-artifact.mjs` consumes that request and runs
`npm pack` against the declared package root, producing the declared
`org.gamecult.eve.unity-uitoolkit-{version}.tgz` artifact as local smoke
evidence. The tagged release still belongs in the EveUnity repo.

The test stage declares the Unity EditMode runner contract:
`scripts/run-uitoolkit-tests.ps1` runs `GameCult.Eve.UnityUIToolkit.Tests` on
`EditMode` from EveUnity's package-only `TestProject` and writes XML and log
artifacts under `artifacts/uitoolkit-tests/{stamp}`. This is a generic Unity
package consumption proof. The test contract names the CultLib-owned
NuGet/precompiled assembly inputs Unity needs during incubation; CultLib still
owns the .NET/NuGet dependency story for its assemblies.
Each managed dependency record carries a nested `dependencyContract` naming
CultLib as `sourceAuthority`, `nuget` as `packageSource`, the package id,
assembly name, version policy, Unity incubation resolution mode, and the
handoff requirement for EveUnity. This is not Eve inventing a second dependency
system. It is EveUnity declaring the assemblies it consumes while CultLib keeps
the package authority.

The capture stage declares `gamecult.eve.runtime_capture_request.v1` request
construction through `tools/eveunity/eveunity-capture-contract.mjs`. That
request is derived from the runtime capability manifest and Aetheria's provider
advertisement, then points at the PNG artifact EveUnity must produce later. It
does not fake a screenshot; it makes the capture input contract executable.
`tools/eveunity/eveunity-uitoolkit-capture-artifact.mjs` also emits a typed
`gamecult.eve.unity_uitoolkit_projection.v1` JSON projection artifact for the
Aetheria world surface. That artifact is runtime projection evidence, not
provider state and not a Unity frame capture.

The manifest also declares `worldSurfaceLowering` target `unity-uitoolkit`.
That means this package can lower provider-advertised `interactive-world` and
`interactive-world-editor` surfaces as semantic UI Toolkit command surfaces.
It does not claim Unity scene/world simulation ownership; Aetheria still owns
world state, assets, command acceptance, and receipts.

Those lifecycle claims are validated by the parity harness. A missing evidence
path is a runtime capability error, not a README footnote.

The split handoff manifest is `eveunity-split-handoff.json`. It is the
machine-readable map of what EveUnity must carry out of Eve incubation: the UPM
package body, the Unity test lifecycle, the Unity capture lifecycle, the Eve
contracts it consumes, and the forbidden imports it must not use as shortcuts.
It does not make the package ready to split by itself; it makes the remaining
release, test, and capture blockers inspectable.

From the repository root, run the split lifecycle smoke with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-eveunity-lifecycle-smoke.ps1
```

That smoke validates `eve-runtime-capability.json`, checks lifecycle evidence
paths, builds the UPM artifact smoke, runs the Aetheria Unity package
consumer-build smoke, and verifies the split handoff manifest. Pass
`-RunUnityEditMode` when the local Unity editor should also execute the
incubating batchmode EditMode test runner. The final tagged UPM release,
batchmode runner ownership, and capture artifact still graduate to `EveUnity`.
The capture contract smoke is:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-eveunity-capture-contract-smoke.ps1
```

The semantic projection capture smoke is:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-eveunity-uitoolkit-capture-smoke.ps1
```

The release contract smoke is:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-eveunity-release-contract-smoke.ps1
```

The release artifact smoke is:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-eveunity-release-artifact-smoke.ps1
```

For Aetheria, the Unity evidence path is:

- `powershell -ExecutionPolicy Bypass -File ..\..\scripts\run-aetheria-unity-package-smoke.ps1`
- `powershell -ExecutionPolicy Bypass -File ..\..\scripts\run-aetheria-unity-editmode-tests.ps1`

The EditMode test asmdef declares Unity precompiled references for the
Brokkr/CultMesh DLLs Unity needs to resolve `GameCult.Mesh`. That is Unity
assembly plumbing, not a claim that CultLib lacks NuGet/.NET packaging.

Those checks sit alongside Eve's shared browser, Flutter, iOS, Android/Kotlin,
and Rust contract tests, all discoverable from the parity manifest.
