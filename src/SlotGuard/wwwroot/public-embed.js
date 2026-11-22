(() => {
  if (!document.body.classList.contains("embed-mode") || window.parent === window) return;
  const configured = new URLSearchParams(location.search).get("origin");
  let target;
  try { target = configured ? new URL(configured).origin : location.origin; }
  catch { return; }
  if (target === "null") return;

  const main = document.querySelector(".public-main");
  let last = 0;
  let pending = 0;
  const measure = () => {
    if (pending) return;
    pending = requestAnimationFrame(() => {
      pending = 0;
      const overlays = [...document.querySelectorAll("dialog[open]")].map(dialog => dialog.scrollHeight + 34);
      const height = Math.ceil(Math.max(main.offsetHeight, ...overlays, 220));
      if (height === last) return;
      last = height;
      window.parent.postMessage({ type: "slotguard:resize:v1", height }, target);
    });
  };
  if (typeof ResizeObserver === "function") new ResizeObserver(measure).observe(main);
  new MutationObserver(measure).observe(document.body, { attributes: true, attributeFilter: ["open"], childList: true, subtree: true });
  window.addEventListener("resize", measure);
  window.addEventListener("message", event => {
    if (event.source !== window.parent || event.origin !== target || event.data?.type !== "slotguard:measure:v1") return;
    last = 0;
    measure();
  });
  document.fonts?.ready.then(measure);
  measure();
})();
