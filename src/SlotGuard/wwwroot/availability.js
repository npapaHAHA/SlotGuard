const availability = { criteria: null, result: null, version: 0 };
const resourceTypeLabels = { room: "Помещение", studio: "Студия", equipment: "Оборудование", other: "Ресурс" };

function availabilityNotice(text, tone, booked = false) {
  const message = section("availability-message");
  const detail = message.querySelector("p");
  const changed = detail.textContent !== text;
  detail.textContent = text;
  message.querySelector("a").hidden = !booked;
  message.dataset.tone = tone ?? "";
  message.hidden = !text;
  if (text && changed && tone !== "changed") reveal(message, 3);
}

function focusAvailabilityNotice() {
  if (currentScreen() !== "availability") return;
  const message = section("availability-message");
  message.focus({ preventScroll: true });
  message.scrollIntoView({ block: "nearest", behavior: reducedMotion.matches ? "auto" : "smooth" });
}

function resetAvailability() {
  const form = section("availability-form");
  form.reset();
  section("availability-filter-panel").open = false;
  const fields = form.elements;
  const calendar = state.calendar;
  fields.date.min = calendar?.today ?? "";
  fields.date.max = calendar?.lastDay ?? "";
  const next = calendar?.days.flatMap(item => item.slots).find(slot => slot.status !== "past");
  const target = next ? new Date(next.startsAt) : null;
  fields.date.value = target ? zoneFormats(viewerZone).day.format(target) : calendar ? isoPlusDays(calendar.today, 1) : "";
  if (target) fields.time.value = zoneFormats(viewerZone).time.format(target);

  const tags = [...new Set(state.resources.flatMap(item => item.equipment ?? []))]
    .sort((left, right) => left.localeCompare(right, "ru"));
  section("availability-equipment").replaceChildren(...tags.map(tag => {
    const label = element("label", "chip-check");
    const input = Object.assign(element("input", ""), { type: "checkbox", name: "equipment", value: tag });
    label.append(input, element("span", "", tag));
    return label;
  }));
  section("availability-equipment-panel").hidden = tags.length === 0;
  clearAvailability();
}

function activeAvailabilityFilters() {
  const form = section("availability-form");
  const { type, capacity } = form.elements;
  const active = [];
  if (type.value) active.push({ input: type, label: type.selectedOptions[0].textContent });
  if (capacity.value) active.push({ input: capacity, label: `От ${capacity.value} чел.` });
  for (const input of form.querySelectorAll('input[name="equipment"]:checked')) active.push({ input, label: input.value });
  return active;
}

function removeAvailabilityFilter(input) {
  if (input.type === "checkbox") input.checked = false;
  else input.value = "";
}

function renderAvailabilityFilters() {
  const active = activeAvailabilityFilters();
  const target = section("availability-filter-chips");
  const count = section("availability-filter-count");
  count.textContent = active.length;
  count.hidden = !active.length;
  section("availability-active-filters").hidden = !active.length;
  target.replaceChildren(...active.map(({ input, label }, index) => {
    const item = button("search-filter-chip", undefined, () => {
      removeAvailabilityFilter(input);
      clearAvailability(true);
      (target.children[Math.min(index, target.children.length - 1)] ?? section("availability-filter-toggle")).focus();
    });
    item.setAttribute("aria-label", `Убрать фильтр: ${label}`);
    const close = element("span", "filter-chip-close", "×");
    close.setAttribute("aria-hidden", "true");
    item.append(element("span", "", label), close);
    return item;
  }));
}

function availabilityEmpty(title, description) {
  const panel = element("div", "panel availability-empty");
  panel.append(symbol("event_available"), element("h2", "", title), element("p", "", description));
  return panel;
}

function clearAvailability(edited = false) {
  availability.version++;
  availability.criteria = null;
  availability.result = null;
  bundleSelection.clear();
  availabilityNotice("");
  section("availability-result-status").textContent = "";
  renderAvailabilityFilters();
  const empty = !state.resources.length
    ? availabilityEmpty("Ресурсов пока нет", "Добавьте ресурс в организации, чтобы начать поиск.")
    : edited
      ? availabilityEmpty("Параметры изменились", "Нажмите «Найти слоты», чтобы обновить результаты.")
      : availabilityEmpty("Свободные варианты", "Укажите дату и время, чтобы проверить доступность ресурсов.");
  section("availability-results").replaceChildren(empty);
}

function renderAvailabilityControls() {
  const disabled = state.busy || !state.resources.length;
  for (const control of section("availability-form").elements) control.disabled = disabled;
  for (const control of section("availability-results").querySelectorAll("button")) control.disabled = state.busy;
  syncSelectionControls();
}

function renderAvailability(result) {
  const content = section("availability-results");
  const focused = document.activeElement?.dataset.availableKey;
  const groups = [];
  if (!result.exact.length) bundleSelection.clear();
  const requested = appointmentTime(result.requested, viewerZone, false,
    new Date(Date.parse(result.requested) + result.durationMinutes * 60000));
  const addGroup = (title, items, kind, caption, icon) => {
    const exact = kind === "exact";
    const group = element("section", "panel search-result-group");
    group.dataset.kind = kind;
    const heading = element("div", "search-result-heading");
    const copy = element("div", "search-result-heading-copy");
    const name = element("h2", "", title);
    name.id = `availability-${kind}-title`;
    name.tabIndex = -1;
    group.setAttribute("aria-labelledby", name.id);
    const count = element("span", "result-count", items.length);
    count.setAttribute("aria-hidden", "true");
    copy.append(name, count, element("p", "", caption));
    heading.append(symbol(icon), copy);
    group.append(heading);
    if (items.length) {
      const list = element("div", "search-result-list");
      for (const slot of items) list.append(availabilityCard(slot, exact && items.length > 1, bookFromAvailability,
        exact ? "" : availabilityTimeDifference(result.requested, slot.startsAt)));
      group.append(list);
      if (exact) group.append(renderSelectionPanel(items));
    } else group.append(element("p", "search-result-no-exact", "На это время нет свободных слотов."));
    groups.push(group);
  };
  if (result.exact.length || result.alternatives.length) {
    addGroup("Точные совпадения", result.exact, "exact", requested, "event_available");
    if (result.alternatives.length)
      addGroup("Ближайшие альтернативы", result.alternatives, "alternative", "Та же длительность и фильтры", "schedule");
  } else {
    groups.push(availabilityEmpty("Нет свободных вариантов", result.bookingMessage ?? "Измените фильтры, дату или время."));
  }
  content.replaceChildren(...groups);
  renderAvailabilityControls();
  section("availability-result-status").textContent = `Точных совпадений: ${result.exact.length}. Альтернатив: ${result.alternatives.length}.`;
  if (focused) [...content.querySelectorAll("button")].find(item => item.dataset.availableKey === focused)?.focus({ preventScroll: true });
}

function availabilityTimeDifference(from, to) {
  const difference = Math.round((Date.parse(to) - Date.parse(from)) / 60000);
  const minutes = Math.abs(difference);
  const parts = [
    [Math.floor(minutes / 1440), "дн."],
    [Math.floor(minutes / 60) % 24, "ч"],
    [minutes % 60, "мин"]
  ].filter(([value]) => value).map(([value, unit]) => `${value} ${unit}`);
  return difference ? `На ${parts.join(" ")} ${difference > 0 ? "позже" : "раньше"}` : "";
}

function availabilityCard(slot, allowBundle, bookSlot = bookFromAvailability, timingNote = "") {
  const { resource } = slot;
  const card = element("article", "panel available-card");
  const leading = element("div", "available-heading");
  const icon = symbol(resource.type === "room" ? "meeting_room" : "event_available");
  icon.classList.add("available-icon");
  const copy = element("div", "available-copy");
  copy.append(element("h3", "", resource.name), element("p", "", resource.location));
  leading.append(icon, copy);
  const tags = element("ul", "available-tags");
  tags.append(element("li", "", resourceTypeLabels[resource.type] ?? resourceTypeLabels.other));
  if (resource.capacity) tags.append(element("li", "", `До ${resource.capacity} чел.`));
  for (const tag of resource.equipment) tags.append(element("li", "", tag));
  if (resource.requiresApproval) tags.append(element("li", "approval-caption", "По согласованию"));
  const schedule = element("div", "available-action");
  const date = element("time", "", appointmentTime(slot.startsAt, viewerZone, true, slot.endsAt));
  date.dateTime = slot.startsAt;
  const timing = element("div", "available-when");
  timing.append(date);
  if (timingNote) timing.append(element("span", "available-time-shift", timingNote));
  const action = resource.requiresApproval ? "Отправить заявку" : "Забронировать";
  const book = button(timingNote ? "outline-action" : "page-action", action, () => bookSlot(slot));
  book.dataset.availableKey = `${resource.id}|${slot.startsAt}`;
  book.setAttribute("aria-label", `${action}: ${resource.name}, ${date.textContent}`);
  if (allowBundle) schedule.append(bundlePickButton(slot));
  schedule.append(book);
  card.append(timing, leading, tags, schedule);
  return card;
}

async function fetchAvailability(criteria, { passive = false } = {}) {
  const version = ++availability.version;
  const query = new URLSearchParams(criteria);
  query.set("timeZone", viewerZone);
  query.set("includeAlternatives", "true");
  const result = await api(`/api/availability?${query}`);
  if (version !== availability.version) return;
  const previous = availability.result;
  if (passive) {
    const currentIds = new Set(result.exact.map(slot => slot.resource.id));
    const slotTaken = previous?.exact.some(slot => !currentIds.has(slot.resource.id));
    if (slotTaken) availabilityNotice("Доступность изменилась. Проверьте обновлённые варианты.", "changed");
    if (JSON.stringify(previous) === JSON.stringify(result)) return;
  }
  availability.result = result;
  renderAvailability(result);
  if (!passive) reveal(section("availability-results"), 6);
}

async function refreshAvailability() {
  if (!availability.criteria || !availability.result) return;
  try {
    await fetchAvailability(availability.criteria, { passive: true });
  } catch {
    // The next push or explicit search can retry the read.
  }
}

async function availabilityZoneChanged() {
  const instant = availability.result?.requested;
  const criteria = availability.criteria;
  const fields = section("availability-form").elements;
  fields.date.min = state.calendar?.today ?? "";
  fields.date.max = state.calendar?.lastDay ?? "";
  clearAvailability();
  if (instant && criteria) {
    const value = new Date(instant);
    fields.date.value = zoneFormats(viewerZone).day.format(value);
    fields.time.value = zoneFormats(viewerZone).time.format(value);
    criteria.set("date", fields.date.value);
    criteria.set("time", fields.time.value);
    availability.criteria = criteria;
    try { await fetchAvailability(criteria); }
    catch (error) { availabilityNotice(error.message, "error"); }
  } else if (fields.date.value && fields.date.value < fields.date.min) {
    fields.date.value = fields.date.min;
  }
}

function bookFromAvailability(slot) {
  const filters = availability.criteria;
  if (!filters) return;
  const organization = state.organizationId;
  return withBookingQuestions({
    resourceId: slot.resource.id,
    context: slot.resource.name,
    label: slot.resource.requiresApproval ? "Отправить заявку" : "Забронировать",
    submit: bookingForm => api("/api/bookings", jsonRequest("POST", {
      resourceId: slot.resource.id, startsAt: slot.startsAt, durationMinutes: durationOf(slot.startsAt, slot.endsAt), bookingForm
    }), organization),
    onError: async error => {
      if (![400, 404, 409].includes(error.status) || new Date(slot.startsAt) <= new Date()) {
        availabilityNotice(error.message, "error");
        focusAvailabilityNotice();
        return;
      }
      const criteria = new URLSearchParams(filters);
      const startsAt = new Date(slot.startsAt);
      const date = zoneFormats(viewerZone).day.format(startsAt);
      const time = zoneFormats(viewerZone).time.format(startsAt);
      criteria.set("date", date);
      criteria.set("time", time);
      const fields = section("availability-form").elements;
      fields.date.value = date;
      fields.time.value = time;
      availability.criteria = criteria;
      availabilityNotice("Этот слот уже недоступен. Вот актуальные варианты.", "changed");
      reveal(section("availability-message"), 3);
      focusAvailabilityNotice();
      await fetchAvailability(criteria);
      return;
    },
    onBooked: async booking => {
      const confirmation = booking?.approvalStatus === "pending" ? "Заявка на согласовании" : "Бронь подтверждена";
      availabilityNotice(`${confirmation} · ${slot.resource.name}, ${appointmentTime(slot.startsAt, viewerZone, true, slot.endsAt)}.`, "success", true);
      focusAvailabilityNotice();
      await fetchAvailability(filters);
    }
  });
}

function bindAvailability() {
  const form = section("availability-form");
  const results = section("availability-results");
  const submitLabel = section("availability-submit-label");
  const filterPanel = section("availability-filter-panel");
  form.addEventListener("change", () => clearAvailability(true));
  form.addEventListener("invalid", event => {
    if (filterPanel.contains(event.target)) filterPanel.open = true;
  }, true);
  section("availability-reset-filters").addEventListener("click", () => {
    for (const { input } of activeAvailabilityFilters()) removeAvailabilityFilter(input);
    clearAvailability(true);
    section("availability-filter-toggle").focus();
  });
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const criteria = new URLSearchParams([...new FormData(form)].filter(([, value]) => value !== ""));
    await run(async () => {
      availabilityNotice("");
      availability.criteria = criteria;
      availability.result = null;
      bundleSelection.clear();
      results.setAttribute("aria-busy", "true");
      section("availability-result-status").textContent = "Ищем свободные слоты.";
      submitLabel.textContent = "Ищем…";
      try {
        await fetchAvailability(criteria);
      } catch (error) {
        availabilityNotice(error.message, "error");
        results.replaceChildren(availabilityEmpty("Не удалось загрузить слоты", "Повторите поиск."));
        section("availability-result-status").textContent = "";
      } finally {
        results.setAttribute("aria-busy", "false");
        submitLabel.textContent = "Найти слоты";
      }
    });
    if (currentScreen() !== "availability") return;
    const heading = results.querySelector("h2");
    if (availability.result && heading) {
      heading.tabIndex = -1;
      heading.focus({ preventScroll: true });
      if (matchMedia("(max-width: 610px)").matches)
        heading.scrollIntoView({ block: "start", behavior: reducedMotion.matches ? "auto" : "smooth" });
    } else form.querySelector('[type="submit"]').focus({ preventScroll: true });
  });
}
