import { Acceptance } from './components/Acceptance';
import { Architecture } from './components/Architecture';
import { Classifier } from './components/Classifier';
import { Hero } from './components/Hero';
import { Nav } from './components/Nav';
import { Protocol } from './components/Protocol';
import { Scope } from './components/Scope';
import { Simulator } from './components/simulator/Simulator';

export default function App() {
  return (
    <div className="min-h-screen bg-slate-950 text-slate-200 antialiased">
      <Nav />
      <main>
        <Hero />
        <Architecture />
        <div className="border-t border-slate-800/80" />
        <Protocol />
        <div className="border-t border-slate-800/80" />
        <Simulator />
        <div className="border-t border-slate-800/80" />
        <Classifier />
        <div className="border-t border-slate-800/80" />
        <Acceptance />
        <div className="border-t border-slate-800/80" />
        <Scope />
      </main>
      <footer className="border-t border-slate-800/80 py-8">
        <div className="mx-auto flex max-w-7xl flex-col gap-2 px-5 font-mono text-[11px] text-slate-600 md:flex-row md:items-center md:justify-between md:px-8">
          <span>UrbanBridge · ТЗ «мост Rhino ↔ Unreal», Phase 0–1 · ws://localhost:7890 · JSON</span>
          <span>Симулятор воспроизводит контракт протокола в браузере; реальные плагины — в rhino-plugin/ и unreal-plugin/</span>
        </div>
      </footer>
    </div>
  );
}
