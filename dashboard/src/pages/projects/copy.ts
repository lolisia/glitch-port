/**
 * Copies text to the clipboard without ever throwing.
 *
 * The async Clipboard API needs a secure context (and a user gesture), so the
 * dashboard falls back to the deprecated textarea + execCommand path used by
 * older browsers and by embedded webviews. The boolean result lets the caller
 * show feedback without treating a refused copy as a failed action.
 */
export async function copyToClipboard(text: string): Promise<boolean> {
  try {
    const clipboard =
      typeof navigator === 'undefined' ? undefined : navigator.clipboard;

    if (clipboard !== undefined && typeof clipboard.writeText === 'function') {
      await clipboard.writeText(text);
      return true;
    }
  } catch {
    // Permission denied or an insecure context; try the legacy fallback.
  }

  return copyWithTextarea(text);
}

function copyWithTextarea(text: string): boolean {
  if (typeof document === 'undefined') {
    return false;
  }

  const textarea = document.createElement('textarea');

  try {
    textarea.value = text;
    textarea.setAttribute('readonly', '');
    // Keep the helper invisible and outside the scroll area, which also stops
    // mobile browsers from scrolling to it.
    textarea.style.position = 'fixed';
    textarea.style.top = '0';
    textarea.style.left = '0';
    textarea.style.opacity = '0';
    textarea.style.pointerEvents = 'none';

    document.body.appendChild(textarea);
    textarea.select();
    textarea.setSelectionRange(0, textarea.value.length);

    if (typeof document.execCommand !== 'function') {
      return false;
    }

    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    textarea.remove();
  }
}
