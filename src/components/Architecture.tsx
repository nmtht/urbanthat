import { Badge, Card, Section } from './ui';

const rhinoItems = [
  ['UrbanBridgePlugin.cs', 'точка входа, PlugIn.OnLoad → сервер'],
  ['BridgeServer.cs', 'WebSocket-сервер, System.Net.WebSockets / Fleck'],
  ['ObjectSerializer.cs', 'RhinoObject → object_payload, единицы → метры'],
  ['DocumentEventHandlers.cs', 'Add / Replace / Delete / EndOpenDocument'],
];

const ueItems = [
  ['UBridgeConnection', 'IWebSocket, reconnect 1s → 2s → 4s → 10s'],
  ['FBridgeMessageParser', 'JSON → FObjectPayload / FMeshData / FPolylineData'],
  ['ABridgeActorRegistry', 'TMap<FGuid, AActor*>, upsert / delete'],
  ['AGenericBridgeMeshActor', 'UProceduralMeshComponent::CreateMeshSection'],
  ['AGenericBridgePolylineActor', 'USplineComponent из точек'],
];

const flow = [
  { who: 'Rhino', text: 'Событие документа (AddRhinoObject / ReplaceRhinoObject / DeleteRhinoObject / EndOpenDocument)' },
  { who: 'Rhino', text: 'Сериализация: Curve → polyline (сегмент ≤ 0.5 м), Brep/Extrusion/Surface → Mesh.CreateFromBrep' },
  { who: 'Rhino', text: 'Слой → FullPath, User Text → attributes (строки, без валидации), координаты → метры' },
  { who: 'wire', text: 'JSON по WebSocket ws://localhost:7890; heartbeat каждые 5 с в обе стороны' },
  { who: 'UE', text: 'Коллбэк из фонового потока → AsyncTask(ENamedThreads::GameThread, …)' },
  { who: 'UE', text: 'Парсер → реестр → создать / обновить / уничтожить актор; метры × 100 → UE-юниты' },
  { who: 'UE', text: 'ClassifyLayer(FullPath) → только цвет/материал для отладки (доменная логика — Phase 2)' },
];

export function Architecture() {
  return (
    <Section
      id="architecture"
      eyebrow="01 · Состав системы"
      title="Три независимых компонента с чётким контрактом"
      lead="Rhino-плагин и Unreal-модуль разрабатываются и тестируются отдельно: плагин — логируя исходящие сообщения без Unreal, модуль — проигрывая записанные сообщения из файла без Rhino."
    >
      <div className="grid gap-4 lg:grid-cols-[1fr_auto_1fr]">
        <Card className="border-amber-500/20">
          <div className="flex items-center justify-between">
            <div>
              <div className="font-mono text-[11px] uppercase tracking-wider text-amber-400">RhinoBridgePlugin</div>
              <div className="mt-1 text-lg font-semibold text-white">Rhino · C# · RhinoCommon</div>
            </div>
            <Badge tone="amber">WS сервер</Badge>
          </div>
          <ul className="mt-5 space-y-2.5">
            {rhinoItems.map(([f, d]) => (
              <li key={f} className="flex gap-3 text-sm">
                <code className="shrink-0 font-mono text-[12px] text-amber-200">{f}</code>
                <span className="text-slate-400">{d}</span>
              </li>
            ))}
          </ul>
          <p className="mt-5 text-xs leading-relaxed text-slate-500">
            Rhino-сессия обычно живёт дольше конкретного запуска Unreal-приложения — поэтому сервер здесь. Лог через{' '}
            <code className="font-mono text-slate-400">RhinoApp.WriteLine("[UrbanBridge] …")</code>.
          </p>
        </Card>

        <div className="flex items-center justify-center py-2 lg:flex-col lg:px-2">
          <div className="flex items-center gap-2 lg:flex-col">
            <div className="hidden h-px w-10 bg-gradient-to-r from-amber-400 to-fuchsia-500 lg:block lg:h-16 lg:w-px lg:bg-gradient-to-b" />
            <div className="rounded-xl border border-fuchsia-500/30 bg-fuchsia-500/10 px-4 py-3 text-center">
              <div className="font-mono text-[11px] text-fuchsia-300">ws://localhost:7890</div>
              <div className="mt-0.5 text-xs text-slate-300">JSON · push →</div>
              <div className="text-[10px] text-slate-500">heartbeat 5 s ↔</div>
            </div>
            <div className="hidden h-px w-10 bg-gradient-to-r from-fuchsia-500 to-sky-400 lg:block lg:h-16 lg:w-px lg:bg-gradient-to-b" />
          </div>
        </div>

        <Card className="border-sky-500/20">
          <div className="flex items-center justify-between">
            <div>
              <div className="font-mono text-[11px] uppercase tracking-wider text-sky-400">UrbanBridgeReceiver</div>
              <div className="mt-1 text-lg font-semibold text-white">Unreal 5 · C++ · модуль UrbanBridge</div>
            </div>
            <Badge tone="sky">WS клиент</Badge>
          </div>
          <ul className="mt-5 space-y-2.5">
            {ueItems.map(([f, d]) => (
              <li key={f} className="flex gap-3 text-sm">
                <code className="shrink-0 font-mono text-[12px] text-sky-200">{f}</code>
                <span className="text-slate-400">{d}</span>
              </li>
            ))}
          </ul>
          <p className="mt-5 text-xs leading-relaxed text-slate-500">
            Встроенный модуль <code className="font-mono text-slate-400">WebSockets</code> (
            <code className="font-mono text-slate-400">FWebSocketsModule</code>), внешних зависимостей нет. Все операции
            с акторами — только на GameThread.
          </p>
        </Card>
      </div>

      <div className="mt-8 grid gap-4 lg:grid-cols-[2fr_1fr]">
        <Card>
          <div className="mb-4 text-sm font-semibold text-white">Путь одного объекта</div>
          <ol className="relative space-y-3 border-l border-slate-800 pl-5">
            {flow.map((s, i) => (
              <li key={i} className="relative text-sm">
                <span
                  className={
                    'absolute -left-[26px] top-1 h-2.5 w-2.5 rounded-full ring-4 ring-slate-950 ' +
                    (s.who === 'Rhino' ? 'bg-amber-400' : s.who === 'UE' ? 'bg-sky-400' : 'bg-fuchsia-400')
                  }
                />
                <span className="mr-2 font-mono text-[11px] uppercase text-slate-500">{s.who}</span>
                <span className="text-slate-300">{s.text}</span>
              </li>
            ))}
          </ol>
        </Card>
        <Card>
          <div className="mb-4 text-sm font-semibold text-white">Почему так</div>
          <dl className="space-y-3 text-sm">
            <div>
              <dt className="text-slate-200">WebSocket, а не named pipes</dt>
              <dd className="text-slate-500">одинаково на Windows и в перспективе на macOS</dd>
            </div>
            <div>
              <dt className="text-slate-200">JSON, а не protobuf/FlatBuffers</dt>
              <dd className="text-slate-500">скорость разработки и отладки важнее производительности; бинарный формат — в бэклоге</dd>
            </div>
            <div>
              <dt className="text-slate-200">Конвертация единиц на стороне Rhino</dt>
              <dd className="text-slate-500">единый источник правды: по проводу всегда метры, UE умножает на 100</dd>
            </div>
            <div>
              <dt className="text-slate-200">batch_upsert для групповых правок</dt>
              <dd className="text-slate-500">не заваливать сокет сотней одиночных сообщений при массовом редактировании</dd>
            </div>
          </dl>
        </Card>
      </div>
    </Section>
  );
}
