import { useEffect, useRef, useState } from "react";
import { api, ApiError, failureText, streamChat } from "../api";
import type { ApiAuth } from "../auth";
import { COMMAND_HELP, mentionedActions, parseCommand } from "../commands";
import { toPercent, usd } from "../format";
import type { WaitingItem } from "../pending";
import type { AnswerEvidence, ConfirmationCard, Playbook } from "../types";
import { EvidenceReceipt } from "./EvidenceReceipt";
import { when } from "./ActionsPanel";
import { DecisionStrip, pastTense, submitDecision } from "./DecisionStrip";

type CardStatus = "pending" | "confirmed" | "cancelled" | "closed";

interface Turn {
  id: number;
  /** "system" turns are command results produced without the model. */
  role: "user" | "agent" | "system";
  /** Actions to show decision buttons for (system turns). Agent turns use the IDs mentioned in their text. */
  actionIds?: string[];
  /** Region for those buttons; defaults to the selected decision region. */
  region?: string;
  text: string;
  tools: { name: string; outcome: string }[];
  cards: (ConfirmationCard & { status: CardStatus })[];
  traceId?: string;
  error?: string;
  streaming?: boolean;
  /** Live evidence for agent answers (numbers traced to tool results). */
  evidence?: AnswerEvidence;
}

interface Props {
  auth: ApiAuth;
  displayName: string;
  suggestions: string[];
  studioContext: string;
  playbook: Playbook | null;
  /** Actions waiting for this user, per region (proposals to decide or approvals to execute). */
  waiting: WaitingItem[];
  /** Region selected in Actions and decisions; used when a reply or command does not name one. */
  decisionRegion: string;
  onDecisionLogged: () => void;
  /** Called when an answer's evidence arrives, so the Trust Center can refresh. */
  onEvidence?: () => void;
  /** A question to send from outside the chat (for example "Ask the agent about these changes"); sent when the nonce changes. */
  askRequest?: { text: string; nonce: number } | null;
  /** Called when a question is sent with the current model settings attached as context. */
  onShared?: () => void;
  /** Opens the Trust Center tab. */
  onOpenTrust?: () => void;
}

export function ChatPanel({
  auth,
  displayName,
  suggestions,
  studioContext,
  playbook,
  waiting,
  decisionRegion,
  onDecisionLogged,
  onEvidence,
  askRequest,
  onShared,
  onOpenTrust,
}: Props) {
  const [turns, setTurns] = useState<Turn[]>([
    {
      id: 0,
      role: "agent",
      text: `Hi ${displayName}. Ask a what-if question. For fine adjustments without the agent, open ⚙ Fine-tune model. Every number I give comes from the cost engine. All data is illustrative.`,
      tools: [],
      cards: [],
    },
  ]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const [shareContext, setShareContext] = useState(true);
  const conversationId = useRef<string | null>(null);
  const nextId = useRef(1);
  const log = useRef<HTMLDivElement>(null);

  useEffect(() => {
    log.current?.scrollTo({ top: log.current.scrollHeight });
  }, [turns]);

  const update = (id: number, change: (t: Turn) => Turn) => setTurns((all) => all.map((t) => (t.id === id ? change(t) : t)));

  const addTurns = (...items: Omit<Turn, "id" | "tools" | "cards">[]) =>
    setTurns((all) => [...all, ...items.map((t) => ({ ...t, id: nextId.current++, tools: [], cards: [] }))]);

  /** Slash commands run directly against the decisions API with the same server checks; the model is not involved. */
  const runCommand = async (text: string) => {
    const command = parseCommand(text)!;
    switch (command.kind) {
      case "help":
        addTurns({ role: "user", text }, { role: "system", text: `Commands (run without the agent):\n${COMMAND_HELP.join("\n")}` });
        return;
      case "invalid":
        addTurns({ role: "user", text }, { role: "system", text: "", error: command.message });
        return;
      case "status": {
        const ids = command.actionId ? [command.actionId] : (playbook?.actions.map((a) => a.actionId) ?? []);
        const region = command.region ?? decisionRegion;
        addTurns({ role: "user", text }, { role: "system", text: `Status for ${region} and your next steps:`, actionIds: ids, region });
        return;
      }
      case "decision":
        setBusy(true);
        try {
          const region = command.region ?? decisionRegion;
          const r = await submitDecision(auth, command.actionId, command.decision, command.reason, command.setting, region);
          addTurns(
            { role: "user", text },
            {
              role: "system",
              text:
                r.status === "already_recorded" && r.message
                  ? `✓ ${r.message}`
                  : `✓ ${command.actionId} ${pastTense(command.decision).toLowerCase()}${
                      command.setting !== undefined ? ` at ${toPercent(command.setting)}%` : ""
                    } for ${region} by you${command.reason ? ` ("${command.reason}")` : ""}. Logged.`,
              actionIds: [command.actionId],
              region,
            },
          );
        } catch (e) {
          addTurns(
            { role: "user", text },
            { role: "system", text: e instanceof ApiError ? "Not logged." : "", error: failureText(e, "Check the status below."), actionIds: [command.actionId] },
          );
        } finally {
          setBusy(false);
          // Always reload: after an error or a dropped connection the server may already have moved on.
          onDecisionLogged();
        }
        return;
    }
  };

  const send = async (question: string, forceContext = false) => {
    const text = question.trim();
    if (!text || busy) {
      return;
    }
    setInput("");
    if (parseCommand(text)) {
      await runCommand(text);
      return;
    }
    setBusy(true);
    const userId = nextId.current++;
    const agentId = nextId.current++;
    setTurns((all) => [
      ...all,
      { id: userId, role: "user", text, tools: [], cards: [] },
      { id: agentId, role: "agent", text: "", tools: [], cards: [], streaming: true },
    ]);

    // Studio settings are shared as context, not instructions; the agent still uses tools for every number.
    const withContext = shareContext || forceContext;
    const message = withContext ? `${text}\n\n[Studio settings for reference: ${studioContext}]` : text;
    if (withContext) {
      onShared?.();
    }
    try {
      await streamChat(auth, message, conversationId.current, (event, data) => {
        switch (event) {
          case "start":
            conversationId.current = String(data.conversationId);
            break;
          case "delta":
            update(agentId, (t) => ({ ...t, text: t.text + String(data.text) }));
            break;
          case "tool":
            update(agentId, (t) => ({ ...t, tools: [...t.tools, { name: String(data.name), outcome: String(data.outcome) }] }));
            if ((data.name === "log_decision" || data.name === "propose_package") && data.outcome === "ok") {
              onDecisionLogged();
            }
            break;
          case "confirmation":
            update(agentId, (t) => ({ ...t, cards: [...t.cards, { ...(data as unknown as ConfirmationCard), status: "pending" }] }));
            break;
          case "confirmation_closed":
            update(agentId, (t) => ({ ...t, cards: t.cards.map((c) => (c.id === data.id && c.status === "pending" ? { ...c, status: "closed" } : c)) }));
            break;
          case "evidence":
            update(agentId, (t) => ({ ...t, evidence: data as unknown as AnswerEvidence }));
            onEvidence?.();
            break;
          case "error":
            update(agentId, (t) => ({ ...t, error: String(data.message) }));
            break;
          case "done":
            update(agentId, (t) => ({ ...t, traceId: data.traceId ? String(data.traceId) : undefined, streaming: false }));
            break;
        }
      });
    } catch (e) {
      update(agentId, (t) => ({ ...t, error: (e as Error).message }));
    } finally {
      update(agentId, (t) => ({ ...t, streaming: false }));
      setBusy(false);
    }
  };

  // Questions sent from outside the chat always carry the model settings: they are about those settings.
  const lastNonce = useRef(0);
  useEffect(() => {
    if (askRequest && askRequest.nonce !== lastNonce.current) {
      lastNonce.current = askRequest.nonce;
      void send(askRequest.text, true);
    }
  }, [askRequest]);

  const resolve = async (turnId: number, card: ConfirmationCard, confirm: boolean) => {
    update(turnId, (t) => ({ ...t, cards: t.cards.map((c) => (c.id === card.id ? { ...c, status: confirm ? "confirmed" : "cancelled" } : c)) }));
    try {
      await api(auth, `/api/confirmations/${card.id}`, { method: "POST", body: { confirm } });
    } catch {
      update(turnId, (t) => ({ ...t, cards: t.cards.map((c) => (c.id === card.id ? { ...c, status: "closed" } : c)) }));
    }
  };

  return (
    <section className="panel chat">
      <h2>Ask the agent</h2>
      {waiting.length > 0 && (
        <div className="waiting-banner">
          <div className="waiting-title">🔔 Waiting for you</div>
          {waiting.map((a) => (
            <div key={a.actionId} className="muted small">
              {a.actionId} {a.action} · <strong>{a.region}</strong> · {a.status.toLowerCase()}
              {a.setting ? ` at ${toPercent(a.setting.value)}%` : ""}
              {a.latestDecisionBy ? ` by ${a.latestDecisionBy}` : ""}
              {a.latestDecisionAt ? ` · ${when(a.latestDecisionAt)}` : ""}
            </div>
          ))}
          <DecisionStrip
            auth={auth}
            items={waiting.map((a) => ({ actionId: a.actionId, region: a.region }))}
            playbook={playbook}
            showRegion
            onDecided={onDecisionLogged}
          />
        </div>
      )}
      <div className="chat-log" ref={log} role="log" aria-live="polite" aria-relevant="additions" aria-label="Conversation with the agent" tabIndex={0}>
        {turns.map((t) => (
          <div key={t.id} className={`bubble ${t.role}`}>
            <div className="who">{t.role === "user" ? displayName : t.role === "system" ? "Studio (no agent)" : "Agent"}</div>
            {t.tools.length > 0 && (
              <div className="tools">
                {t.tools.map((tool, i) => (
                  <span key={i} className={`tool ${tool.outcome === "ok" ? "ok" : "denied"}`} title={`Tool outcome: ${tool.outcome}`}>
                    ⚙ {tool.name}
                    {tool.outcome !== "ok" ? ` · ${tool.outcome}` : ""}
                  </span>
                ))}
              </div>
            )}
            {t.cards.map((c) => (
              <div key={c.id} className={`confirm-card ${c.status}`}>
                <div className="confirm-title">
                  Confirm: {c.actor} → {c.decision.toUpperCase()} {c.actionId}
                </div>
                <div>
                  {c.actionName}
                  {c.region ? ` · for ${c.region}` : ""}
                </div>
                {c.setting && (
                  <div>
                    {/^P-/.test(c.actionId) ? "Includes" : "Setting"}: {c.setting}
                  </div>
                )}
                {c.reason && <div className="muted">Reason: {c.reason}</div>}
                {c.estimatedWeeklySavingUsd != null && <div className="muted">Modeled weekly saving: {usd(c.estimatedWeeklySavingUsd, 0)}</div>}
                {c.guardrailNotes.map((n) => (
                  <div key={n} className="muted">
                    Guardrail: {n}
                  </div>
                ))}
                {c.status === "pending" ? (
                  <div className="confirm-buttons">
                    <button className="primary" onClick={() => resolve(t.id, c, true)}>
                      Confirm
                    </button>
                    <button className="secondary" onClick={() => resolve(t.id, c, false)}>
                      Cancel
                    </button>
                  </div>
                ) : (
                  <div className="confirm-status">{c.status === "confirmed" ? "Confirmed by you" : c.status === "cancelled" ? "Cancelled" : "Closed"}</div>
                )}
              </div>
            ))}
            {t.text && <div className="text">{t.text}</div>}
            {t.streaming && !t.text && t.cards.every((c) => c.status !== "pending") && <div className="muted">Thinking…</div>}
            {t.error && <div className="error">{t.error}</div>}
            {(t.role === "system" || (t.role === "agent" && !t.streaming && t.id !== 0)) && (
              <DecisionStrip
                auth={auth}
                items={(t.actionIds ?? mentionedActions(t.text)).map((actionId) => ({ actionId, region: t.region ?? decisionRegion }))}
                showRegion
                playbook={playbook}
                onDecided={onDecisionLogged}
              />
            )}
            {t.evidence && !t.streaming && <EvidenceReceipt evidence={t.evidence} onOpenTrust={onOpenTrust} />}
            {t.traceId && !t.evidence && <div className="trace">trace {t.traceId}</div>}
          </div>
        ))}
      </div>
      <div className="chips">
        {suggestions.map((s) => (
          <button key={s} className="chip" disabled={busy} onClick={() => send(s)}>
            {s}
          </button>
        ))}
      </div>
      <form
        className="chat-input"
        onSubmit={(e) => {
          e.preventDefault();
          void send(input);
        }}
      >
        <label className="sr-only" htmlFor="chat-input">
          Ask the agent
        </label>
        <input
          id="chat-input"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Ask a what-if question, or type /help for commands"
          maxLength={1500}
          disabled={busy}
        />
        <button className="primary" disabled={busy || !input.trim()}>
          Send
        </button>
      </form>
      <label className="check small">
        <input type="checkbox" checked={shareContext} onChange={(e) => setShareContext(e.target.checked)} /> Share the model settings with the agent
      </label>
    </section>
  );
}
