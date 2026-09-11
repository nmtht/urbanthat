# Простая установка UrbanBridge для Rhino 8

Плагин работает в Rhino 8 на **Windows и macOS**. Установщик не требует Unreal
или Node.js: они нужны только для проверки передачи данных.

## Вариант A — готовая сборка (для обычного пользователя)

1. Скачайте архив релиза UrbanBridge для Rhino 8 и распакуйте его в любую папку.
2. Полностью закройте Rhino.
3. Запустите установщик для своей ОС:

   | ОС | Что запустить |
   | --- | --- |
   | macOS | Двойной клик по `installer/install-urbanbridge-macos.sh` в Terminal или `bash installer/install-urbanbridge-macos.sh` |
   | Windows | Правый клик по `installer/install-urbanbridge-windows.ps1` → **Run with PowerShell**; если Windows блокирует запуск, откройте PowerShell и выполните `powershell -ExecutionPolicy Bypass -File installer/install-urbanbridge-windows.ps1` |

4. Запустите Rhino и введите в командной строке `UrbanBridgeStatus`.
   Сообщение `Running at ws://localhost:7890` означает успешную установку.

При первом запуске Rhino может попросить подтвердить загрузку нового plug-in —
разрешите её. Если команда не найдена, в Rhino выполните `PlugInManager` →
**Install…** и укажите `UrbanBridgePlugin.rhp` из папки, куда установщик скопировал
плагин (путь выводится установщиком).

## Вариант B — собрать из исходного кода

На Windows или macOS с .NET SDK 7.x:

```sh
dotnet build rhino-plugin/UrbanBridgePlugin.csproj -c Release
```

Команда создаёт готовый bundle в
`rhino-plugin/bin/Release/net7.0/package/UrbanBridgePlugin`. Затем запустите
установщик из варианта A. В macOS используйте:

```sh
bash installer/install-urbanbridge-macos.sh
```

В Windows:

```powershell
powershell -ExecutionPolicy Bypass -File installer/install-urbanbridge-windows.ps1
```

## Что появляется внутри Rhino

Плагин не добавляет тяжёлую панель: его интерфейс — одна команда Rhino
`UrbanBridgeStatus`. Она выводит адрес сервера и число подключённых клиентов в
Command History. Это помогает быстро понять, загрузился ли мост, не занимая
место в интерфейсе Rhino.

Далее запустите `node tools/ws-probe.mjs`, если хотите проверить передачу
геометрии без Unreal. Полный тестовый сценарий приведён в
[testing-rhino.md](testing-rhino.md).

## Удаление

Закройте Rhino и удалите каталог `UrbanBridgePlugin` из стандартной папки
плагинов, которую вывел установщик. Затем запустите Rhino снова. На Windows это
`%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\UrbanBridgePlugin`, на macOS —
`~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/UrbanBridgePlugin`.
