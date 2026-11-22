"use strict";
const form = document.getElementById("settings");
const status = document.getElementById("status");
function render(origin) {
  form.elements.address.value = origin;
  for (const link of document.querySelectorAll("[data-path]")) link.href = origin + link.dataset.path;
}
chrome.storage.local.get({ origin: "http://127.0.0.1:5090" }).then(data => render(data.origin));
form.addEventListener("submit", async event => {
  event.preventDefault();
  try {
    const url = new URL(form.elements.address.value);
    if (!["http:", "https:"].includes(url.protocol) || url.username || url.password) throw new Error();
    await chrome.storage.local.set({ origin: url.origin });
    render(url.origin);
    status.textContent = "Адрес сохранён.";
  } catch { status.textContent = "Введите HTTP- или HTTPS-адрес."; }
});
