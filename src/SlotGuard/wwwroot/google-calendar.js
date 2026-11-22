function editGoogleCalendar(resource) {
  const organization = state.organizationId;
  const path = `/api/workspace/resources/${encodeURIComponent(resource.id)}/google-calendar`;
  return run(async () => {
    if (document.querySelector(".google-calendar-dialog")) return;
    const model = await api(path, { cache: "no-store" }, organization);
    openGoogleCalendar(resource.name, model, {
      status: () => api(path, { cache: "no-store" }, organization),
      connect: calendarId => api(path + "/connect", jsonRequest("POST", { calendarId }), organization),
      disconnect: () => api(path, { method: "DELETE" }, organization)
    });
  });
}

function openGoogleCalendar(resourceName, initial, actions) {
  const dialog = document.createElement("dialog");
  dialog.className = "panel login-dialog google-calendar-dialog";
  dialog.setAttribute("aria-labelledby", "google-calendar-title");
  dialog.innerHTML = `
    <header class="google-calendar-heading">
      <div><h2 id="google-calendar-title">Google Calendar</h2><p data-resource></p></div>
      <button type="button" class="icon-button today-button" data-close aria-label="Закрыть">×</button>
    </header>
    <div class="google-calendar-status" data-status aria-live="polite"></div>
    <form class="workspace-form" data-connect>
      <label>ID календаря
        <input name="calendarId" required maxlength="254" placeholder="primary" autocapitalize="none" spellcheck="false">
      </label>
      <p>Найдите ID в настройках календаря Google или укажите <code>primary</code> для основного.</p>
      <button type="submit" class="page-action" data-submit>Подключить Google</button>
    </form>
    <p data-message class="form-message" role="status" hidden></p>
    <footer class="google-calendar-actions">
      <button type="button" class="outline-action" data-refresh>Обновить статус</button>
      <button type="button" class="outline-action" data-disconnect>Отключить</button>
    </footer>`;
  const node = name => dialog.querySelector(`[data-${name}]`);
  const connect = node("connect");
  const input = connect.elements.calendarId;
  let model = initial;
  let busy = false;
  node("resource").textContent = resourceName;
  input.value = model.calendarId ?? "";

  function message(text, error = false) {
    const notice = node("message");
    notice.textContent = text;
    notice.hidden = !text;
    notice.classList.toggle("error", error);
  }

  function render() {
    const card = node("status");
    if (!model.connected) {
      const title = element("strong", "", model.configured ? "Календарь не подключён" : "OAuth ожидает настройки");
      const text = element("p", "", model.configured
        ? "Внешняя занятость закроет слоты ресурса, а подтверждённые брони появятся в календаре Google."
        : "Добавьте параметры Google OAuth в конфигурацию SlotGuard, чтобы открыть подключение.");
      card.replaceChildren(title, text);
    } else {
      const name = element("strong", "", model.name || model.calendarId);
      const sync = value => value ? appointmentTime(value, viewerZone) : "ожидается";
      const rows = [
        ["Внешняя занятость", sync(model.importedAt)],
        ["Отправка броней", sync(model.exportedAt)]
      ].map(([label, value]) => {
        const row = element("div", "google-calendar-status-row");
        row.append(element("span", "", label), element("strong", "", value));
        return row;
      });
      card.replaceChildren(name, ...rows);
      if (model.availabilityStale)
        card.append(element("p", "google-calendar-alert", "Слоты откроются после обновления внешней занятости."));
      if (model.error)
        card.append(element("p", "google-calendar-alert", model.error));
      if (model.conflicts)
        card.append(element("p", "google-calendar-alert", `Брони с пересечением во внешнем календаре — ${model.conflicts}.`));
    }
    connect.hidden = !model.configured;
    node("submit").textContent = model.connected ? "Подключить заново" : "Подключить Google";
    node("refresh").hidden = !model.connected;
    node("disconnect").hidden = !model.connected;
  }

  async function perform(action) {
    if (busy) return;
    busy = true;
    const buttons = [...dialog.querySelectorAll("button"), input];
    for (const button of buttons) button.disabled = true;
    message("");
    try {
      await action();
    } catch (error) {
      message(error.message, true);
    } finally {
      busy = false;
      for (const button of buttons) button.disabled = false;
    }
  }

  connect.addEventListener("submit", event => {
    event.preventDefault();
    perform(async () => {
      const { authorize } = await actions.connect(input.value.trim());
      location.assign(authorize);
    });
  });
  node("refresh").addEventListener("click", () => perform(async () => {
    model = await actions.status();
    render();
  }));
  node("disconnect").addEventListener("click", () => {
    if (!window.confirm("Отключить Google Calendar для этого ресурса?")) return;
    perform(async () => {
      await actions.disconnect();
      model = await actions.status();
      render();
      message("Google Calendar отключён.");
    });
  });
  node("close").addEventListener("click", () => dialog.close());
  dialog.addEventListener("cancel", event => { if (busy) event.preventDefault(); });
  dialog.addEventListener("close", () => dialog.remove(), { once: true });
  render();
  document.body.append(dialog);
  dialog.showModal();
}

function showGoogleCalendarFeedback() {
  const url = new URL(location.href);
  const result = url.searchParams.get("googleCalendar");
  if (!result) return;
  url.searchParams.delete("googleCalendar");
  history.replaceState(null, "", url);
  const messages = {
    connected: "Google Calendar подключён. Первая синхронизация запустится автоматически.",
    denied: "Подключение Google отменено или срок запроса истёк.",
    token: "Google не выдал доступ для фоновой синхронизации. Подключите календарь заново.",
    replace: "Отключите текущий календарь перед выбором другого.",
    "in-use": "Этот календарь уже используется в SlotGuard."
  };
  const notice = section("google-calendar-feedback");
  notice.textContent = messages[result] ?? "Подключение не завершилось. Проверьте ID календаря и право редактировать события.";
  notice.classList.toggle("error", result !== "connected");
  notice.hidden = false;
}
