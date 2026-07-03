import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { X } from 'lucide-react';
import { cn } from '@/lib/utils';

// A clickable <img> that expands to a full-screen preview when clicked.
// The overlay renders via a React Portal so it floats above every scroll
// container / z-index, and its top edge is inset by `--app-titlebar-h`
// so the OS title-bar controls (minimize / maximize / close) stay
// interactive while the preview is open — the same inset trick
// MediaPreview uses. Click anywhere or press Escape to dismiss.

export function LightboxImage({
  src,
  alt,
  className,
  ...rest
}: React.ImgHTMLAttributes<HTMLImageElement>) {
  const { t } = useTranslation('common');
  const [open, setOpen] = useState(false);

  // Without a source there's nothing to expand — render a plain image.
  if (typeof src !== 'string' || src.length === 0) {
    return <img src={src} alt={alt} className={className} {...rest} />;
  }

  return (
    <>
      <img
        {...rest}
        src={src}
        alt={alt}
        onClick={() => setOpen(true)}
        title={rest.title ?? t('image.expand')}
        className={cn('cursor-zoom-in', className)}
      />
      {open && (
        <Lightbox src={src} alt={alt} onClose={() => setOpen(false)} />
      )}
    </>
  );
}

function Lightbox({
  src,
  alt,
  onClose,
}: {
  src: string;
  alt?: string;
  onClose: () => void;
}) {
  const { t } = useTranslation('common');

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return createPortal(
    <div
      role="dialog"
      aria-modal="true"
      onClick={onClose}
      style={{ top: 'var(--app-titlebar-h, 32px)' }}
      className="fixed inset-x-0 bottom-0 z-50 flex items-center justify-center bg-black/80 p-6"
    >
      <img
        src={src}
        alt={alt ?? ''}
        // Clicking the image closes too (cursor-zoom-out); there's
        // nothing to interact with inside the preview.
        className="max-h-full max-w-full cursor-zoom-out object-contain"
      />
      <button
        type="button"
        onClick={onClose}
        aria-label={t('image.close')}
        className="text-white/80 hover:bg-red-600 hover:text-white absolute right-4 top-4 cursor-pointer rounded-xs p-1 transition-colors"
      >
        <X className="size-5" />
      </button>
    </div>,
    document.body,
  );
}
