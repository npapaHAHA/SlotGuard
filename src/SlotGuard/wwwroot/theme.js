(() => {
  const key = "slotguard.theme";
  const system = matchMedia("(prefers-color-scheme: dark)");
  const root = document.documentElement;
  const toolbarColor = document.querySelector('meta[name="theme-color"]');
  const preferredTheme = value => value === "dark" || value === "light" ? value : null;
  let preference;
  try { preference = preferredTheme(localStorage.getItem(key)); } catch { /* The system theme still works. */ }

  function applyTheme() {
    const dark = (preference ?? (system.matches ? "dark" : "light")) === "dark";
    root.dataset.theme = dark ? "dark" : "light";
    if (toolbarColor) toolbarColor.content = dark ? "#101a17" : "#eef3ec";
    for (const control of document.querySelectorAll("[data-theme-toggle]")) {
      control.hidden = false;
      control.setAttribute("aria-pressed", String(dark));
      control.title = dark ? "Включить светлую тему" : "Включить тёмную тему";
    }
  }

  applyTheme();
  document.addEventListener("DOMContentLoaded", applyTheme, { once: true });
  document.addEventListener("click", event => {
    if (!event.target.closest("[data-theme-toggle]")) return;
    preference = root.dataset.theme === "dark" ? "light" : "dark";
    try { localStorage.setItem(key, preference); } catch { /* Keep the choice for this page. */ }
    applyTheme();
  });
  system.addEventListener("change", () => { if (!preference) applyTheme(); });
  window.addEventListener("storage", event => {
    if (event.key !== key && event.key !== null) return;
    preference = preferredTheme(event.newValue);
    applyTheme();
  });
})();
