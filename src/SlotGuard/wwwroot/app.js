const ui = Object.fromEntries([
  "guest", "notice", "resources", "week-label", "prev", "today", "next", "week",
  "details", "race-panel", "race-button", "race-result", "status"
].map(id => [id, document.getElementById(id)]));

const labels = {
  available: "Свободно",
  booked: "Занято",
  mine: "Моя бронь",
  past: "Прошло"
};
const resourceIcons = { microscope: "◎", electronics: "ϟ", studio: "◉" };
const state = { resources: [], resourceId: null, calendar: null, selected: null, race: null, busy: false };
const dateFormat = new Intl.DateTimeFormat("ru-RU", {
  month: "long", day: "numeric", timeZone: "UTC"
});
const slotDateFormat = new Intl.DateTimeFormat("ru-RU", {
  weekday: "long", month: "long", day: "numeric", timeZone: "Europe/Moscow"
});
const timeFormat = new Intl.DateTimeFormat("ru-RU", {
  hour: "2-digit", minute: "2-digit", timeZone: "Europe/Moscow"
});
const weekdays = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

function element(tag, className, text) {
  const node = document.createElement(tag);
  node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function button(className, text, onClick) {
  const node = element("button", className, text);
  node.type = "button";
  node.addEventListener("click", onClick);
  return node;
}

async function api(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    const fallback = response.status === 401
      ? "Сеанс завершился. Обновите страницу."
      : "Сервер не смог выполнить запрос.";
    throw new Error(problem.title || fallback);
  }
  if (response.status !== 204) return response.json();
}

function showError(error, retry) {
  const message = error instanceof TypeError ? "Не удалось связаться с сервером." : error.message;
  ui.notice.replaceChildren(element("span", "", message));
  if (retry) ui.notice.append(button("retry", "Повторить", retry));
  ui.notice.hidden = false;
}

function isoPlusDays(iso, days) {
  const date = new Date(iso);
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

function allSlots() {
  return state.calendar?.days.flatMap(day => day.slots) ?? [];
}

function selectedSlot() {
  return allSlots().find(slot => slot.startsAt === state.selected);
}

async function loadCalendar(resourceId, week, retainSelection = false) {
  const query = new URLSearchParams({ resourceId });
  if (week) query.set("week", week);
  const calendar = await api(`/api/calendar?${query}`);
  state.calendar = calendar;
  state.resourceId = resourceId;
  const slots = allSlots();
  const retained = retainSelection && slots.find(slot => slot.startsAt === state.selected);
  const initial = retained || slots.find(slot => slot.status === "available")
    || slots.find(slot => slot.status !== "past") || slots[0];
  state.selected = initial?.startsAt ?? null;
  if (!retainSelection) state.race = null;
}

async function run(action, retry) {
  if (state.busy) return;
  const focused = document.activeElement;
  const resourceFocus = focused?.dataset.resourceId;
  const slotFocus = focused?.dataset.startsAt;
  state.busy = true;
  ui.notice.hidden = true;
  render();
  try {
    await action();
  } catch (error) {
    showError(error, retry);
  } finally {
    state.busy = false;
    render();
    if (resourceFocus) {
      [...ui.resources.children].find(node => node.dataset.resourceId === resourceFocus)?.focus();
    } else if (slotFocus || focused?.closest(".details")) {
      ui.week.querySelector('.slot[aria-pressed="true"]')?.focus({ preventScroll: true });
    }
  }
}

function changeCalendar(resourceId, week) {
  return run(async () => {
    await loadCalendar(resourceId, week);
    scrollSelectionIntoView();
  }, () => changeCalendar(resourceId, week));
}

function scrollSelectionIntoView() {
  requestAnimationFrame(() => {
    const selected = ui.week.querySelector('.slot[aria-pressed="true"]');
    const viewport = ui.week.parentElement;
    if (!selected) return;
    const offset = selected.getBoundingClientRect().left - viewport.getBoundingClientRect().left;
    if (offset < 0 || offset + selected.offsetWidth > viewport.clientWidth)
      viewport.scrollLeft += offset - 7;
  });
}

function render() {
  renderResources();
  renderCalendar();
  renderDetails();
}

function renderResources() {
  const buttons = state.resources.map(resource => {
    const item = button("resource", undefined, () => changeCalendar(resource.id, state.calendar?.week));
    item.dataset.resourceId = resource.id;
    item.disabled = state.busy;
    item.setAttribute("aria-pressed", resource.id === state.resourceId);
    const icon = element("span", `resource-icon ${resource.id}`, resourceIcons[resource.id] ?? "•");
    icon.setAttribute("aria-hidden", "true");
    const description = element("span", "");
    description.append(
      element("span", "resource-name", resource.name),
      element("span", "resource-location", resource.location)
    );
    item.append(icon, description);
    return item;
  });
  ui.resources.replaceChildren(...buttons);
}

function renderCalendar() {
  const calendar = state.calendar;
  ui.prev.disabled = state.busy || !calendar || calendar.week === calendar.firstWeek;
  ui.today.disabled = ui.prev.disabled;
  ui.next.disabled = state.busy || !calendar || calendar.week === calendar.lastWeek;
  if (!calendar) {
    ui["week-label"].textContent = state.busy ? "Загрузка…" : "Календарь недоступен";
    return;
  }

  const { week, days } = calendar;
  ui["week-label"].textContent = dateFormat.formatRange(new Date(week), new Date(days.at(-1).date))
    + ` ${new Date(days.at(-1).date).getUTCFullYear()}`;

  const columns = days.map((day, index) => {
    const column = element("div", day.date === calendar.today ? "day current" : "day");
    const heading = element("div", "day-heading");
    heading.append(element("span", "weekday", weekdays[index]), element("span", "day-number", Number(day.date.slice(-2))));
    const slots = element("div", "slots");
    for (const slot of day.slots) {
      const date = new Date(slot.startsAt);
      const time = timeFormat.format(date);
      const item = button(`slot ${slot.status}`, undefined, () => selectSlot(slot));
      item.dataset.startsAt = slot.startsAt;
      item.disabled = state.busy;
      item.setAttribute("aria-pressed", slot.startsAt === state.selected);
      item.setAttribute("aria-label", `${slotDateFormat.format(date)}, ${time}. ${labels[slot.status]}`);
      item.append(element("span", "slot-time", time), element("span", "slot-state", labels[slot.status]));
      slots.append(item);
    }
    column.append(heading, slots);
    return column;
  });
  ui.week.replaceChildren(...columns);
}

function selectSlot(slot) {
  if (slot.startsAt !== state.selected) state.race = null;
  state.selected = slot.startsAt;
  for (const item of ui.week.querySelectorAll(".slot"))
    item.setAttribute("aria-pressed", item.dataset.startsAt === state.selected);
  renderDetails();
}

function renderDetails() {
  const slot = selectedSlot();
  ui.details.replaceChildren();
  ui["race-panel"].hidden = !slot || (slot.status !== "available" && !state.race);
  if (!slot) return;

  const start = new Date(slot.startsAt);
  const end = new Date(start.valueOf() + 60 * 60 * 1000);
  const section = element("div", "details-slot");
  section.append(
    element("span", `pill ${slot.status}`, labels[slot.status]),
    element("p", "details-date", slotDateFormat.format(start)),
    element("p", "details-time", `${timeFormat.format(start)}–${timeFormat.format(end)}`)
  );
  if (slot.status === "available" || slot.status === "mine") {
    const isMine = slot.status === "mine";
    const action = button(
      isMine ? "button secondary cancel" : "button primary",
      isMine ? "Отменить бронь" : "Забронировать",
      () => updateBooking(slot)
    );
    action.disabled = state.busy;
    section.append(action);
  }
  ui.details.append(section);
  ui["race-button"].hidden = slot.status !== "available";
  ui["race-button"].disabled = state.busy;
  ui["race-result"].replaceChildren();
  if (state.race) {
    const { created, conflicts, other } = state.race;
    const counts = element("div", "race-counts");
    counts.append(
      element("span", "pill available", `${created} × HTTP 201`),
      element("span", "pill booked", `${conflicts} × HTTP 409`)
    );
    if (other > 0) counts.append(element("span", "pill past", `${other} прочих исходов`));
    ui["race-result"].append(element("p", "", "Результат 20 запросов"), counts);
  }
}

function bookingPayload(slot) {
  return {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ resourceId: state.resourceId, startsAt: slot.startsAt })
  };
}

function refreshCalendar() {
  return loadCalendar(state.resourceId, state.calendar.week, true);
}

function updateBooking(slot) {
  return run(async () => {
    state.race = null;
    const isCancel = slot.status === "mine";
    try {
      if (isCancel) await api(`/api/bookings/${slot.bookingId}`, { method: "DELETE" });
      else await api("/api/bookings", bookingPayload(slot));
      ui.status.textContent = isCancel ? "Бронь отменена." : "Бронирование подтверждено.";
    } finally {
      await refreshCalendar();
    }
  }, () => run(refreshCalendar));
}

function raceForSlot() {
  const slot = selectedSlot();
  if (slot?.status !== "available") return;
  return run(async () => {
    const requests = Array.from({ length: 20 }, async () => {
      const response = await fetch("/api/bookings", bookingPayload(slot));
      await response.arrayBuffer();
      return response.status;
    });
    const results = await Promise.allSettled(requests);
    const created = results.filter(result => result.status === "fulfilled" && result.value === 201).length;
    const conflicts = results.filter(result => result.status === "fulfilled" && result.value === 409).length;
    state.race = { created, conflicts, other: results.length - created - conflicts };
    await refreshCalendar();
  }, () => run(refreshCalendar));
}

function start() {
  return run(async () => {
    const session = await api("/api/session", { method: "POST" });
    ui.guest.textContent = session.label;
    ui.guest.hidden = false;
    state.resources = await api("/api/resources");
    await loadCalendar(state.resources[0].id);
    scrollSelectionIntoView();
  }, start);
}

ui.prev.addEventListener("click", () => changeCalendar(state.resourceId, isoPlusDays(state.calendar.week, -7)));
ui.next.addEventListener("click", () => changeCalendar(state.resourceId, isoPlusDays(state.calendar.week, 7)));
ui.today.addEventListener("click", () => changeCalendar(state.resourceId, state.calendar.firstWeek));
ui["race-button"].addEventListener("click", raceForSlot);
start();
