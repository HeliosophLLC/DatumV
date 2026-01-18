// Lightweight alert/confirm/prompt + image lightbox. Mounted under the
// #modal-root element in index.html. No state outside the DOM — every
// open() returns a Promise that resolves on close.

type ModalBuilder<T> = (modal: HTMLDivElement, close: (value: T) => void) => void;

export function showModal<T>(buildContent: ModalBuilder<T>): Promise<T | null> {
  return new Promise<T | null>((resolve) => {
    const root = document.getElementById('modal-root');
    if (!root) {
      resolve(null);
      return;
    }
    const backdrop = document.createElement('div');
    backdrop.className = 'modal-backdrop';
    const modal = document.createElement('div');
    modal.className = 'modal';
    backdrop.appendChild(modal);
    root.appendChild(backdrop);
    const close = (value: T | null): void => {
      root.removeChild(backdrop);
      resolve(value);
    };
    buildContent(modal, close as (value: T) => void);
    backdrop.addEventListener('click', (e) => {
      if (e.target === backdrop) close(null);
    });
  });
}

export function alertModal(title: string, message: string): Promise<boolean | null> {
  return showModal<boolean>((modal, close) => {
    const h = document.createElement('h3'); h.textContent = title; modal.appendChild(h);
    const p = document.createElement('p'); p.textContent = message; modal.appendChild(p);
    const actions = document.createElement('div'); actions.className = 'actions';
    const ok = document.createElement('button'); ok.className = 'primary'; ok.textContent = 'OK';
    ok.addEventListener('click', () => close(true));
    actions.appendChild(ok); modal.appendChild(actions); ok.focus();
  });
}

export function confirmModal(title: string, message: string): Promise<boolean | null> {
  return showModal<boolean>((modal, close) => {
    const h = document.createElement('h3'); h.textContent = title; modal.appendChild(h);
    const p = document.createElement('p'); p.textContent = message; modal.appendChild(p);
    const actions = document.createElement('div'); actions.className = 'actions';
    const cancel = document.createElement('button'); cancel.textContent = 'Cancel';
    cancel.addEventListener('click', () => close(false));
    const ok = document.createElement('button'); ok.className = 'primary'; ok.textContent = 'OK';
    ok.addEventListener('click', () => close(true));
    actions.appendChild(cancel); actions.appendChild(ok); modal.appendChild(actions);
    ok.focus();
  });
}

export function promptModal(
  title: string,
  message: string,
  defaultValue = '',
): Promise<string | null> {
  return showModal<string>((modal, close) => {
    const h = document.createElement('h3'); h.textContent = title; modal.appendChild(h);
    const p = document.createElement('p'); p.textContent = message; modal.appendChild(p);
    const input = document.createElement('input'); input.type = 'text'; input.value = defaultValue;
    modal.appendChild(input);
    const actions = document.createElement('div'); actions.className = 'actions';
    const cancel = document.createElement('button'); cancel.textContent = 'Cancel';
    cancel.addEventListener('click', () => close(null as unknown as string));
    const ok = document.createElement('button'); ok.className = 'primary'; ok.textContent = 'OK';
    ok.addEventListener('click', () => close(input.value));
    actions.appendChild(cancel); actions.appendChild(ok); modal.appendChild(actions);
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') close(input.value);
      if (e.key === 'Escape') close(null as unknown as string);
    });
    input.focus(); input.select();
  });
}

// Image lightbox. Built ad-hoc rather than via showModal so the backdrop
// can swallow clicks anywhere outside the image (the standard modal only
// closes when clicking the backdrop element directly, which would conflict
// with the close-button positioning). Restores body scroll and removes the
// Escape listener on close so multiple opens don't stack handlers.
export function openImageLightbox(url: string): void {
  const root = document.getElementById('modal-root');
  if (!root) return;
  const backdrop = document.createElement('div');
  backdrop.className = 'lightbox-backdrop';
  const img = document.createElement('img');
  img.src = url; img.alt = '';
  const close = document.createElement('button');
  close.className = 'lightbox-close';
  close.type = 'button';
  close.setAttribute('aria-label', 'Close');
  close.textContent = '✕';
  backdrop.appendChild(img);
  backdrop.appendChild(close);
  root.appendChild(backdrop);

  const dismiss = (): void => {
    document.removeEventListener('keydown', onKey);
    if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
  };
  const onKey = (e: KeyboardEvent): void => { if (e.key === 'Escape') dismiss(); };
  backdrop.addEventListener('click', (e) => {
    if (e.target === backdrop || e.target === close) dismiss();
  });
  img.addEventListener('click', (e) => e.stopPropagation());
  document.addEventListener('keydown', onKey);
}
