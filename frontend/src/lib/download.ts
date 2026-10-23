/**
 * Starts the download of a signed URL. The API answers it with `Content-Disposition: attachment`,
 * so following the link turns into a download and the current page stays where it is.
 *
 * Exactly one navigation, through a temporary link: `window.open(url, "_blank", "noopener")`
 * always returns null (noopener), so a "fall back when the popup was blocked" check would start
 * the same download a second time in the current tab.
 */
export function openDownload(url: string) {
  const link = document.createElement("a");
  link.href = url;
  // Honoured for same-origin URLs only; cross-origin ones rely on the attachment header.
  link.download = "";
  link.rel = "noopener";
  link.hidden = true;
  document.body.appendChild(link);
  try {
    link.click();
  } finally {
    link.remove();
  }
}
