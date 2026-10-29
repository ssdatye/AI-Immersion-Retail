import type { ApiAuth } from "./auth";
import { createSseParser, type SseEvent } from "./sse";
import type { Persona } from "./types";

const PERSONA_KEY = "cts.persona";

/** Local development persona. Not authentication: Entra sign-in replaces this. */
export function loadPersona(): Persona {
  return localStorage.getItem(PERSONA_KEY) === "luis" ? "luis" : "dana";
}

export function savePersona(persona: Persona) {
  localStorage.setItem(PERSONA_KEY, persona);
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

async function failure(response: Response) {
  let message = `${response.status} ${response.statusText}`;
  try {
    const problem = await response.json();
    message = problem.detail ?? problem.title ?? message;
  } catch {
    // Not a problem-details body.
  }
  return new ApiError(response.status, message);
}

export async function api<T>(auth: ApiAuth, path: string, init?: { method?: string; body?: unknown; signal?: AbortSignal }): Promise<T> {
  const response = await fetch(path, {
    method: init?.method ?? "GET",
    headers: { ...(await auth.headers()), ...(init?.body !== undefined ? { "Content-Type": "application/json" } : {}) },
    body: init?.body !== undefined ? JSON.stringify(init.body) : undefined,
    signal: init?.signal,
  });
  if (!response.ok) {
    throw await failure(response);
  }
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

/** Streams a chat turn. Resolves when the server sends "done" or the stream ends. */
export async function streamChat(
  auth: ApiAuth,
  message: string,
  conversationId: string | null,
  onEvent: (event: string, data: Record<string, unknown>) => void,
  signal?: AbortSignal,
) {
  const response = await fetch("/api/chat", {
    method: "POST",
    headers: { ...(await auth.headers()), "Content-Type": "application/json" },
    body: JSON.stringify({ conversationId, message }),
    signal,
  });
  if (!response.ok || !response.body) {
    throw await failure(response);
  }

  const parser = createSseParser((e: SseEvent) => onEvent(e.event, JSON.parse(e.data)));
  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  for (;;) {
    const { value, done } = await reader.read();
    if (done) {
      break;
    }
    parser.push(value);
  }
  parser.end();
}
