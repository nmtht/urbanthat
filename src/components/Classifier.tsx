import { useState } from 'react';
import { classifyLayer, HINT_COLORS } from '../lib/geometry';
import { Badge, Card, CodeBlock, Section } from './ui';

const CPP = `EBridgeAssetHint ClassifyLayer(const FString& LayerName)
{
    FString Root;
    if (!LayerName.Split(TEXT("::"), &Root, nullptr)) Root = LayerName;
    Root.TrimStartAndEndInline();

    if (Root == TEXT("Zones"))     return EBridgeAssetHint::Zone;
    if (Root == TEXT("Roads"))     return EBridgeAssetHint::Road;
    if (Root == TEXT("Buildings")) return EBridgeAssetHint::Building;
    return EBridgeAssetHint::Generic; // рендерится, но вне доменной логики
}`;

const PRESETS = ['Zones', 'Zones::Residential', 'Roads::Primary', 'Buildings', 'Default', 'zones', 'Zones_old', 'Site::Buildings'];

export function Classifier() {
  const [name, setName] = useState('Zones::Residential');
  const hint = classifyLayer(name);
  const tone = hint === 'Zone' ? 'emerald' : hint === 'Road' ? 'amber' : hint === 'Building' ? 'slate' : 'rose';

  return (
    <Section
      id="classifier"
      eyebrow="04 · ClassifyLayer"
      title="Классификатор по имени слоя"
      lead="На Phase 0–1 результат влияет только на цвет/материал актора для визуальной отладки. Полноценные доменные Zone/Road/Building — Phase 2."
    >
      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <label className="font-mono text-[11px] uppercase tracking-wider text-slate-500">layer FullPath</label>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            className="mt-2 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 font-mono text-sm text-white outline-none focus:border-cyan-500/60"
          />
          <div className="mt-2 flex flex-wrap gap-1.5">
            {PRESETS.map((p) => (
              <button
                key={p}
                onClick={() => setName(p)}
                className="rounded-md bg-slate-800 px-2 py-1 font-mono text-[11px] text-slate-300 hover:bg-slate-700"
              >
                {p}
              </button>
            ))}
          </div>
          <div className="mt-6 flex items-center gap-4 rounded-xl border border-slate-800 bg-slate-950 p-4">
            <div className="h-12 w-12 rounded-lg shadow-inner" style={{ background: HINT_COLORS[hint] }} />
            <div>
              <div className="font-mono text-[11px] text-slate-500">EBridgeAssetHint</div>
              <div className="flex items-center gap-2">
                <span className="font-mono text-xl font-semibold text-white">{hint}</span>
                <Badge tone={tone}>{hint === 'Generic' ? 'вне доменной логики' : 'участвует в Phase 2'}</Badge>
              </div>
              <div className="mt-1 text-xs text-slate-400">
                {hint === 'Zone' && 'полупрозрачный цвет по zone_type из User Text'}
                {hint === 'Road' && 'USplineComponent, отрисовка линией'}
                {hint === 'Building' && 'серый непрозрачный процедурный меш'}
                {hint === 'Generic' && 'рендерится нейтральным цветом; корень слоя не совпал ни с одним из трёх (сравнение точное, регистрозависимое)'}
              </div>
            </div>
          </div>
        </Card>
        <div>
          <CodeBlock code={CPP} title="BridgeActorRegistry.cpp (эскиз)" />
          <div className="mt-4 grid grid-cols-2 gap-2 text-xs sm:grid-cols-4">
            {(['Zone', 'Road', 'Building', 'Generic'] as const).map((h) => (
              <div key={h} className="rounded-lg border border-slate-800 bg-slate-900/60 p-3">
                <div className="h-1.5 w-full rounded-full" style={{ background: HINT_COLORS[h] }} />
                <div className="mt-2 font-mono text-white">{h}</div>
                <div className="text-[11px] text-slate-500">
                  {h === 'Zone' ? '"Zones" / "Zones::*"' : h === 'Road' ? '"Roads" / "Roads::*"' : h === 'Building' ? '"Buildings" / "Buildings::*"' : 'всё остальное'}
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </Section>
  );
}
