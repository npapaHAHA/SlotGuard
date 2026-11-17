const ui = Object.fromEntries([
  "guest", "notice", "resources", "week-label", "prev", "today", "next", "week",
  "details", "race-panel", "race-button", "race-result", "status", "organization-controls", "organization",
  "organization-role", "session-button", "login-dialog", "login-form", "login-cancel", "login-error",
  "notification-panel", "notifications"
].map(id => [id, document.getElementById(id)]));

const labels = {
  available: "Свободно",
  booked: "Занято",
  mine: "Моя бронь",
  held: "Ваш резерв",
  maintenance: "Обслуживание",
  past: "Прошло"
};
const resourceIcons = { microscope: "◎", electronics: "ϟ", studio: "◉" };
const state = {
  session: null, organizations: [], organizationId: null,
  resources: [], resourceId: null, calendar: null, selected: null, race: null, busy: false
};
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
let polling = false;
let pollingNotifications = false;
let notificationSignature = "";

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

function request(url, options, organizationId = state.organizationId) {
  const headers = new Headers(options?.headers);
  if (organizationId) headers.set("X-SlotGuard-Organization", organizationId);
  return fetch(url, { ...options, headers });
}

async function api(url, options, organizationId) {
  const response = await request(url, options, organizationId);
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    const fallback = {
      401: "Сеанс завершился. Обновите страницу.",
      403: "Для этого действия нужны права администратора.",
      404: "Ресурс недоступен в этой организации.",
      429: "Слишком много запросов. Повторите через минуту."
    }[response.status] ?? "Сервер не смог выполнить запрос.";
    const error = new Error(problem.title || fallback);
    error.conflicts = problem.conflicts;
    throw error;
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

function fetchCalendar(resourceId, week, organizationId = state.organizationId) {
  const query = new URLSearchParams({ resourceId });
  if (week) query.set("week", week);
  return api(`/api/calendar?${query}`, undefined, organizationId);
}

async function loadCalendar(resourceId, week, retainSelection = false, organizationId = state.organizationId) {
  const calendar = await fetchCalendar(resourceId, week, organizationId);
  state.calendar = calendar;
  state.resourceId = resourceId;
  const slots = allSlots();
  const retained = retainSelection && slots.find(slot => slot.startsAt === state.selected);
  const initial = retained || slots.find(slot => slot.status === "available")
    || slots.find(slot => slot.status !== "past") || slots[0];
  state.selected = initial?.startsAt ?? null;
  if (!retainSelection) state.race = null;
}

async function pollWaitlist(force = false) {
  const previous = state.calendar;
  if (!previous || polling || state.busy ||
      (!force && !allSlots().some(slot => slot.waitlistId || slot.status === "held"))) return;

  polling = true;
  try {
    const calendar = await fetchCalendar(state.resourceId, previous.week);
    if (state.busy || state.calendar !== previous || JSON.stringify(calendar) === JSON.stringify(previous)) return;

    const previousSlot = selectedSlot();
    const focusedSlot = document.activeElement?.dataset.startsAt;
    state.calendar = calendar;
    renderCalendar();
    if (focusedSlot) {
      [...ui.week.querySelectorAll(".slot")].find(node => node.dataset.startsAt === focusedSlot)
        ?.focus({ preventScroll: true });
    }
    const slot = selectedSlot();
    if (JSON.stringify(slot) !== JSON.stringify(previousSlot)) {
      renderDetails();
      if (slot?.status === "held" && previousSlot?.status !== "held")
        ui.status.textContent = "Слот зарезервирован для вас.";
    }
  } catch {
    // Retry on the next poll. Explicit actions still surface API errors.
  } finally {
    polling = false;
  }
}

function renderNotifications(items) {
  const signature = JSON.stringify(items);
  if (signature === notificationSignature) return;
  notificationSignature = signature;
  ui["notification-panel"].hidden = items.length === 0;
  ui.notifications.replaceChildren(...items.map(item => {
    const control = button("notification", undefined, () => openNotification(item));
    const start = new Date(item.startsAt);
    control.append(
      element("strong", "", item.resourceName),
      element("span", "", `${slotDateFormat.format(start)}, ${timeFormat.format(start)} · подтвердите до ${timeFormat.format(new Date(item.holdExpiresAt))}`)
    );
    return control;
  }));
}

async function pollNotifications() {
  const { session, organizationId } = state;
  if (!session || !organizationId || state.busy || pollingNotifications) return;

  pollingNotifications = true;
  try {
    const items = await api("/api/notifications", undefined, organizationId);
    if (!state.busy && state.session === session && state.organizationId === organizationId)
      renderNotifications(items);
  } catch {
    // Retry on the next refresh and keep the current calendar usable.
  } finally {
    pollingNotifications = false;
  }
}

function openNotification(item) {
  return goToCalendar(item);
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
  for (const control of ui.notifications.querySelectorAll("button"))
    control.disabled = state.busy;
  renderOrganization();
  renderAccountControls();
  renderLiveStatus();
  renderWorkspaceScreen();
  renderResources();
  renderCalendar();
  renderDetails();
}

function renderOrganization() {
  const session = state.session;
  const organization = state.organizations.find(item => item.id === state.organizationId);
  ui["session-button"].hidden = !session?.demoEnabled || session.isAccount;
  ui["session-button"].textContent = session?.isDemo ? "Выйти" : "Демо-вход";
  ui["session-button"].disabled = state.busy;
  ui["organization-controls"].hidden = !organization;
  if (!organization) return;

  const select = ui.organization;
  const options = state.organizations.map(item => {
    const option = element("option", "", item.name);
    option.value = item.id;
    return option;
  });
  select.replaceChildren(...options);
  select.value = organization.id;
  select.disabled = state.busy || options.length < 2;
  ui["organization-role"].textContent = organization.role === "admin" ? "Администратор" : "Участник";
}

function isAdmin() {
  return state.organizations.some(item => item.id === state.organizationId && item.role === "admin");
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
  ui.resources.replaceChildren(...(buttons.length ? buttons : (state.organizationId
    ? [element("p", "list-empty", "Нет доступных ресурсов.")] : [])));
}

function renderCalendar() {
  const calendar = state.calendar;
  ui.prev.disabled = state.busy || !calendar || calendar.week === calendar.firstWeek;
  ui.today.disabled = ui.prev.disabled;
  ui.next.disabled = state.busy || !calendar || calendar.week === calendar.lastWeek;
  if (!calendar) {
    ui["week-label"].textContent = state.busy ? "Загрузка…" : "Выберите доступный ресурс";
    ui.week.replaceChildren();
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
      const label = slot.waitlistId ? "В очереди" : labels[slot.status];
      item.setAttribute("aria-label", `${slotDateFormat.format(date)}, ${time}. ${label}`);
      item.append(element("span", "slot-time", time), element("span", "slot-state", label));
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
  renderSlotActions(section, slot);
  ui.details.append(section);
  updateCountdown();
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

function renderSlotActions(section, slot) {
  function action(css, label, url, options, message) {
    const control = button(css, label, () => updateBooking(url, options, message));
    control.disabled = state.busy;
    section.append(control);
    return control;
  }

  const booking = `/api/bookings/${slot.bookingId}`;
  const remove = { method: "DELETE" };

  switch (slot.status) {
    case "available":
      action("button primary", "Забронировать", "/api/bookings", bookingPayload(slot), "Бронирование подтверждено.");
      if (isAdmin()) section.append(maintenanceForm(slot));
      break;
    case "mine":
      action("button secondary cancel", "Отменить бронь", booking, remove, "Бронь отменена.");
      break;
    case "booked":
      if (slot.waitlistId) {
        section.append(element("p", "details-date", `Место в очереди — ${slot.waitlistPosition}`));
        action("button secondary cancel", "Выйти из очереди", `/api/waitlist/${slot.waitlistId}`, remove, "Вы вышли из очереди.");
      } else {
        action("button secondary cancel", "Встать в очередь", "/api/waitlist", bookingPayload(slot), "Вы в листе ожидания.");
      }
      break;
    case "held": {
      const countdown = element("p", "details-date hold-countdown");
      countdown.dataset.expiresAt = slot.holdExpiresAt;
      section.append(countdown);
      const confirm = action("button primary", "Подтвердить бронь", `${booking}/confirm`,
        { method: "POST" }, "Бронирование подтверждено.");
      confirm.dataset.holdConfirm = "";
      action("button secondary cancel", "Отказаться от резерва", booking, remove, "Резерв освобождён.");
      break;
    }
    case "maintenance":
      section.append(element("p", "details-date", slot.maintenanceReason));
      if (isAdmin() && slot.maintenanceId) {
        action("button secondary cancel", "Снять блокировку", `/api/maintenance/${slot.maintenanceId}`,
          remove, "Слот снова доступен.");
      }
      break;
  }
}

function maintenanceForm(slot) {
  const form = element("form", "maintenance-form");
  const label = element("label", "", "Причина обслуживания");
  label.htmlFor = "maintenance-reason";
  const input = element("input", "");
  input.id = label.htmlFor;
  input.required = true;
  input.maxLength = 160;
  input.disabled = state.busy;
  const submit = element("button", "button secondary", "Закрыть на обслуживание");
  submit.type = "submit";
  submit.disabled = state.busy;
  form.append(label, input, submit);
  form.addEventListener("submit", event => {
    event.preventDefault();
    updateBooking("/api/maintenance", bookingPayload(slot, { reason: input.value.trim() }), "Слот закрыт на обслуживание.");
  });
  return form;
}

function updateCountdown() {
  const label = ui.details.querySelector(".hold-countdown");
  if (!label) return;
  const seconds = Math.max(0, Math.ceil((Date.parse(label.dataset.expiresAt) - Date.now()) / 1000));
  const minutes = Math.floor(seconds / 60);
  label.textContent = seconds > 0
    ? `Подтвердите за ${minutes}:${String(seconds % 60).padStart(2, "0")}`
    : "Время резерва истекло.";
  ui.details.querySelector("[data-hold-confirm]").disabled = state.busy || seconds === 0;
}

function bookingPayload(slot, fields = {}) {
  return {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ resourceId: state.resourceId, startsAt: slot.startsAt, ...fields })
  };
}

function refreshCalendar() {
  return state.calendar ? loadCalendar(state.resourceId, state.calendar.week, true) : Promise.resolve();
}

function updateBooking(url, options, message) {
  return run(async () => {
    state.race = null;
    try {
      await api(url, options);
      ui.status.textContent = message;
    } finally {
      await refreshCalendar();
      await loadWorkspace();
    }
  }, () => run(refreshCalendar));
}

function raceForSlot() {
  const slot = selectedSlot();
  if (slot?.status !== "available") return;
  return run(async () => {
    const requests = Array.from({ length: 20 }, async () => {
      const response = await request("/api/bookings", bookingPayload(slot));
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

async function enterOrganization(id) {
  const resources = await api("/api/resources", undefined, id);
  if (resources.length) await loadCalendar(resources[0].id, undefined, false, id);
  else Object.assign(state, { resourceId: null, calendar: null, selected: null, race: null });
  state.resources = resources;
  state.organizationId = id;
  sessionStorage.setItem("slotguard.organization", id);
  renderNotifications([]);
  resetWorkspace();
  await loadWorkspace();
  connectCalendar(id);
  scrollSelectionIntoView();
}

async function enterSession(session, preferredOrganization = sessionStorage.getItem("slotguard.organization")) {
  disconnectCalendar();
  Object.assign(state, {
    session, organizations: [], organizationId: null,
    resources: [], resourceId: null, calendar: null, selected: null, race: null
  });
  ui.guest.textContent = session.label;
  ui.guest.hidden = false;
  renderNotifications([]);
  resetWorkspace();
  render();
  state.organizations = await api("/api/organizations");
  const organization = state.organizations.find(item => item.id === preferredOrganization) ?? state.organizations[0];
  if (organization) await enterOrganization(organization.id);
  else render();
}

function start() {
  return run(async () => enterSession(await api("/api/session", { method: "POST" })), start);
}

ui.organization.addEventListener("change", event => {
  const id = event.currentTarget.value;
  run(() => enterOrganization(id), () => run(() => enterOrganization(id)));
});
ui["session-button"].addEventListener("click", () => {
  if (state.session?.isDemo) {
    run(async () => enterSession(await api("/api/session/guest", { method: "POST" })), start);
    return;
  }
  ui["login-error"].hidden = true;
  ui["login-dialog"].showModal();
});
ui["login-cancel"].addEventListener("click", () => ui["login-dialog"].close());
ui["login-form"].addEventListener("submit", event => {
  event.preventDefault();
  const form = event.currentTarget;
  const credentials = Object.fromEntries(new FormData(form));
  run(async () => {
    let session;
    try {
      session = await api("/api/session/demo", {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(credentials)
      });
    } catch (error) {
      ui["login-error"].textContent = error instanceof TypeError ? "Не удалось связаться с сервером." : error.message;
      ui["login-error"].hidden = false;
      return;
    }
    form.elements.password.value = "";
    ui["login-dialog"].close();
    await enterSession(session);
  }, start);
});
ui.prev.addEventListener("click", () => changeCalendar(state.resourceId, isoPlusDays(state.calendar.week, -7)));
ui.next.addEventListener("click", () => changeCalendar(state.resourceId, isoPlusDays(state.calendar.week, 7)));
ui.today.addEventListener("click", () => changeCalendar(state.resourceId, state.calendar.firstWeek));
ui["race-button"].addEventListener("click", raceForSlot);
setInterval(() => { if (!liveReady) pollWaitlist(); }, 2000);
setInterval(pollNotifications, 2000);
setInterval(updateCountdown, 1000);
bindWorkspace();
bindAccounts();
start();
