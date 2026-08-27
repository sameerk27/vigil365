import React from "react";

let gradSeq = 0;

/**
 * Inline SVG sparkline. `color`/`fill` are the one sanctioned raw-hex slot
 * (SVG gradient stops and stroke). Pass `stroke="good"` for the green variant.
 */
export function Sparkline({ points, stroke = "accent", height = 120, fill = true, axis }: {
  points: number[];
  stroke?: "accent" | "good";
  height?: number;
  fill?: boolean;
  /** Optional [left, right] axis labels rendered below the chart. */
  axis?: [React.ReactNode, React.ReactNode];
}) {
  const strokeColor = stroke === "good" ? "#16a34a" : "#2563eb";
  const W = 100, H = 100; // viewBox units; preserveAspectRatio="none" stretches to box
  const gradId = `spark-grad-${(gradSeq = (gradSeq + 1) % 1_000_000)}`;

  if (points.length < 2) {
    return <div className="ui-spark" style={{ height }} aria-hidden="true" />;
  }

  const min = Math.min(...points);
  const max = Math.max(...points);
  const span = max - min || 1;
  const stepX = W / (points.length - 1);
  const coords = points.map((p, i) => {
    const x = i * stepX;
    const y = H - ((p - min) / span) * H;
    return [x, y] as const;
  });
  const line = coords.map(([x, y], i) => `${i === 0 ? "M" : "L"}${x.toFixed(2)} ${y.toFixed(2)}`).join(" ");
  const area = `${line} L${W} ${H} L0 ${H} Z`;
  const [lastX, lastY] = coords[coords.length - 1];

  return (
    <div className="ui-spark-wrap">
      <div className="ui-spark-plot" style={{ height }}>
        <svg className="ui-spark" viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" style={{ height }} role="img">
          {fill && (
            <defs>
              <linearGradient id={gradId} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0" stopColor={strokeColor} stopOpacity="0.2" />
                <stop offset="1" stopColor={strokeColor} stopOpacity="0" />
              </linearGradient>
            </defs>
          )}
          {fill && <path d={area} fill={`url(#${gradId})`} stroke="none" />}
          <path d={line} fill="none" stroke={strokeColor} strokeWidth="2" vectorEffect="non-scaling-stroke"
            strokeLinejoin="round" strokeLinecap="round" />
        </svg>
        {/* End marker as a fixed-size DOM overlay so it stays a true circle
            regardless of the non-uniform SVG stretch. Position (left/top) is
            genuinely dynamic, so it stays inline. */}
        <span
          className={`ui-spark-dot tone-${stroke}`}
          style={{ left: `${lastX}%`, top: `${(lastY / H) * 100}%` }}
        />
      </div>
      {axis && (
        <div className="ui-spark-axis">
          <span>{axis[0]}</span>
          <span>{axis[1]}</span>
        </div>
      )}
    </div>
  );
}
