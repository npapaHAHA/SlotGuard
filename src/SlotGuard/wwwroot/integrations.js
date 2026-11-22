const integrationAddress = "/api/workspace/integrations";
const integrationNames = {
  "booking.created": "Создание",
  "booking.rescheduled": "Перенос",
  "booking.cancelled": "Отмена",
  "booking.approved": "Одобрение",
  "calendar.imported": "Импорт календаря",
  "integration.test": "Пробное событие"
};
const integrations = { data: null, request: 0 };

function integrationNotice(message, error = false) {
  const node = section("integration-notice");
  node.textContent = message;
  node.hidden = !message;
  node.dataset.tone = error ? "error" : "success";
}

function resetIntegrations() {
  integrations.request++;
  integrations.data = null;
  section("integration-key-form").reset();
  section("integration-webhook-form").reset();
  for (const id of ["integration-events", "integration-keys", "integration-webhooks", "integration-deliveries"])
    section(id).replaceChildren();
  document.querySelector(".integration-secret-dialog")?.close();
  integrationNotice("");
}

function renderIntegrationControls() {
  for (const control of document.querySelectorAll('[data-screen="integrations"] button, [data-screen="integrations"] input, [data-screen="integrations"] select'))
    control.disabled = state.busy || !integrations.data && control.id !== "refresh-integrations";
}

async function loadIntegrations() {
  const organization = state.organizationId;
  const request = ++integrations.request;
  const model = await api(integrationAddress, undefined, organization);
  if (request !== integrations.request || organization !== state.organizationId || currentScreen() !== "integrations") return;
  if (!integrations.data) {
    section("integration-events").replaceChildren(...model.events.map(type => {
      const label = element("label", "chip-check");
      const input = Object.assign(element("input", ""), { type: "checkbox", name: "events", value: type, checked: true, defaultChecked: true });
      label.append(input, element("span", "", integrationNames[type]));
      return label;
    }));
  }
  if (JSON.stringify(model) !== JSON.stringify(integrations.data)) renderIntegrations(model);
  integrations.data = model;
  renderIntegrationControls();
}

function integrationRow(name, details, action) {
  const row = element("li", "integration-row");
  const copy = element("div", "data-title");
  copy.append(element("strong", "", name), ...details.map(detail => element("span", "", detail)));
  row.append(copy, action);
  return row;
}

function renderIntegrations(model) {
  const keys = model.keys.map(key => integrationRow(key.name,
    [`${key.prefix}… · ${key.canWrite ? "Чтение и управление" : "Чтение"}`],
    button("outline-action", "Отозвать", () => revokeIntegration("keys", key, "Ключ отозван."))));
  const hooks = model.webhooks.map(hook => {
    const actions = element("div", "integration-actions");
    actions.append(button("outline-action", "Пробное событие", () => sendIntegrationTest(hook)),
      button("outline-action", "Отключить", () => revokeIntegration("webhooks", hook, "Вебхук отключён.")));
    return integrationRow(hook.name, [hook.url, hook.events.map(type => integrationNames[type]).join(" · ")], actions);
  });
  section("integration-keys").replaceChildren(...keys);
  section("integration-webhooks").replaceChildren(...hooks);

  const history = section("integration-deliveries");
  if (!model.deliveries.length) {
    history.replaceChildren(element("p", "list-empty", "Доставок пока нет. Можно отправить пробное событие."));
    return;
  }
  const wrapper = element("div", "integration-scroll");
  const table = element("table", "integration-table");
  const header = table.createTHead().insertRow();
  for (const title of ["Событие", "Получатель", "Статус", "Попытки", "Создано"]) {
    const heading = element("th", "", title);
    heading.scope = "col";
    header.append(heading);
  }
  const body = table.createTBody();
  for (const delivery of model.deliveries) {
    const row = body.insertRow();
    const state = delivery.deliveredAt ? "Доставлено" : delivery.failedAt ? "Ошибка" : delivery.attempts ? "Повтор" : "Ожидает";
    const tone = delivery.deliveredAt ? "approved" : delivery.failedAt ? "rejected" : "pending";
    row.insertCell().append(element("strong", "", integrationNames[delivery.type] ?? delivery.type),
      element("code", "integration-delivery-id", delivery.eventId));
    row.insertCell().textContent = delivery.name;
    const status = row.insertCell();
    status.append(element("span", `pill ${tone}`, state));
    if (delivery.lastStatus) status.append(element("span", "integration-response", `HTTP ${delivery.lastStatus}`));
    else if (delivery.lastError) status.append(element("span", "integration-response", {
      network: "Сеть", timeout: "Таймаут", disabled: "Отключён", destination: "Адрес недоступен", secret_unavailable: "Секрет недоступен"
    }[delivery.lastError] ?? "Ошибка доставки"));
    row.insertCell().textContent = delivery.attempts;
    row.insertCell().textContent = appointmentTime(delivery.createdAt, viewerZone, false);
  }
  wrapper.append(table);
  history.replaceChildren(wrapper);
}

function revealIntegrationSecret(title, label, value) {
  const dialog = questionDialog(title, "Скопируйте значение сейчас. При повторном открытии оно скрыто.");
  dialog.classList.add("integration-secret-dialog");
  const form = dialog.querySelector("form");
  const field = element("label", "", label);
  const input = Object.assign(element("input", ""), { readOnly: true, value, autocomplete: "off", spellcheck: false });
  field.append(input);
  dialog.querySelector("[data-fields]").append(field);
  dialog.querySelector("[data-save]").textContent = "Скопировать";
  dialog.querySelector("[data-close]").textContent = "Готово";
  form.addEventListener("submit", async event => {
    event.preventDefault();
    try {
      await navigator.clipboard.writeText(value);
      dialog.querySelector("[data-save]").textContent = "Скопировано";
    } catch {
      input.select();
    }
  });
  document.body.append(dialog);
  dialog.showModal();
  input.select();
}

function revokeIntegration(kind, item, success) {
  const action = kind === "keys" ? "Отозвать ключ" : "Отключить вебхук";
  if (!window.confirm(`${action} «${item.name}»?`)) return;
  return run(async () => {
    try {
      await api(`${integrationAddress}/${kind}/${item.id}`, { method: "DELETE" });
      await loadIntegrations();
      integrationNotice(success);
    } catch (error) {
      integrationNotice(error.message, true);
    }
  });
}

function sendIntegrationTest(hook) {
  return run(async () => {
    try {
      await api(`${integrationAddress}/webhooks/${hook.id}/test`, { method: "POST" });
      await loadIntegrations();
      integrationNotice("Пробное событие в очереди. Обновите список доставок, чтобы увидеть ответ.");
    } catch (error) {
      integrationNotice(error.message, true);
    }
  });
}

function bindIntegrations() {
  const keys = section("integration-key-form");
  const webhooks = section("integration-webhook-form");
  keys.addEventListener("submit", event => {
    event.preventDefault();
    const payload = Object.fromEntries(new FormData(keys));
    run(async () => {
      try {
        const issued = await api(`${integrationAddress}/keys`, jsonRequest("POST", payload));
        keys.reset();
        revealIntegrationSecret("API-ключ создан", "Ключ", issued.token);
        await loadIntegrations();
        integrationNotice("API-ключ создан.");
      } catch (error) {
        integrationNotice(error.message, true);
      }
    });
  });
  webhooks.addEventListener("submit", event => {
    event.preventDefault();
    const fields = new FormData(webhooks);
    const payload = { name: fields.get("name"), url: fields.get("url"), events: fields.getAll("events") };
    run(async () => {
      try {
        const added = await api(`${integrationAddress}/webhooks`, jsonRequest("POST", payload));
        webhooks.reset();
        revealIntegrationSecret("Вебхук добавлен", "Секрет подписи", added.secret);
        await loadIntegrations();
        integrationNotice("Вебхук добавлен.");
      } catch (error) {
        integrationNotice(error.message, true);
      }
    });
  });
  section("refresh-integrations").addEventListener("click", () => run(loadIntegrations));
  setInterval(() => {
    if (currentScreen() === "integrations" && !state.busy
      && integrations.data?.deliveries.some(item => !item.deliveredAt && !item.failedAt))
      loadIntegrations().catch(() => {});
  }, 4000);
}
