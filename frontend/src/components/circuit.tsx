// The signature: how a turn ran, drawn as a circuit trace. Copper lights each node the turn reaches;
// the node it is working on pulses. Triage and routing are code, so the trace shows the model proposing
// and the code deciding.
import { circuit } from "@/lib/support/labels";
import type { AssistantEntry } from "@/lib/support/conversation";

export function Circuit({ entry }: { entry: AssistantEntry | undefined }) {
  const nodes = circuit(entry);
  return (
    <ol className="relative" aria-label="How this turn ran">
      {nodes.map((node, index) => {
        const lit = node.state === "lit";
        const active = node.state === "active";
        const nextLit = nodes[index + 1]?.state === "lit" || nodes[index + 1]?.state === "active";
        const negative = node.tone === "negative";
        return (
          <li key={node.key} className="relative grid grid-cols-[20px_1fr] gap-x-3 pb-5 last:pb-0">
            {index < nodes.length - 1 && (
              <span
                aria-hidden="true"
                className={`absolute top-4 bottom-0 left-[9px] w-0.5 transition-colors duration-150 ${
                  lit && nextLit ? "bg-accent-primary" : "bg-trace-idle"
                }`}
              />
            )}
            <span aria-hidden="true" className="relative mt-0.5 grid size-5 place-items-center">
              {active && (
                <span className="pad-pulse bg-accent-primary/25 absolute inset-0 rounded-full" />
              )}
              <span
                className={`size-3 rounded-full border-2 transition-colors duration-150 ${
                  negative
                    ? "border-status-danger bg-status-danger"
                    : lit
                      ? "border-accent-primary bg-accent-primary"
                      : active
                        ? "border-accent-primary bg-bg-secondary"
                        : "border-trace-idle bg-bg-secondary"
                }`}
              />
            </span>
            <div className="min-w-0">
              <p
                className={`text-sm font-medium ${
                  lit || active ? "text-text-primary" : "text-text-tertiary"
                } ${node.state === "skipped" ? "decoration-trace-idle line-through" : ""}`}
              >
                {node.label}
                <span className="sr-only">
                  {node.state === "lit"
                    ? ": done"
                    : node.state === "active"
                      ? ": in progress"
                      : node.state === "skipped"
                        ? ": not needed"
                        : ""}
                </span>
              </p>
              {node.items && node.items.length > 0 && (
                <ul className="mt-1 flex flex-wrap gap-1">
                  {node.items.map((item) => (
                    <li
                      key={item.name}
                      className={`text-accent-primary rounded-sm px-1.5 py-0.5 font-mono text-xs ${
                        item.held
                          ? "border-accent-primary border border-dashed"
                          : "bg-accent-tint border border-transparent"
                      }`}
                    >
                      {item.name}
                      {item.held && <span className="sr-only"> (held, not run)</span>}
                    </li>
                  ))}
                </ul>
              )}
              {node.detail && (
                <p
                  className={`mt-0.5 text-xs ${negative ? "text-status-danger" : "text-text-tertiary"}`}
                >
                  {node.detail}
                </p>
              )}
            </div>
          </li>
        );
      })}
    </ol>
  );
}
