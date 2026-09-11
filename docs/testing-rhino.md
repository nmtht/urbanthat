# Проверка Rhino-плагина Phase 1

Этот сценарий позволяет проверить протокол **до** интеграции Unreal: скрипт
`tools/ws-probe.mjs` играет роль минимального WebSocket-получателя.

## Требования

- Rhino 8 для Windows **или macOS**;
- .NET SDK 7.x (проект нацелен на кроссплатформенный `net7.0`);
- Node.js 22+ для probe (не нужен для сборки плагина).

## Сборка и загрузка

1. В корне репозитория выполните `dotnet build rhino-plugin/UrbanBridgePlugin.csproj`.
   После успешной сборки файл для установки находится по пути
   `rhino-plugin/bin/Debug/net7.0/UrbanBridgePlugin.rhp`.
2. В командной строке Rhino выполните `PlugInManager`, нажмите **Install…** и выберите
   этот `.rhp`. Эта команда доступна и в Rhino for Windows, и в Rhino for Mac.
   Убедитесь, что плагин включён.
3. Откройте Command History. После загрузки должна появиться строка
   `[UrbanBridge] Rhino bridge started at ws://localhost:7890`.

> Если WebSocket-сервер не может открыть порт, освободите `7890` или измените
> константу адреса одновременно в `BridgeServer.cs` и в `URBANBRIDGE_URL` у probe.

## Ручной end-to-end сценарий

1. Создайте новый Rhino-документ в метрах. Создайте слои `Zones`, `Roads`,
   `Buildings`; при желании добавьте дочерний `Zones::Residential`.
2. На одном слое создайте mesh или box, на другом — curve, на третьем — extrusion.
   Добавьте выбранному объекту User Text, например `zone_type=residential`.
3. В отдельном терминале из корня репозитория запустите:

   ```powershell
   node tools/ws-probe.mjs
   ```

   Ожидаемый первый предметный ответ — `type=full_sync objects=N`, где `N` —
   число поддерживаемых видимых объектов. Heartbeat будет появляться каждые 5 секунд.
4. Добавьте или переместите один объект. В течение примерно 75 мс probe должен
   показать `type=object_upserted`.
5. Добавьте/измените несколько объектов одним действием. Должен прийти
   `type=batch_upsert objects=N`.
6. Удалите объект. Должен прийти `type=object_deleted id=<GUID>`.
7. Перезапустите probe. Он повторно отправит `request_full_sync`; Rhino должен
   ответить новым `full_sync` без перезапуска Rhino.

## Критерии готовности

- `full_sync` содержит ожидаемое число объектов и не прерывается из-за
  неподдерживаемого объекта (он только записывается в Command History).
- Payload кривой содержит `geometry_type: "polyline"`, а mesh —
  `geometry_type: "mesh"`; ровно одно из `mesh` / `polyline` задано.
- Для документа в миллиметрах координаты в payload всё равно выражены в метрах.
- Добавление, замена и удаление приводят к соответствующим сообщениям без
  перезагрузки плагина.

После этого шага Unreal receiver может использовать этот же сценарий: он должен
подключиться к `ws://localhost:7890`, отправить `request_full_sync` и вести
реестр акторов по GUID из `id`.
