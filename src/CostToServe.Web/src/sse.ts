/** A server-sent event: the event name and its (joined) data lines. */
export interface SseEvent {
  event: string;
  data: string;
}

/**
 * Incremental parser for the text/event-stream format. Feed decoded chunks with push();
 * events are emitted when a blank line ends them. Handles chunks split mid-line and CRLF.
 */
export function createSseParser(onEvent: (e: SseEvent) => void) {
  let buffer = "";
  let event = "message";
  let data: string[] = [];

  const dispatch = () => {
    if (data.length > 0) {
      onEvent({ event, data: data.join("\n") });
    }
    event = "message";
    data = [];
  };

  return {
    push(chunk: string) {
      buffer += chunk;
      for (;;) {
        const newline = buffer.indexOf("\n");
        if (newline < 0) {
          return;
        }
        let line = buffer.slice(0, newline);
        buffer = buffer.slice(newline + 1);
        if (line.endsWith("\r")) {
          line = line.slice(0, -1);
        }
        if (line === "") {
          dispatch();
          continue;
        }
        if (line.startsWith(":")) {
          continue;
        }
        const colon = line.indexOf(":");
        const field = colon < 0 ? line : line.slice(0, colon);
        let value = colon < 0 ? "" : line.slice(colon + 1);
        if (value.startsWith(" ")) {
          value = value.slice(1);
        }
        if (field === "event") {
          event = value;
        } else if (field === "data") {
          data.push(value);
        }
      }
    },
    end() {
      dispatch();
    },
  };
}
