// Lightweight alert / confirm / prompt + image lightbox, implemented as
// React components but exposed through a Promise-returning façade so the
// existing imperative call sites keep working unchanged.
//
// Each helper creates an isolated React root under #modal-root, renders
// the component, and resolves the returned Promise (and unmounts) when
// the user closes the modal.

import { createRoot, type Root } from 'react-dom/client';
import {
  useEffect,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
  type ReactNode,
} from 'react';

// ===== Mount helper =====
//
// `render` is called with a `close(value)` callback. The component the
// caller returns owns its own UI and decides when to call close. We
// schedule the unmount via setTimeout(0) so the close handler can finish
// returning before React tears down its tree (avoids React's "unmount
// inside an event handler" warning when called synchronously).

type CloseCallback<T> = (value: T | null) => void;

function mountModal<T>(
  render: (close: CloseCallback<T>) => ReactNode,
): Promise<T | null> {
  const root = document.getElementById('modal-root');
  if (!root) return Promise.resolve(null);

  return new Promise<T | null>((resolve) => {
    const container = document.createElement('div');
    root.appendChild(container);
    const reactRoot: Root = createRoot(container);

    let closed = false;
    const close: CloseCallback<T> = (value) => {
      if (closed) return;
      closed = true;
      setTimeout(() => {
        reactRoot.unmount();
        if (container.parentNode) container.parentNode.removeChild(container);
      }, 0);
      resolve(value);
    };

    reactRoot.render(render(close));
  });
}

// ===== Backdrop wrapper =====

interface BackdropProps {
  onBackdropClick: () => void;
  children: ReactNode;
}

function ModalBackdrop({ onBackdropClick, children }: BackdropProps) {
  return (
    <div
      className="modal-backdrop"
      onClick={(e) => {
        if (e.target === e.currentTarget) onBackdropClick();
      }}
    >
      <div className="modal">{children}</div>
    </div>
  );
}

// ===== Alert =====

interface AlertProps {
  title: string;
  message: string;
  onClose: () => void;
}

function AlertView({ title, message, onClose }: AlertProps) {
  const okRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    okRef.current?.focus();
  }, []);
  return (
    <ModalBackdrop onBackdropClick={onClose}>
      <h3>{title}</h3>
      <p>{message}</p>
      <div className="actions">
        <button ref={okRef} className="primary" onClick={onClose}>
          OK
        </button>
      </div>
    </ModalBackdrop>
  );
}

export function alertModal(
  title: string,
  message: string,
): Promise<boolean | null> {
  return mountModal<boolean>((close) => (
    <AlertView title={title} message={message} onClose={() => close(true)} />
  ));
}

// ===== Confirm =====

interface ConfirmProps {
  title: string;
  message: string;
  onResult: (ok: boolean) => void;
  onBackdrop: () => void;
}

function ConfirmView({ title, message, onResult, onBackdrop }: ConfirmProps) {
  const okRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    okRef.current?.focus();
  }, []);
  return (
    <ModalBackdrop onBackdropClick={onBackdrop}>
      <h3>{title}</h3>
      <p>{message}</p>
      <div className="actions">
        <button onClick={() => onResult(false)}>Cancel</button>
        <button ref={okRef} className="primary" onClick={() => onResult(true)}>
          OK
        </button>
      </div>
    </ModalBackdrop>
  );
}

export function confirmModal(
  title: string,
  message: string,
): Promise<boolean | null> {
  return mountModal<boolean>((close) => (
    <ConfirmView
      title={title}
      message={message}
      onResult={(ok) => close(ok)}
      onBackdrop={() => close(null)}
    />
  ));
}

// ===== Prompt =====

interface PromptProps {
  title: string;
  message: string;
  defaultValue: string;
  onResult: (value: string | null) => void;
}

function PromptView({ title, message, defaultValue, onResult }: PromptProps) {
  const [value, setValue] = useState(defaultValue);
  const inputRef = useRef<HTMLInputElement>(null);
  useEffect(() => {
    const el = inputRef.current;
    if (el) {
      el.focus();
      el.select();
    }
  }, []);

  const submit = (e?: FormEvent) => {
    e?.preventDefault();
    onResult(value);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Escape') onResult(null);
  };

  return (
    <ModalBackdrop onBackdropClick={() => onResult(null)}>
      <h3>{title}</h3>
      <p>{message}</p>
      <form onSubmit={submit}>
        <input
          ref={inputRef}
          type="text"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={onKeyDown}
        />
        <div className="actions">
          <button type="button" onClick={() => onResult(null)}>
            Cancel
          </button>
          <button type="submit" className="primary">
            OK
          </button>
        </div>
      </form>
    </ModalBackdrop>
  );
}

export function promptModal(
  title: string,
  message: string,
  defaultValue = '',
): Promise<string | null> {
  return mountModal<string>((close) => (
    <PromptView
      title={title}
      message={message}
      defaultValue={defaultValue}
      onResult={(v) => close(v)}
    />
  ));
}

// ===== Image lightbox =====
//
// Keeps the original behaviour: click on the image itself does NOT
// dismiss; only clicks on the surrounding backdrop or the close button.
// Escape also closes. Returns void since callers don't await it.

interface LightboxProps {
  url: string;
  onClose: () => void;
}

function Lightbox({ url, onClose }: LightboxProps) {
  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div
      className="lightbox-backdrop"
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <img src={url} alt="" onClick={(e) => e.stopPropagation()} />
      <button
        className="lightbox-close"
        type="button"
        aria-label="Close"
        onClick={onClose}
      >
        ✕
      </button>
    </div>
  );
}

export function openImageLightbox(url: string): void {
  const root = document.getElementById('modal-root');
  if (!root) return;
  const container = document.createElement('div');
  root.appendChild(container);
  const reactRoot: Root = createRoot(container);

  let closed = false;
  const close = () => {
    if (closed) return;
    closed = true;
    setTimeout(() => {
      reactRoot.unmount();
      if (container.parentNode) container.parentNode.removeChild(container);
    }, 0);
  };

  reactRoot.render(<Lightbox url={url} onClose={close} />);
}
