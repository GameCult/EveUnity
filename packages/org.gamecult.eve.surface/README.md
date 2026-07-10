# Eve Surface Contract

This package defines the retained CultUI surface document that every Eve
runtime lowers.

Components own normal child layout through `Children`, live CultMesh value
bindings through `StateBindings`, and nested synced document regions through
`EmbeddedDocuments`. An embedded document slot names the child document id,
schema, presentation kind, and route hint; renderers resolve that child through
CultMesh instead of rebuilding it with runtime-local projection code.

See `../../docs/surface-contract-v1.md` for the full contract and
`../../web/fixtures/cultui-embedded-surface.json` for the parity fixture.
Runtime discovery and required feature coverage live in
`../../tools/parity/parity-manifest.json`; active GUI runtimes must list
`embeddedDocuments` as a supported feature and require the `embedded-surface`
fixture.

Verification entrypoints:

- Browser/TypeScript: `node --test web\eve-dsl.test.mjs`
- Semantic runtime matrix: `powershell -ExecutionPolicy Bypass -File .\scripts\run-parity-harness.ps1`
- Flutter GUI contract, when Flutter is installed: `flutter test --plain-name embedded_surface_fixture_contract`
- Rust/CultMesh document sync: run `cargo test rust_preserves_cultui_embedded_surface_slots_through_typed_document_sync` from `E:/Projects/CultLib/packages/cultnet-rs`

The parity manifest is the discovery source for web, Flutter desktop/Linux/Android,
iOS/UIKit, Android/Kotlin, Unity UI Toolkit, and Rust contract coverage. Do not
add a new renderer claim without adding its nested-surface fixture requirement
there.
