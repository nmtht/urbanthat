import { Card, CodeBlock, Section } from './ui';

const OUT = [
  ['Обратная синхронизация Unreal → Rhino', 'поток строго односторонний'],
  ['Доменные Zone/Road/Building с расчётами', 'площадь, FAR, население — Phase 2'],
  ['UI-инспектор атрибутов внутри Unreal', 'пока только лог / debug-вывод'],
  ['Импорт OSM/GIS', 'отдельный источник данных'],
  ['MCP-сервер', ''],
  ['Кастомная панель атрибутов в Rhino', 'используется штатный User Text'],
  ['Бинарный протокол вместо JSON', 'бэклог, если профилирование покажет узкое место'],
  ['macOS для Rhino-плагина', 'после подтверждения подхода на Windows'],
];

const TREE = `urban-bridge/
├── rhino-plugin/
│   ├── UrbanBridgePlugin.csproj
│   ├── UrbanBridgePlugin.cs        # точка входа, PlugIn
│   ├── BridgeServer.cs             # WebSocket-сервер
│   ├── ObjectSerializer.cs         # RhinoObject → object_payload
│   └── DocumentEventHandlers.cs    # подписки на события RhinoDoc
├── unreal-plugin/
│   └── UrbanBridgeReceiver/
│       ├── UrbanBridgeReceiver.uplugin
│       └── Source/UrbanBridge/
│           ├── Public/
│           │   ├── BridgeConnection.h
│           │   ├── BridgeMessageParser.h
│           │   ├── BridgeActorRegistry.h
│           │   ├── GenericBridgeMeshActor.h
│           │   └── GenericBridgePolylineActor.h
│           └── Private/
│               └── (соответствующие .cpp)
├── docs/
│   └── protocol.md                 # живой источник правды по протоколу
└── README.md`;

const ROBUST = [
  ['Некорректная геометрия', 'не блокировать full_sync: залогировать объект, продолжить со следующим'],
  ['Нет нормалей у меша', 'normals: null → Unreal считает по треугольникам'],
  ['Фоновый поток WebSocket', 'все операции с акторами через AsyncTask(ENamedThreads::GameThread, …)'],
  ['Разрыв соединения', 'heartbeat 5 с + reconnect 1s → 2s → 4s → max 10s, затем request_full_sync'],
  ['Групповое редактирование', 'batch_upsert вместо N одиночных сообщений'],
  ['Разные единицы документа', 'плагин конвертирует в метры перед отправкой; UE × 100'],
];

export function Scope() {
  return (
    <Section
      id="scope"
      eyebrow="06 · Границы и структура"
      title="Явно вне скоупа, устойчивость и репозиторий"
      lead="Чтобы задача не расширялась неявно в процессе — фиксируем, чего сознательно не делаем на этом этапе."
    >
      <div className="grid gap-4 lg:grid-cols-3">
        <Card>
          <div className="mb-3 text-sm font-semibold text-white">Вне скоупа Phase 0–1</div>
          <ul className="space-y-2">
            {OUT.map(([t, d]) => (
              <li key={t} className="flex gap-2.5 text-sm">
                <span className="mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full bg-rose-400" />
                <div>
                  <div className="text-slate-200">{t}</div>
                  {d && <div className="text-xs text-slate-500">{d}</div>}
                </div>
              </li>
            ))}
          </ul>
        </Card>
        <Card>
          <div className="mb-3 text-sm font-semibold text-white">Устойчивость (4.4, 5.3)</div>
          <ul className="space-y-2.5">
            {ROBUST.map(([t, d]) => (
              <li key={t} className="text-sm">
                <div className="text-slate-200">{t}</div>
                <div className="text-xs text-slate-500">{d}</div>
              </li>
            ))}
          </ul>
        </Card>
        <div>
          <CodeBlock code={TREE} title="структура репозитория" />
        </div>
      </div>
    </Section>
  );
}
