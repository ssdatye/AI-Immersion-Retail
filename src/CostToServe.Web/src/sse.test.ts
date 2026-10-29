import { describe, expect, it } from "vitest";
import { usd, usdCompact, pct } from "./format";
import { createSseParser, type SseEvent } from "./sse";

describe("createSseParser", () => {
  const parse = (chunks: string[]) => {
    const events: SseEvent[] = [];
    const parser = createSseParser((e) => events.push(e));
    chunks.forEach((c) => parser.push(c));
    parser.end();
    return events;
  };

  it("parses named events", () => {
    expect(parse(['event: delta\ndata: {"text":"Hi"}\n\n'])).toEqual([{ event: "delta", data: '{"text":"Hi"}' }]);
  });

  it("handles chunks split mid-line and CRLF line endings", () => {
    const events = parse(["event: to", "ol\r\nda", 'ta: {"name":"run_scenario"}\r', "\n\r\nevent: done\ndata: {}\n\n"]);
    expect(events).toEqual([
      { event: "tool", data: '{"name":"run_scenario"}' },
      { event: "done", data: "{}" },
    ]);
  });

  it("joins multi-line data and ignores comments", () => {
    expect(parse([": keep-alive\ndata: a\ndata: b\n\n"])).toEqual([{ event: "message", data: "a\nb" }]);
  });

  it("dispatches a final event without a trailing blank line on end", () => {
    expect(parse(["event: done\ndata: {}\n"])).toEqual([{ event: "done", data: "{}" }]);
  });
});

describe("format", () => {
  it("formats currency and compact currency", () => {
    expect(usd(17.0091)).toBe("$17.01");
    expect(usdCompact(28234417)).toBe("$28.2M");
    expect(usdCompact(-3353240)).toBe("-$3.4M");
    expect(usdCompact(344596)).toBe("$344.6K");
    expect(pct(88.12)).toBe("88.1%");
    expect(usd(null)).toBe("-");
  });
});
