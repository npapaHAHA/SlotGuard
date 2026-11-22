(() => {
  const source = document.currentScript?.src;
  if (!source) return;
  const widgetOrigin = new URL(source).origin;

  function sourceOf(frame) {
    if (!frame.matches("iframe[data-slotguard-embed]") || !frame.getAttribute("src")) return null;
    const url = new URL(frame.src);
    return url.origin === widgetOrigin && /^\/embed\/[a-f\d-]{32,36}\/?$/i.test(url.pathname) ? url : null;
  }

  function measure(frame) {
    const url = sourceOf(frame);
    if (url) frame.contentWindow?.postMessage({ type: "slotguard:measure:v1" }, url.origin);
  }

  window.addEventListener("message", event => {
    const { type, height } = event.data ?? {};
    if (event.origin !== widgetOrigin || type !== "slotguard:resize:v1" || !Number.isFinite(height)) return;
    for (const frame of document.querySelectorAll("iframe[data-slotguard-embed]")) {
      if (frame.contentWindow !== event.source || !sourceOf(frame)) continue;
      frame.height = String(Math.min(4000, Math.max(220, Math.ceil(height))));
      break;
    }
  });
  document.addEventListener("load", event => {
    if (event.target instanceof HTMLIFrameElement) measure(event.target);
  }, true);
  for (const frame of document.querySelectorAll("iframe[data-slotguard-embed]")) measure(frame);
})();
