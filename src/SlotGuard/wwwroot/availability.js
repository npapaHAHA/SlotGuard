const availability = { criteria: null, result: null, version: 0 };
const availabilityDate = new Intl.DateTimeFormat("sv-SE", {
  year: "numeric", month: "2-digit", day: "2-digit", timeZone: "Europe/Moscow"
});
const resourceTypeLabels = { room: "Помещение", studio: "Студия", equipment: "Оборудование", other: "Ресурс" };

function availabilityNotice(text, tone, booked = false) {
  const message = section("availability-message");
  message.querySelector("p").textContent = text;
  message.querySelector("a").hidden = !booked;
  message.dataset.tone = tone ?? "";
  message.hidden = !text;
}

function resetAvailability() {
  const form = section("availability-form");
  form.reset();
  clearAvailability();
  const fields = form.elements;
  const calendar = state.calendar;
  fields.date.min = calendar?.today ?? "";
  fields.date.max = calendar ? isoPlusDays(calendar.lastWeek, 6) : "";
  const slots = calendar?.days[0].slots ?? [];
  fields.hour.replaceChildren(...slots.map(slot => {
    const time = timeFormat.format(new Date(slot.startsAt));
    return new Option(time, Number(time.slice(0, 2)));
  }));
  const next = calendar?.days.flatMap(item => item.slots).find(slot => slot.status !== "past");
  const target = next ? new Date(next.startsAt) : null;
  fields.date.value = target ? availabilityDate.format(target) : calendar ? isoPlusDays(calendar.today, 1) : "";
  if (target) fields.hour.value = Number(timeFormat.format(target).slice(0, 2));

  const tags = [...new Set(state.resources.flatMap(item => item.equipment ?? []))]
    .sort((left, right) => left.localeCompare(right, "ru"));
  section("availability-equipment").replaceChildren(...tags.map(tag => {
    const label = element("label", "chip-check");
    const input = Object.assign(element("input", ""), { type: "checkbox", name: "equipment", value: tag });
    label.append(input, element("span", "", tag));
    return label;
  }));
  section("availability-equipment-panel").hidden = tags.length === 0;
}

function availabilityEmpty(title, description) {
  const panel = element("div", "panel availability-empty");
  panel.append(symbol("event_available"), element("h2", "", title), element("p", "", description));
  return panel;
}

function clearAvailability() {
  availability.version++;
  availability.criteria = null;
  availability.result = null;
  availabilityNotice("");
  const empty = state.resources.length
    ? availabilityEmpty("Свободные варианты", "Укажите дату и время, чтобы проверить доступность ресурсов.")
    : availabilityEmpty("Ресурсов пока нет", "Добавьте ресурс в организации, чтобы начать поиск.");
  section("availability-results").replaceChildren(empty);
}

function renderAvailabilityControls() {
  const disabled = state.busy || !state.resources.length;
  for (const control of section("availability-form").elements) control.disabled = disabled;
  for (const control of section("availability-results").querySelectorAll("button")) control.disabled = state.busy;
}

function renderAvailability(result) {
  const content = section("availability-results");
  const focused = document.activeElement?.dataset.availableKey;
  const groups = [];
  const addGroup = (title, items) => {
    if (items.length === 0) return;
    const group = element("section", "availability-group");
    group.append(element("h2", "", title));
    for (const slot of items) group.append(availabilityCard(slot));
    groups.push(group);
  };
  const requested = appointmentTime(result.requested);
  if (!result.exact.length && result.alternatives.length)
    groups.push(availabilityEmpty("На это время всё занято", requested));
  addGroup(`Свободно · ${requested}`, result.exact);
  addGroup("Ближайшие альтернативы", result.alternatives);
  if (!groups.length)
    groups.push(availabilityEmpty("Нет свободных вариантов", "Измените фильтры, дату или время."));
  content.replaceChildren(...groups);
  if (focused) [...content.querySelectorAll("button")].find(item => item.dataset.availableKey === focused)?.focus();
  renderAvailabilityControls();
}

function availabilityCard(slot) {
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
  const date = element("time", "", appointmentTime(slot.startsAt));
  date.dateTime = slot.startsAt;
  const action = resource.requiresApproval ? "Отправить заявку" : "Забронировать";
  const book = button("page-action", action, () => bookFromAvailability(slot));
  book.dataset.availableKey = `${resource.id}|${slot.startsAt}`;
  book.setAttribute("aria-label", `${action}: ${resource.name}, ${date.textContent}`);
  schedule.append(date, book);
  card.append(leading, tags, schedule);
  return card;
}

async function fetchAvailability(criteria, withAlternatives = false, passive = false) {
  const version = ++availability.version;
  const query = new URLSearchParams(criteria);
  if (withAlternatives) query.set("includeAlternatives", "true");
  const result = await api(`/api/availability?${query}`);
  if (version !== availability.version) return;
  const previous = availability.result;
  if (passive) {
    const currentIds = new Set(result.exact.map(slot => slot.resource.id));
    const slotTaken = previous?.exact.some(slot => !currentIds.has(slot.resource.id));
    if (slotTaken) availabilityNotice("Доступность изменилась. Проверьте обновлённые варианты.", "changed");
    if (!slotTaken && previous?.exact.length && !previous.alternatives.length && result.exact.length)
      result.alternatives = [];
    if (JSON.stringify(previous) === JSON.stringify(result)) return;
  }
  availability.result = result;
  renderAvailability(result);
}

async function refreshAvailability() {
  if (!availability.criteria || !availability.result) return;
  try {
    await fetchAvailability(availability.criteria, true, true);
  } catch {
    // The next push or explicit search can retry the read.
  }
}

function bookFromAvailability(slot) {
  const filters = availability.criteria;
  if (!filters) return;
  run(async () => {
    let booking;
    try {
      booking = await api("/api/bookings", jsonRequest("POST", { resourceId: slot.resource.id, startsAt: slot.startsAt }));
    } catch (error) {
      if (error.status !== 409 && error.status !== 404) {
        availabilityNotice(error.message, "error");
        return;
      }
      const criteria = new URLSearchParams(filters);
      const startsAt = new Date(slot.startsAt);
      const date = availabilityDate.format(startsAt);
      const hour = Number(timeFormat.format(startsAt).slice(0, 2)).toString();
      criteria.set("date", date);
      criteria.set("hour", hour);
      const fields = section("availability-form").elements;
      fields.date.value = date;
      fields.hour.value = hour;
      availability.criteria = criteria;
      availabilityNotice("Этот слот уже недоступен. Вот актуальные варианты.", "changed");
      await fetchAvailability(criteria, true);
      return;
    }
    const confirmation = booking?.approvalStatus === "pending" ? "Заявка на согласовании" : "Бронь подтверждена";
    availabilityNotice(`${confirmation} · ${slot.resource.name}, ${appointmentTime(slot.startsAt)}.`, "success", true);
    await fetchAvailability(filters, true);
  });
}

function bindAvailability() {
  const form = section("availability-form");
  form.addEventListener("change", clearAvailability);
  form.addEventListener("submit", event => {
    event.preventDefault();
    const criteria = new URLSearchParams([...new FormData(form)].filter(([, value]) => value !== ""));
    run(async () => {
      availabilityNotice("");
      availability.criteria = criteria;
      availability.result = null;
      try {
        await fetchAvailability(criteria);
      } catch (error) {
        availabilityNotice(error.message, "error");
      }
    });
  });
}
