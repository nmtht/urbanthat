# UrbanBridge

Local, one-way synchronization from Rhino to Unreal. The repository contains
the Phase 1 Rhino plugin and the protocol contract for the Unreal receiver.

## Установка

Для обычного пользователя есть отдельная инструкция с установщиками для Windows и macOS:
[docs/installation.md](docs/installation.md).

## Rhino plugin

The plugin targets Rhino 8 on Windows and macOS (`net7.0`). Restore and build it
with:

```sh
dotnet build rhino-plugin/UrbanBridgePlugin.csproj
```

Load the resulting plugin assembly in Rhino. It opens `ws://localhost:7890`,
subscribes to Rhino add/replace/delete/open-document events, and sends JSON in
metres. On an Unreal `request_full_sync`, it serializes the active document on
the Rhino UI thread. See [the wire contract](docs/protocol.md).

Supported geometry is Curve (polyline), Mesh, Brep, Extrusion, and Surface.
Unsupported objects are reported in Rhino's command history and skipped.

## Проверка без Unreal

Для быстрой end-to-end проверки используйте included receiver probe из Node.js 22+
(он использует встроенный WebSocket, зависимости не нужны):

```sh
node tools/ws-probe.mjs
```

Проба подключается, отправляет `request_full_sync` и выводит тип и количество
полученных объектов. Подробный пошаговый сценарий, включая проверку add/replace/
delete и критерии готовности, находится в [docs/testing-rhino.md](docs/testing-rhino.md).
