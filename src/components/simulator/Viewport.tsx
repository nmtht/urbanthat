import { useEffect, useRef, type ReactNode } from 'react';
import type { Vec2 } from '../../lib/geometry';

export function Viewport({
  render,
  onPick,
  overlay,
  className,
  deps,
}: {
  render: (ctx: CanvasRenderingContext2D, w: number, h: number) => Map<string, Vec2>;
  onPick: (centers: Map<string, Vec2>, x: number, y: number) => void;
  overlay?: ReactNode;
  className?: string;
  deps: unknown[];
}) {
  const ref = useRef<HTMLCanvasElement>(null);
  const centers = useRef<Map<string, Vec2>>(new Map());
  const size = useRef({ w: 0, h: 0 });
  const renderRef = useRef(render);
  renderRef.current = render;

  const draw = () => {
    const c = ref.current;
    if (!c) return;
    const { w, h } = size.current;
    if (!w || !h) return;
    const dpr = window.devicePixelRatio || 1;
    if (c.width !== Math.round(w * dpr) || c.height !== Math.round(h * dpr)) {
      c.width = Math.round(w * dpr);
      c.height = Math.round(h * dpr);
    }
    const ctx = c.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    centers.current = renderRef.current(ctx, w, h);
  };

  useEffect(() => {
    const c = ref.current;
    if (!c || !c.parentElement) return;
    const ro = new ResizeObserver((entries) => {
      const r = entries[0].contentRect;
      size.current = { w: r.width, h: r.height };
      draw();
    });
    ro.observe(c.parentElement);
    return () => ro.disconnect();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(draw, deps);

  return (
    <div className={className ?? 'relative h-[340px] w-full overflow-hidden rounded-xl'}>
      <canvas
        ref={ref}
        className="absolute inset-0 h-full w-full cursor-crosshair"
        onClick={(e) => {
          const r = e.currentTarget.getBoundingClientRect();
          const x = e.clientX - r.left;
          const y = e.clientY - r.top;
          onPick(centers.current, x, y);
        }}
      />
      {overlay}
    </div>
  );
}
