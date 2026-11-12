// Renders a reply's light markdown (paragraphs, "- " lists, **bold**) as React elements, never as HTML:
// the text comes from a model, so nothing in it can become markup.
import { Fragment, type ReactNode } from "react";

export function RichText({ text }: { text: string }) {
  const blocks = text.trim().split(/\n{2,}/);
  return (
    <>
      {blocks.map((block, index) => {
        const lines = block.split("\n");
        if (lines.every((line) => /^\s*[-*•]\s+/.test(line))) {
          return (
            <ul key={index} className="marker:text-text-tertiary my-2 list-disc space-y-1 pl-5">
              {lines.map((line, item) => (
                <li key={item}>{inline(line.replace(/^\s*[-*•]\s+/, ""))}</li>
              ))}
            </ul>
          );
        }
        return (
          <p key={index} className="my-2 first:mt-0 last:mb-0">
            {lines.map((line, item) => (
              <Fragment key={item}>
                {item > 0 && <br />}
                {inline(line)}
              </Fragment>
            ))}
          </p>
        );
      })}
    </>
  );
}

/** **bold** spans; record ids (ORD-1002, TKT-0005, KB-102...) in the mono face, as the fixtures write them. */
function inline(text: string): ReactNode[] {
  return text.split(/(\*\*[^*]+\*\*)/g).flatMap<ReactNode>((part, index) => {
    if (part.startsWith("**") && part.endsWith("**") && part.length > 4) {
      return [
        <strong key={index} className="font-semibold">
          {ids(part.slice(2, -2), index)}
        </strong>,
      ];
    }
    return ids(part, index);
  });
}

function ids(text: string, key: number): ReactNode[] {
  return text.split(/\b([A-Z]{2,4}-\d{3,4})\b/g).map((part, index) =>
    index % 2 === 1 ? (
      <span key={`${key}-${index}`} className="font-mono text-[0.92em]">
        {part}
      </span>
    ) : (
      <Fragment key={`${key}-${index}`}>{part}</Fragment>
    ),
  );
}
