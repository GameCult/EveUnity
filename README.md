# EveUnity

EveUnity is the Unity lowering client for Eve interactive surfaces.

It owns Unity projection, scene lifecycle, input capture, camera behavior,
asset resolution hooks, UPM packaging, runtime tests, and frame evidence. It
does not own provider state, simulation, commands, receipts, assets, or plugin
semantics.

The first package is `org.gamecult.eve.unity-scene`. A generic Unity host can
connect any provider component that implements the EveUnity provider ports,
including Aetheria's provider-owned adapter for its daemon-published 3D ARPG
surface.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-package-tests.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\pack.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\run-playmode-capture.ps1
```

See [docs/authority-map.md](docs/authority-map.md) for the live boundary.

`TestProject` is the owner-controlled minimal Unity consumer. It depends on the
generated CultLib UPM package and Eve contracts directly, contains no Aetheria
assemblies, and produces the generic `eve.world-smoke` PlayMode PNG control.
