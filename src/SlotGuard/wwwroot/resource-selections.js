const bundleSelection = new Set();
const bundleLimit = 6;
const selectionKey = slot => `${slot.resource.id}|${slot.startsAt}`;

function renderSelectionPanel(exact) {
  const current = new Set(exact.map(selectionKey));
  for (const key of bundleSelection)
    if (!current.has(key)) bundleSelection.delete(key);

  const panel = element("section", "panel selection-panel");
  const info = element("div", "selection-heading");
  const text = element("div", "");
  text.append(element("h2", "", "Подбор ресурсов"));
  if (exact.length > 1)
    text.append(element("p", "", "Доверьте выбор SlotGuard или отметьте ресурсы для общей брони."));
  info.append(symbol("grid_view"), text);
  const actions = element("div", "selection-actions");
  const byTime = new Map();
  for (const slot of exact) {
    const group = byTime.get(slot.startsAt) ?? [];
    group.push(slot);
    byTime.set(slot.startsAt, group);
  }
  for (const [time, slots] of byTime) {
    const label = byTime.size === 1 ? "Подобрать ресурс" : `Подобрать · ${timeWithOffset(new Date(time), viewerZone)}`;
    const auto = button("outline-action", label, () => openResourceSelection("auto", slots.slice(0, 32)));
    auto.dataset.availableKey = `auto|${time}`;
    actions.append(auto);
  }
  if (exact.length > 1) {
    const bundle = button("page-action", "", () => {
      const picked = exact.filter(slot => bundleSelection.has(selectionKey(slot)));
      if (picked.length >= 2) openResourceSelection("bundle", picked);
    });
    bundle.dataset.bundleAction = "";
    bundle.dataset.availableKey = "bundle";
    actions.append(bundle);
    const counter = element("span", "selection-count");
    counter.dataset.bundleCount = "";
    counter.setAttribute("role", "status");
    panel.append(info, actions, counter);
  } else panel.append(info, actions);
  return panel;
}

function bundlePickButton(slot) {
  const key = selectionKey(slot);
  const pick = button("outline-action bundle-pick", "", () => {
    if (!bundleSelection.delete(key)) bundleSelection.add(key);
    syncSelectionControls();
  });
  Object.assign(pick.dataset, { bundlePick: key, availableKey: `pick|${key}`, bundleTime: slot.startsAt,
    bundleName: slot.resource.name });
  return pick;
}

function syncSelectionControls() {
  const results = section("availability-results");
  const bundle = results.querySelector("[data-bundle-action]");
  if (!bundle) return;
  const size = bundleSelection.size;
  const choices = [...results.querySelectorAll("[data-bundle-pick]")];
  const activeTime = choices.find(choice => bundleSelection.has(choice.dataset.bundlePick))?.dataset.bundleTime;
  for (const choice of choices) {
    const checked = bundleSelection.has(choice.dataset.bundlePick);
    choice.textContent = checked ? "В комплекте" : "+ В комплект";
    choice.setAttribute("aria-pressed", String(checked));
    choice.setAttribute("aria-label", `${choice.textContent} · ${choice.dataset.bundleName}`);
    choice.disabled = state.busy || !checked && (size >= bundleLimit || Boolean(activeTime && activeTime !== choice.dataset.bundleTime));
  }
  bundle.textContent = size ? `Оформить комплект · ${size}` : "Оформить комплект";
  bundle.disabled = state.busy || size < 2;
  const count = results.querySelector("[data-bundle-count]");
  count.textContent = size === 1 ? "Выберите ещё один ресурс на это время."
    : size === bundleLimit ? "Выбрано максимальное число ресурсов." : "";
  count.hidden = !count.textContent;
}

function openResourceSelection(mode, slots) {
  if (!slots.length || document.querySelector(".resource-selection-dialog")) return;
  const first = slots[0];
  const organization = state.organizationId;
  const criteria = availability.criteria;
  const address = "/api/booking-selections";
  const payload = { mode, resourceIds: slots.map(slot => slot.resource.id), startsAt: first.startsAt,
    durationMinutes: durationOf(first.startsAt, first.endsAt) };
  const preview = () => api(`${address}/preview`, jsonRequest("POST", payload), organization);
  const renderError = async error => {
    availabilityNotice(error.message, [404, 409].includes(error.status) ? "changed" : "error");
    if (criteria && [404, 409, 422].includes(error.status)) await fetchAvailability(criteria);
  };

  return run(async () => {
    try {
      const plan = await preview();
      confirmResourceSelection({ first, payload, plan, preview, organization, address, criteria });
    } catch (error) {
      await renderError(error);
    }
  });
}

function confirmResourceSelection({ first, payload, plan, preview, organization, address, criteria }) {
  const bundle = payload.mode === "bundle";
  const title = bundle ? "Бронь комплекта" : "Подходящий ресурс";
  const dialog = questionDialog(title, appointmentTime(first.startsAt, viewerZone, true, first.endsAt));
  dialog.classList.add("resource-selection-dialog");
  const list = dialog.querySelector("[data-fields]");
  list.className = "selection-resources";
  const message = dialog.querySelector("[data-message]");
  const submit = dialog.querySelector("[data-save]");
  const close = dialog.querySelector("[data-close]");
  const requestId = crypto.randomUUID();
  let cards = new Map();
  let busy = false;
  let available = true;

  function renderPlan(next) {
    plan = next;
    const updated = new Map();
    for (const resource of plan.resources) {
      let card = cards.get(resource.id);
      if (!card) {
        const row = element("section", "selection-resource");
        const label = element("div", "selection-resource-label");
        const name = element("strong", "", resource.name);
        const location = element("p", "", resource.location);
        const fields = element("div", "booking-question-fields");
        label.append(symbol(bundle ? "layers" : "event_available"), name, location);
        row.append(label, fields);
        card = { row, name, location, fields: bookingFields(fields, resource.form) };
      } else {
        card.name.textContent = resource.name;
        card.location.textContent = resource.location;
        card.fields.setForm(resource.form);
      }
      card.fields.visible(true);
      updated.set(resource.id, card);
    }
    cards = updated;
    list.replaceChildren(...[...cards.values()].map(card => card.row));
    submit.textContent = plan.requiresApproval ? "Отправить на согласование"
      : bundle ? "Забронировать комплект" : "Подтвердить бронь";
  }

  function notify(text) {
    message.textContent = text;
    message.hidden = !text;
  }

  function setBusy(value) {
    busy = close.disabled = value;
    submit.disabled = value || !available;
    for (const card of cards.values()) card.fields.disable(value);
  }

  renderPlan(plan);
  dialog.addEventListener("cancel", event => { if (busy) event.preventDefault(); });
  dialog.querySelector("form").addEventListener("submit", async event => {
    event.preventDefault();
    if (busy || !available || organization !== state.organizationId) return;
    const forms = {};
    for (const [id, card] of cards) {
      const answer = card.fields.read();
      if (answer === undefined) return;
      if (answer) forms[id] = answer;
    }
    setBusy(true);
    notify("");
    let result;
    try {
      result = await api(address, jsonRequest("POST", {
        ...payload, requestId, forms, expectedApproval: plan.requiresApproval,
        preferredId: bundle ? null : plan.resources[0].id
      }), organization);
    } catch (error) {
      notify(error.message);
      if ([404, 409, 422].includes(error.status)) {
        try {
          renderPlan(await preview());
          if (error.questionErrors) {
            setBusy(false);
            for (const card of cards.values()) card.fields.showErrors(error.questionErrors);
          }
        } catch (refreshError) {
          if (refreshError.status && refreshError.status < 500) {
            available = false;
            notify(refreshError.message);
            if (criteria && criteria === availability.criteria)
              await fetchAvailability(criteria).catch(() => {});
          }
        }
      }
    } finally {
      setBusy(false);
    }
    if (!result) return;
    dialog.close();
    bundleSelection.clear();
    const status = result.bookings[0].booking.approvalStatus === "pending"
      ? "Заявка на согласовании" : bundle ? "Комплект забронирован" : "Бронь подтверждена";
    const names = result.bookings.map(item => item.resourceName).join(" + ");
    availabilityNotice(`${status} · ${names}, ${appointmentTime(first.startsAt, viewerZone, true, first.endsAt)}.`, "success", true);
    if (criteria && criteria === availability.criteria) await fetchAvailability(criteria).catch(() => {});
  });

  document.body.append(dialog);
  dialog.showModal();
}
