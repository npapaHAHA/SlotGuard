function editEmbedSettings(resource) {
  const organization = state.organizationId;
  return run(async () => {
    const address = `/api/workspace/resources/${encodeURIComponent(resource.id)}/embed`;
    const model = await api(address, undefined, organization);
    openEmbedSettings(resource, address, model, organization);
  });
}

function embedWidgetUrl(publicLinkId, origin) {
  const url = new URL(`/embed/${publicLinkId}`, location.origin);
  if (origin) url.searchParams.set("origin", origin);
  return url;
}

function embedSnippet(publicLinkId, origin) {
  const attr = value => String(value).replaceAll("&", "&amp;").replaceAll('"', "&quot;");
  const url = embedWidgetUrl(publicLinkId, origin);
  const full = new URL(`/book/${publicLinkId}`, url.origin);
  return [
    `<iframe data-slotguard-embed src="${attr(url)}"`,
    '  title="Бронирование в SlotGuard" width="100%" height="720"',
    '  loading="lazy" referrerpolicy="no-referrer" style="border:0"></iframe>',
    `<script src="${attr(new URL("/embed.js", url.origin))}" defer></script>`,
    `<a href="${attr(full)}" target="_blank" rel="noopener">Открыть страницу бронирования</a>`
  ].join("\n");
}

function openEmbedSettings(resource, address, model, organization) {
  const dialog = questionDialog("Виджет бронирования", resource.name);
  dialog.classList.add("embed-settings-dialog");
  const field = dialog.querySelector("[data-fields]");
  const form = dialog.querySelector("form");
  const save = dialog.querySelector("[data-save]");
  const close = dialog.querySelector("[data-close]");
  const message = dialog.querySelector("[data-message]");
  save.textContent = "Сохранить сайты";

  function note(text, error = false) {
    message.textContent = text;
    message.hidden = !text;
    message.classList.toggle("error", error);
  }

  if (!model.publicLinkId) {
    const enable = button("page-action", "Настроить публичную ссылку", () => {
      dialog.close();
      editPublicLink({ ...resource, publicLinkId: model.publicLinkId });
    });
    field.append(element("p", "embed-setting-help", "Включите публичную ссылку, чтобы добавить календарь на сайт."), enable);
    save.hidden = true;
    document.body.append(dialog);
    dialog.showModal();
    return;
  }

  field.className = "embed-setting-fields";
  field.innerHTML = `
    <label>Разрешённые сайты
      <textarea data-embed-origins rows="3" maxlength="3650" placeholder="https://coworking.example"></textarea>
    </label>
    <p class="embed-setting-help">Укажите адрес каждого сайта на отдельной строке, включая протокол и порт. Пустой список отключает виджет.</p>
    <div data-embed-code class="embed-setting-code" hidden>
      <label>Сайт<select data-embed-host></select></label>
      <label>HTML для вставки<textarea data-embed-snippet rows="5" readonly spellcheck="false"></textarea></label>
      <div class="embed-setting-actions">
        <button type="button" class="outline-action" data-copy-embed>Скопировать HTML</button>
        <a class="text-link" data-open-embed target="_blank" rel="noopener">Открыть виджет ↗</a>
      </div>
      <details data-embed-preview>
        <summary>Предпросмотр виджета</summary>
        <iframe data-slotguard-embed title="Предпросмотр виджета SlotGuard" width="100%" height="360" referrerpolicy="no-referrer"></iframe>
      </details>
    </div>`;
  const origins = field.querySelector("[data-embed-origins]");
  const code = field.querySelector("[data-embed-code]");
  const host = field.querySelector("[data-embed-host]");
  const snippet = field.querySelector("[data-embed-snippet]");
  const preview = field.querySelector("[data-embed-preview]");
  const frame = preview.querySelector("iframe");
  const full = field.querySelector("[data-open-embed]");
  let busy = false;

  function render(next) {
    model = next;
    const chosen = host.value;
    origins.value = model.origins.join("\n");
    host.replaceChildren(...model.origins.map(origin => new Option(origin, origin)));
    if (model.origins.includes(chosen)) host.value = chosen;
    code.hidden = !model.origins.length;
    full.href = embedWidgetUrl(model.publicLinkId);
    preview.open = false;
    frame.removeAttribute("src");
    refreshMarkup();
  }

  function refreshMarkup() {
    snippet.value = host.value ? embedSnippet(model.publicLinkId, host.value) : "";
  }

  host.addEventListener("change", refreshMarkup);
  preview.addEventListener("toggle", () => {
    if (preview.open && !frame.getAttribute("src")) frame.src = embedWidgetUrl(model.publicLinkId);
  });
  field.querySelector("[data-copy-embed]").addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(snippet.value);
      note("HTML скопирован.");
    } catch {
      snippet.select();
      note("HTML выделен. Скопируйте его из поля.");
    }
  });

  dialog.addEventListener("cancel", event => { if (busy) event.preventDefault(); });
  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (busy || organization !== state.organizationId) return;
    const values = origins.value.split(/\r?\n/).map(value => value.trim()).filter(Boolean);
    busy = save.disabled = close.disabled = origins.disabled = true;
    note("");
    try {
      const next = await api(address, jsonRequest("PUT", { origins: values }), organization);
      render(next);
      note(next.origins.length ? "Список сайтов сохранён." : "Виджет отключён.");
      await loadWorkspace();
    } catch (error) {
      note(error.message, true);
    } finally {
      busy = save.disabled = close.disabled = origins.disabled = false;
    }
  });

  render(model);
  document.body.append(dialog);
  dialog.showModal();
}
