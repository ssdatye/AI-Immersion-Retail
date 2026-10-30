import { useEffect, useId, useRef, useState } from "react";
import { api } from "../api";
import type { ApiAuth } from "../auth";
import { leverList } from "../modelSync";
import type { Levers } from "../types";

interface PackageResult {
  status: string;
  actionId?: string;
  name?: string;
  message?: string;
}

interface Props {
  auth: ApiAuth;
  region: string;
  levers: Levers;
  onProposed: (id: string) => void;
  onOpen: (id: string) => void;
}

/** Regional ops bundles the levers they tuned into one named action that Finance decides as a whole. */
export function PackageForm({ auth, region, levers, onProposed, onOpen }: Props) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState(`${region} fuel response`);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<{ id: string; name: string } | null>(null);
  const nameId = useId();
  const reasonId = useId();
  const items = leverList(levers);
  const nameRef = useRef<HTMLInputElement>(null);
  const openRef = useRef<HTMLButtonElement>(null);
  const proposeRef = useRef<HTMLButtonElement>(null);
  const used = useRef(false);

  // Keyboard focus follows the form: into the name when it opens, then back to the Decisions link or the button.
  useEffect(() => {
    if (open) {
      used.current = true;
      nameRef.current?.focus();
    } else if (used.current) {
      (openRef.current ?? proposeRef.current)?.focus();
    }
  }, [open]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const r = await api<PackageResult>(auth, "/api/packages", { method: "POST", body: { name: name.trim(), reason: reason.trim() || null, levers } });
      if (r.status === "recorded" && r.actionId) {
        setDone({ id: r.actionId, name: r.name ?? name.trim() });
        setOpen(false);
        setReason("");
        onProposed(r.actionId);
      } else {
        setError(r.message ?? "The package was not created.");
      }
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  if (!open) {
    return (
      <div className="package-bar">
        {done && (
          <p className="package-done" role="status">
            ✓ <strong>{done.id}</strong> “{done.name}” proposed to Finance.{" "}
            <button type="button" className="link" ref={openRef} onClick={() => onOpen(done.id)}>
              See it in Decisions
            </button>
          </p>
        )}
        {items.length > 0 && (
          <button
            type="button"
            ref={proposeRef}
            onClick={() => {
              setDone(null);
              setError(null);
              setOpen(true);
            }}
          >
            📦 Propose {items.length === 1 ? "this lever" : `these ${items.length} levers`} as a package
          </button>
        )}
      </div>
    );
  }

  return (
    <form className="package-form" onSubmit={submit} aria-label="Propose a custom package">
      <strong>📦 New custom package · {region}</strong>
      <label htmlFor={nameId}>Name</label>
      <input id={nameId} ref={nameRef} value={name} maxLength={60} required onChange={(e) => setName(e.target.value)} />
      <span className="small muted">Includes:</span>
      <ul>
        {items.map((i) => (
          <li key={i}>{i}</li>
        ))}
      </ul>
      <label htmlFor={reasonId}>Why (optional)</label>
      <input id={reasonId} value={reason} maxLength={300} onChange={(e) => setReason(e.target.value)} placeholder="For example: offsets the diesel spike in two weeks" />
      <p className="small muted">
        Applies to {region} only. Finance approves or rejects it as one decision; you execute it once approved. The diesel scenario is not part of
        the package.
      </p>
      {error && (
        <p className="error small" role="alert">
          {error}
        </p>
      )}
      <div className="drawer-foot-buttons">
        <button type="submit" className="primary" disabled={busy || !name.trim()}>
          {busy ? "Proposing…" : "Propose to Finance"}
        </button>
        <button type="button" onClick={() => setOpen(false)} disabled={busy}>
          Cancel
        </button>
      </div>
    </form>
  );
}
