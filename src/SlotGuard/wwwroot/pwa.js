"use strict";
const installButton = document.getElementById("p3-install");
let installEvent;
if ("serviceWorker" in navigator) window.addEventListener("load", () =>
  navigator.serviceWorker.register("/service-worker.js").catch(() => {}));
window.addEventListener("beforeinstallprompt", event => {
  event.preventDefault();
  installEvent = event;
  if (installButton) installButton.hidden = false;
});
installButton?.addEventListener("click", async () => {
  if (!installEvent) return;
  const prompt = installEvent;
  installEvent = null;
  installButton.hidden = true;
  await prompt.prompt();
});
