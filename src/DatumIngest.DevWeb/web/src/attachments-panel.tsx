// Attachments drawer panel. Mounted once into #attachments-drawer;
// visibility is gated by attachmentsState.open. Lists the staged files
// and offers rename / remove actions plus a drop zone at the top for
// adding more without dragging onto the editor.

import { useState } from 'react';
import { useSnapshot } from 'valtio';
import {
  addAttachment,
  attachmentsState,
  formatSize,
  removeAttachment,
  renameAttachment,
} from './attachments.js';

export function AttachmentsPanel() {
  const snap = useSnapshot(attachmentsState);

  if (!snap.open) return null;

  return (
    <div className="attachments-panel">
      <div className="attachments-header">
        <span className="attachments-title">Attachments</span>
        <button
          type="button"
          className="attachments-close"
          title="Close"
          onClick={() => {
            attachmentsState.open = false;
          }}
        >
          ×
        </button>
      </div>

      <DropZone />

      {snap.items.length === 0 ? (
        <div className="attachments-empty">
          Drop a file anywhere on the editor or here to attach it.
          <br />
          Reference attached files from SQL with{' '}
          <code className="attachments-code">$name</code>.
        </div>
      ) : (
        <div className="attachments-list">
          {snap.items.map((a) => (
            <AttachmentRow key={a.id} id={a.id} />
          ))}
        </div>
      )}
    </div>
  );
}

// ===== Drop zone =====

function DropZone() {
  const [over, setOver] = useState(false);

  return (
    <label
      className={`attachments-drop ${over ? 'over' : ''}`}
      onDragEnter={(e) => {
        e.preventDefault();
        setOver(true);
      }}
      onDragOver={(e) => {
        e.preventDefault();
        setOver(true);
      }}
      onDragLeave={() => setOver(false)}
      onDrop={(e) => {
        e.preventDefault();
        setOver(false);
        const files = Array.from(e.dataTransfer.files);
        files.forEach((f) => addAttachment(f));
      }}
    >
      <input
        type="file"
        multiple
        style={{ display: 'none' }}
        onChange={(e) => {
          const files = Array.from(e.target.files ?? []);
          files.forEach((f) => addAttachment(f));
          e.target.value = '';
        }}
      />
      <span className="attachments-drop-label">
        Drop files here or click to browse
      </span>
    </label>
  );
}

// ===== One attachment row =====

function AttachmentRow({ id }: { id: string }) {
  // Read the row's *current* state from the proxy snapshot inside this
  // component; the parent's snapshot (via useSnapshot above) already
  // tracks proxy-level changes, but the row's local edit state needs
  // to live here so renaming one row doesn't re-render the whole list.
  const snap = useSnapshot(attachmentsState);
  const att = snap.items.find((a) => a.id === id);
  const [editing, setEditing] = useState(false);
  const [draftName, setDraftName] = useState('');
  const [error, setError] = useState<string | null>(null);

  if (!att) return null;

  const beginEdit = () => {
    setDraftName(att.name);
    setError(null);
    setEditing(true);
  };

  const commit = () => {
    if (draftName === att.name) {
      setEditing(false);
      return;
    }
    const ok = renameAttachment(id, draftName);
    if (!ok) {
      setError(
        'Name must be a SQL identifier and unique across attachments.',
      );
      return;
    }
    setEditing(false);
    setError(null);
  };

  return (
    <div className="attachment-row">
      <div className="attachment-row-main">
        {editing ? (
          <input
            className="attachment-name-input"
            type="text"
            value={draftName}
            autoFocus
            onChange={(e) => {
              setDraftName(e.target.value);
              setError(null);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') commit();
              else if (e.key === 'Escape') {
                setEditing(false);
                setError(null);
              }
            }}
            onBlur={commit}
          />
        ) : (
          <button
            type="button"
            className="attachment-name"
            title="Click to rename"
            onClick={beginEdit}
          >
            ${att.name}
          </button>
        )}
        <button
          type="button"
          className="attachment-remove"
          title="Remove"
          onClick={() => removeAttachment(id)}
        >
          ×
        </button>
      </div>
      <div className="attachment-meta">
        <span className="attachment-kind">{att.kind}</span>
        <span className="attachment-size">{formatSize(att.size)}</span>
        <span
          className="attachment-filename"
          title={att.originalFilename}
        >
          {att.originalFilename}
        </span>
      </div>
      {error && <div className="attachment-error">{error}</div>}
    </div>
  );
}
