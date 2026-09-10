import { Badge } from './ui';

export function Hero() {
  return (
    <section id="overview" className="relative overflow-hidden border-b border-slate-800/80">
      <div className="pointer-events-none absolute inset-0">
        <div className="absolute -left-40 top-10 h-96 w-96 rounded-full bg-amber-500/10 blur-3xl" />
        <div className="absolute -right-40 top-20 h-96 w-96 rounded-full bg-sky-500/10 blur-3xl" />
        <div
          className="absolute inset-0 opacity-[0.07]"
          style={{
            backgroundImage:
              'linear-gradient(to right, #94a3b8 1px, transparent 1px), linear-gradient(to bottom, #94a3b8 1px, transparent 1px)',
            backgroundSize: '48px 48px',
            maskImage: 'radial-gradient(ellipse at center, black 30%, transparent 75%)',
          }}
        />
      </div>
      <div className="relative mx-auto max-w-7xl px-5 pb-16 pt-16 md:px-8 md:pb-24 md:pt-24">
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone="cyan">ТЗ · мост Rhino ↔ Unreal</Badge>
          <Badge tone="slate">Phase 0–1</Badge>
          <Badge tone="slate">WebSocket · JSON · localhost:7890</Badge>
        </div>
        <h1 className="mt-6 max-w-4xl text-4xl font-semibold leading-[1.05] tracking-tight text-white md:text-6xl">
          Односторонний push геометрии
          <br />
          <span className="bg-gradient-to-r from-amber-300 via-fuchsia-400 to-sky-400 bg-clip-text text-transparent">
            из Rhino в Unreal Engine 5
          </span>
        </h1>
        <p className="mt-6 max-w-2xl text-lg leading-relaxed text-slate-400">
          Интерактивная спецификация и playground для bridge-процесса «как у Enscape»: Rhino-плагин сериализует
          объекты по слоям и User Text, Unreal-модуль превращает их в акторы с процедурным мешем или сплайном.
          Ниже — живой симулятор протокола: можно поднять сервер, подключить клиент, порвать соединение и посмотреть
          на reconnect и <code className="rounded bg-slate-800 px-1 font-mono text-sm text-slate-200">full_sync</code>.
        </p>
        <div className="mt-8 flex flex-wrap gap-3">
          <a
            href="#simulator"
            className="rounded-lg bg-cyan-500 px-5 py-2.5 text-sm font-semibold text-slate-950 transition hover:bg-cyan-400"
          >
            Открыть симулятор
          </a>
          <a
            href="#protocol"
            className="rounded-lg border border-slate-700 px-5 py-2.5 text-sm text-slate-200 transition hover:bg-slate-800"
          >
            Читать протокол
          </a>
        </div>

        <dl className="mt-14 grid grid-cols-2 gap-px overflow-hidden rounded-2xl border border-slate-800 bg-slate-800 md:grid-cols-4">
          {[
            ['Направление', 'Rhino → Unreal', 'обратной синхронизации нет'],
            ['Транспорт', 'WebSocket', 'Rhino — сервер, UE — клиент'],
            ['Формат', 'JSON', 'читается глазами в логах'],
            ['Классификация', 'по имени слоя', 'атрибуты — из User Text'],
          ].map(([k, v, s]) => (
            <div key={k} className="bg-slate-950 p-5">
              <dt className="font-mono text-[11px] uppercase tracking-wider text-slate-500">{k}</dt>
              <dd className="mt-1 text-lg font-semibold text-white">{v}</dd>
              <dd className="text-xs text-slate-500">{s}</dd>
            </div>
          ))}
        </dl>
      </div>
    </section>
  );
}
