const ui = Object.fromEntries([
  "guest", "notice", "resources", "week-label", "prev", "today", "next", "week",
  "details", "race-panel", "race-button", "race-result", "status", "organization-controls", "organization",
  "organization-role", "session-button", "login-dialog", "login-form", "login-cancel", "login-error",
  "notification-panel", "notifications", "mobile-days", "calendar-search", "calendar-search-wrap"
].map(id => [id, document.getElementById(id)]));

const labels = {
  available: "Свободно",
  booked: "Занято",
  mine: "Моя бронь",
  held: "Ваш резерв",
  pending: "На согласовании",
  maintenance: "Обслуживание",
  external: "Внешний календарь",
  restricted: "По правилам",
  group: "Занятие",
  past: "Прошло"
};
const resourceIcons = { microscope: "◎", electronics: "ϟ", studio: "◉" };
const calendarResourceIcons = { room: "meeting_room", equipment: "settings" };
const state = {
  session: null, organizations: [], organizationId: null,
  resources: [], resourceId: null, calendar: null, selected: null, selectedDay: null, race: null, busy: false
};
let viewerPreference = savedTimeZone();
let viewerZone = viewerPreference || browserTimeZone;
const dateFormat = new Intl.DateTimeFormat("ru-RU", {
  month: "long", day: "numeric", timeZone: "UTC"
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

function symbol(name) {
  const icon = element("span", "symbol", name);
  icon.setAttribute("aria-hidden", "true");
  return icon;
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
    error.status = response.status;
    error.conflicts = problem.conflicts;
    error.templateConflicts = problem.templateConflicts;
    error.bookingForm = problem.bookingForm;
    error.questionErrors = problem.questionErrors;
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

function mondayOfDay(day) {
  return isoPlusDays(day, -((new Date(day).getUTCDay() + 6) % 7));
}

function weekOf(value, zone = viewerZone) {
  return mondayOfDay(zoneFormats(zone).day.format(new Date(value)));
}

function allSlots() {
  return state.calendar?.days.flatMap(day => day.slots) ?? [];
}

function selectedSlot() {
  return allSlots().find(slot => slot.startsAt === state.selected);
}

async function fetchCalendar(resourceId, week, organizationId = state.organizationId, timeZone = viewerZone, duration = 60,
  signal) {
  const query = new URLSearchParams({ resourceId, timeZone, durationMinutes: duration });
  const options = signal ? { signal, cache: "no-store" } : undefined;
  if (week) query.set("week", week);
  try { return await api(`/api/calendar?${query}`, options, organizationId); }
  catch (error) {
    if (error.status !== 400 || duration === 60) throw error;
    query.set("durationMinutes", "60");
    return api(`/api/calendar?${query}`, options, organizationId);
  }
}

async function loadCalendar(resourceId, week, retainSelection = false, organizationId = state.organizationId,
  duration = resourceId === state.resourceId ? state.calendar?.durationMinutes ?? 60 : 60, day = null) {
  const calendar = await fetchCalendar(resourceId, week, organizationId, viewerZone, duration);
  state.calendar = calendar;
  state.resourceId = resourceId;
  if (!retainSelection) state.selectedDay = day;
  reconcileCalendarSelection(retainSelection);
  if (!retainSelection) state.race = null;
}

function preferredCalendarSlot(slots) {
  return slots.find(slot => slot.status === "available") ?? slots.find(slot => slot.status !== "past") ?? slots[0];
}

function reconcileCalendarSelection(retainSelection) {
  if (retainSelection && selectedSlot()) return;
  const shownDay = state.calendar.days.find(day => day.date === state.selectedDay);
  const slot = preferredCalendarSlot(shownDay?.slots ?? allSlots());
  state.selected = slot?.startsAt ?? null;
  state.race = null;
}

async function pollWaitlist(force = false) {
  const previous = state.calendar;
  if (!previous || polling || state.busy ||
      (!force && !allSlots().some(slot => slot.waitlistId || slot.status === "held"))) return;

  polling = true;
  try {
    const calendar = await fetchCalendar(state.resourceId, previous.week, state.organizationId, viewerZone, previous.durationMinutes);
    if (state.busy || state.calendar !== previous || JSON.stringify(calendar) === JSON.stringify(previous)) return;

    const previousSlot = selectedSlot();
    const focusedSlot = document.activeElement?.dataset.startsAt;
    const focusedDay = document.activeElement?.dataset.day;
    state.calendar = calendar;
    reconcileCalendarSelection(true);
    renderCalendar();
    if (focusedSlot) calendarSlotControl(focusedSlot)?.focus({ preventScroll: true });
    else if (focusedDay) calendarDayControl(focusedDay)?.focus({ preventScroll: true });
    const slot = selectedSlot();
    if (JSON.stringify(slot) !== JSON.stringify(previousSlot) || calendar.requiresApproval !== previous.requiresApproval
      || calendar.resourceTimeZone !== previous.resourceTimeZone || calendar.priceMinor !== previous.priceMinor
      || calendar.publicLinkId !== previous.publicLinkId) {
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
      element("span", "", `${appointmentTime(start, viewerZone, true, item.endsAt)} · подтвердите до ${zoneFormats(viewerZone).time.format(new Date(item.holdExpiresAt))}`)
    );
    return control;
  }));
}

async function pollNotifications() {
  const { session, organizationId } = state;
  if (!session || !organizationId || state.busy || pollingNotifications) return;

  pollingNotifications = true;
  try {
    const items = await api(`/api/notifications?${new URLSearchParams({ timeZone: viewerZone })}`, undefined, organizationId);
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
    } else if ((slotFocus || focused?.closest(".details"))
      && !section("calendar-feedback").contains(document.activeElement)) {
      calendarSlotControl(state.selected)?.focus({ preventScroll: true });
    }
  }
}

async function changeCalendar(resourceId, week, day) {
  const previous = state.calendar;
  await run(async () => {
    await loadCalendar(resourceId, week, false, undefined, undefined, day);
    scrollSelectionIntoView();
  }, () => changeCalendar(resourceId, week, day));
  if (previous !== state.calendar) reveal(calendarView === "month" ? section("calendar-month-slots") : calendarViewPanel());
}

function render() {
  section("display-time-zone").disabled = state.busy;
  section("display-time-zone").value = viewerPreference;
  section("calendar-zone").textContent = timeZoneLabel(viewerZone);
  section("availability-zone").textContent = `Время поиска · ${timeZoneLabel(viewerZone)}`;
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
  ui["organization-role"].textContent = organizationRoleLabel(organization.role);
}

function isAdmin() {
  return state.organizations.some(item => item.id === state.organizationId && item.role === "admin");
}

function renderResources() {
  const selected = state.resources.find(resource => resource.id === state.resourceId);
  const picker = section("calendar-picker");
  picker.hidden = state.resources.length < 2;
  if (picker.hidden) picker.open = false;
  section("calendar-resource-icon").textContent = calendarResourceIcons[selected?.type] ?? "event_available";
  section("calendar-resource-name").textContent = selected?.name ?? "Нет доступных ресурсов";
  const locationCaption = section("calendar-resource-location");
  locationCaption.textContent = selected?.location ?? "";
  locationCaption.hidden = !locationCaption.textContent;
  ui["calendar-search-wrap"].hidden = picker.hidden;
  const query = ui["calendar-search-wrap"].hidden ? "" : ui["calendar-search"].value.trim().toLocaleLowerCase("ru");
  const resources = state.resources.filter(resource =>
    `${resource.name} ${resource.location}`.toLocaleLowerCase("ru").includes(query));
  const buttons = resources.map(resource => {
    const item = button("resource", undefined, () => {
      picker.open = false;
      section("calendar-picker-toggle").focus({ preventScroll: true });
      if (resource.id !== state.resourceId) changeCalendar(resource.id, state.calendar?.week);
    });
    item.dataset.resourceId = resource.id;
    item.disabled = state.busy;
    item.setAttribute("aria-pressed", resource.id === state.resourceId);
    const icon = symbol(calendarResourceIcons[resource.type] ?? "event_available");
    const description = element("span", "calendar-picker-description");
    description.append(
      element("span", "resource-name", resource.name),
      element("span", "resource-location", resource.location)
    );
    item.append(icon, description);
    if (resource.id === state.resourceId) {
      const check = element("span", "calendar-picker-check", "✓");
      check.setAttribute("aria-hidden", "true");
      item.append(check);
    }
    return item;
  });
  ui.resources.replaceChildren(...(buttons.length ? buttons : (state.organizationId
    ? [element("p", "list-empty", query ? "По запросу ничего не найдено." : "Нет доступных ресурсов.")] : [])));
}

function renderCalendar() {
  const calendar = state.calendar;
  section("external-calendar-legend").hidden = !allSlots().some(slot => slot.status === "external");
  section("group-session-legend").hidden = !allSlots().some(slot => slot.status === "group");
  const policyCaption = section("calendar-policy");
  policyCaption.textContent = [bookingPolicySummary(calendar?.policy), calendar?.bookingMessage].filter(Boolean).join(" · ");
  policyCaption.hidden = !policyCaption.textContent;
  const durationControl = section("calendar-duration");
  const flexible = calendar?.durationOptions.length > 1;
  section("calendar-duration-bar").hidden = !flexible;
  ui.week.parentElement.classList.toggle("flexible-slots", flexible);
  if (calendar) {
    fillDurations(durationControl, calendar.durationOptions, calendar.durationMinutes);
    durationControl.disabled = state.busy;
    const { bufferBeforeMinutes, bufferAfterMinutes } = calendar;
    section("calendar-buffer").textContent = bufferBeforeMinutes || bufferAfterMinutes
      ? `Буфер · ${bufferBeforeMinutes} мин до и ${bufferAfterMinutes} мин после` : "Старт каждые 15 минут";
  }
  section("calendar-check-time").disabled = state.busy || !calendar;
  if (calendar) {
    const { days, today, resourceTimeZone } = calendar;
    const day = days.find(item => item.slots.some(slot => slot.startsAt === state.selected))
      ?? days.find(item => item.date === state.selectedDay)
      ?? days.find(item => item.date === today) ?? days[0];
    state.selectedDay = day.date;
    section("calendar-zone").title = `Часовой пояс ресурса · ${timeZoneLabel(resourceTimeZone)}`;
  }
  renderCalendarView();
}

function renderWeekSchedule() {
  const calendar = state.calendar;
  const mobileTabs = [];
  const columns = calendar.days.map((day, index) => {
    const column = element("div", day.date === calendar.today ? "day current" : "day");
    column.dataset.date = day.date;
    const dayButton = button("mobile-day", undefined, () => {
      selectSlot(preferredCalendarSlot(day.slots), day.date);
    });
    dayButton.dataset.day = day.date;
    dayButton.disabled = state.busy;
    dayButton.setAttribute("aria-label", dateFormat.format(new Date(day.date)));
    if (day.date === calendar.today) dayButton.setAttribute("aria-current", "date");
    dayButton.append(element("span", "", weekdays[index]), element("strong", "", Number(day.date.slice(-2))));
    mobileTabs.push(dayButton);
    const heading = element("div", "day-heading");
    heading.append(element("span", "weekday", weekdays[index]), element("span", "day-number", Number(day.date.slice(-2))));
    const slots = element("div", "slots");
    slots.append(...calendarSlotButtons(day));
    column.append(heading, slots);
    return column;
  });
  ui.week.replaceChildren(...columns);
  ui["mobile-days"].replaceChildren(...mobileTabs);
  updateWeekDay();
}

function calendarSlotButtons(day, showRange = false) {
  if (!day.slots.length) {
    const inspect = button("slot-closed slot-check-day", "Нет слотов", () => showSlotDiagnostics({ date: day.date }));
    inspect.disabled = state.busy;
    inspect.setAttribute("aria-label", `Проверить время · ${dateFormat.format(new Date(day.date))}`);
    return [inspect];
  }
  const formats = zoneFormats(viewerZone);
  const times = day.slots.map(slot => formats.time.format(new Date(slot.startsAt)));
  return day.slots.map((slot, index) => {
    const time = times[index];
    const duplicate = times.indexOf(time) !== times.lastIndexOf(time);
    const offset = utcOffset(slot.startsAt, viewerZone);
    const item = button(`slot ${slot.status}`, undefined, () => selectSlot(slot, day.date));
    item.dataset.startsAt = slot.startsAt;
    item.disabled = state.busy;
    item.setAttribute("aria-pressed", slot.startsAt === state.selected);
    const label = slot.waitlistId ? "В очереди" : labels[slot.status];
    const range = showRange ? slotTimeRange(slot.startsAt, viewerZone, slot.endsAt) : time;
    item.setAttribute("aria-label", `${formats.longDate.format(new Date(slot.startsAt))}, ${showRange ? range : `${time} ${offset}`}. ${label}`);
    item.append(element("span", "slot-time", range), element("span", "slot-state", label));
    if (duplicate && !showRange) item.append(element("span", "slot-offset", offset));
    return item;
  });
}

function updateWeekDay() {
  for (const column of ui.week.children)
    column.dataset.mobileActive = String(column.dataset.date === state.selectedDay);
  for (const tab of ui["mobile-days"].children)
    tab.setAttribute("aria-pressed", String(tab.dataset.day === state.selectedDay));
}

function selectSlot(slot, day) {
  const start = slot?.startsAt ?? null;
  const changed = start !== state.selected;
  const previousDay = state.selectedDay;
  if (changed) state.race = null;
  state.selected = start;
  state.selectedDay = day ?? state.calendar.days.find(item => item.slots.includes(slot)).date;
  syncCalendarViewSelection(previousDay);
  renderDetails();
  if (changed) reveal(ui.details, 5);
}

function renderDetails() {
  const slot = selectedSlot();
  syncCalendarFeedback();
  ui.details.replaceChildren();
  ui["race-panel"].hidden = !slot || state.calendar?.priceMinor != null || (slot.status !== "available" && !state.race);
  if (!slot) {
    if (state.selectedDay)
      ui.details.append(element("p", "details-date", dateFormat.format(new Date(state.selectedDay))),
        element("p", "details-date", "На этот день нет слотов."));
    return;
  }

  const start = new Date(slot.startsAt);
  const end = new Date(slot.endsAt);
  const clock = zoneFormats(viewerZone).time;
  const fromOffset = utcOffset(start, viewerZone);
  const toOffset = utcOffset(end, viewerZone);
  const displayTime = element("p", "details-time", `${clock.format(start)}–${clock.format(end)}`);
  displayTime.append(element("span", "details-offset", fromOffset === toOffset ? toOffset : `${fromOffset} → ${toOffset}`));
  const section = element("div", "details-slot");
  const resource = state.resources.find(item => item.id === state.resourceId);
  section.append(
    element("span", `pill ${slot.status}`, labels[slot.status]),
    element("p", "details-date", zoneFormats(viewerZone).longDate.format(start)),
    displayTime
  );
  if (resource) {
    const context = element("div", "slot-resource");
    const identity = element("div", "data-title");
    identity.append(element("strong", "", resource.name), element("span", "", resource.location));
    const resourceZone = state.calendar.resourceTimeZone;
    if (resourceZone !== viewerZone)
      identity.append(element("span", "", `${timeZoneLabel(resourceZone)} · ${slotTimeRange(start, resourceZone, end)}`));
    context.append(symbol("meeting_room"), identity);
    section.append(context);
  }
  if (slot.groupSessionId) {
    section.append(element("strong", "group-calendar-title", slot.groupTitle),
      button("page-action", "Открыть занятие", () => openGroupSession(slot.groupSessionId)));
  } else renderSlotActions(section, slot);
  if (slot.answerBookingId) {
    const answers = button("outline-action", "Ответы гостя", () => openCalendarAnswers(slot.answerBookingId, resource?.name));
    answers.disabled = state.busy;
    section.append(answers);
  }
  if (slot.status !== "available") {
    const inspect = button("outline-action slot-check-action", "Почему недоступно?",
      () => showSlotDiagnostics({ startsAt: slot.startsAt }));
    inspect.disabled = state.busy;
    section.append(inspect);
  }
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

function showSlotDiagnostics({ startsAt, date } = {}) {
  const { resourceId, calendar } = state;
  if (!calendar || state.busy) return;
  const context = calendarBookingContext();
  const { timeZone, duration } = context;
  const slot = startsAt ?? (date ? null : selectedSlot()?.startsAt);
  const local = zoneFormats(timeZone);
  openSlotDiagnostics({
    resourceName: state.resources.find(item => item.id === resourceId)?.name,
    duration,
    timeZone,
    resourceZone: calendar.resourceTimeZone,
    firstDay: calendar.firstWeek,
    lastDay: isoPlusDays(calendar.lastWeek, 6),
    date: date ?? (slot ? local.day.format(new Date(slot)) : state.selectedDay ?? calendar.today),
    time: slot ? local.time.format(new Date(slot)) : "09:00",
    startsAt,
    check: (parameters, signal) => checkCalendarSlot(context, parameters, signal),
    choose: async (start, signal) => {
      const choice = await readAvailableCalendarSlot(context, start, signal);
      if (signal.aborted || !sameCalendarContext(context) || state.busy || !choice) return false;
      applyAvailableCalendarSlot(choice);
      ui.status.textContent = "Свободный слот выбран.";
      return true;
    }
  });
}

section("calendar-check-time").addEventListener("click", () => showSlotDiagnostics());

function renderSlotActions(section, slot) {
  function action(css, label, url, options, message) {
    const control = button(css, label, () => updateBooking(url, options, message));
    control.disabled = state.busy;
    section.append(control);
    return control;
  }

  const booking = `/api/bookings/${slot.bookingId}`;
  const remove = { method: "DELETE" };
  const requiresApproval = state.calendar?.requiresApproval;
  if ((slot.status === "mine" || slot.status === "pending") && slot.changes?.canReschedule !== false) {
    const resource = state.resources.find(item => item.id === state.resourceId);
    const move = button("button secondary", "Перенести", () => showWorkspaceReschedule({
      id: slot.bookingId, resourceName: resource?.name, startsAt: slot.startsAt, endsAt: slot.endsAt
    }));
    move.disabled = state.busy;
    section.append(move);
  }
  const deadlines = bookingDeadlines(slot.changes, slot.startsAt, viewerZone);
  if (deadlines) section.append(element("p", "details-date", deadlines));

  switch (slot.status) {
    case "available":
      if (state.calendar.priceMinor != null) {
        section.append(element("p", "details-date", `Демо-оплата · ${bookingPrice(state.calendar.priceMinor)} за бронь`));
        if (state.calendar.publicLinkId) {
          const guestPage = element("a", "outline-action", "Открыть страницу бронирования");
          guestPage.href = `/book/${state.calendar.publicLinkId}`;
          section.append(guestPage);
        } else section.append(element("p", "details-date", "Откройте ссылку на ресурс, выданную администратором."));
        if (isAdmin()) section.append(maintenanceForm(slot));
        break;
      }
      const book = button("button primary", requiresApproval ? "Отправить заявку" : "Забронировать", () => bookCalendarSlot(slot));
      book.disabled = state.busy;
      section.append(book);
      const series = button("button secondary", "Повторять каждую неделю", () => showWorkspaceSeries(slot));
      series.disabled = state.busy;
      section.append(series);
      if (isAdmin()) section.append(maintenanceForm(slot));
      break;
    case "pending":
      if (slot.bundleId) {
        section.append(bundleAgendaLink());
        break;
      }
      section.append(element("p", "details-date", "Статус и причина решения появятся в «Моих записях»."));
      if (slot.changes?.canCancel !== false) action("button secondary cancel", "Отозвать заявку", booking, remove, "Заявка отозвана.");
      break;
    case "mine":
      if (slot.bundleId) {
        section.append(bundleAgendaLink());
        break;
      }
      if (slot.changes?.canCancel !== false) action("button secondary cancel", "Отменить бронь", booking, remove, "Бронь отменена.");
      break;
    case "booked":
      if (slot.waitlistId) {
        section.append(element("p", "details-date", `Место в очереди — ${slot.waitlistPosition}`));
        action("button secondary cancel", "Выйти из очереди", `/api/waitlist/${slot.waitlistId}`, remove, "Вы вышли из очереди.");
      } else if (slot.bookingRestriction) {
        section.append(element("p", "details-date", slot.bookingRestriction));
      } else if (state.calendar.priceMinor == null) {
        action("button secondary cancel", "Встать в очередь", "/api/waitlist", bookingPayload(slot), "Вы в листе ожидания.");
      }
      break;
    case "restricted":
      section.append(element("p", "details-date", slot.bookingRestriction));
      break;
    case "external":
      section.append(element("p", "details-date", "Это время занято в подключённом Google Calendar."));
      break;
    case "held": {
      const countdown = element("p", "details-date hold-countdown");
      countdown.dataset.expiresAt = slot.holdExpiresAt;
      section.append(countdown);
      const confirm = button("button primary", requiresApproval ? "Отправить заявку" : "Подтвердить бронь",
        () => confirmBookingHold(slot.bookingId, state.resourceId, state.calendar.bookingForm, requiresApproval));
      confirm.dataset.holdConfirm = "";
      section.append(confirm);
      action("button secondary cancel", "Отказаться от резерва", booking, remove, "Резерв освобождён.");
      break;
    }
    case "maintenance":
      section.append(element("p", "details-date", slot.maintenanceReason));
      if (slot.maintenanceStartsAt) section.append(element("p", "details-date", `${slotTimeRange(slot.maintenanceStartsAt, viewerZone, slot.maintenanceEndsAt)}`));
      if (isAdmin() && slot.maintenanceId) {
        action("button secondary cancel", "Снять блокировку", `/api/maintenance/${slot.maintenanceId}`,
          remove, "Слот снова доступен.");
      }
      break;
  }
}

function bundleAgendaLink() {
  const link = element("a", "text-link", "Управлять комплектом в «Моих записях»");
  link.href = "#mine";
  return link;
}

function showWorkspaceSeries(slot) {
  const path = `/api/resources/${encodeURIComponent(state.resourceId)}/series`;
  const organization = state.organizationId;
  openWeeklySeries({
    startsAt: slot.startsAt,
    durationMinutes: state.calendar.durationMinutes,
    bookingForm: state.calendar.bookingForm,
    send: (suffix, options) => request(path + suffix, options, organization),
    onBooked: result => run(async () => {
      await refreshCalendar();
      await loadWorkspace();
      ui.status.textContent = result.bookings.some(item => item.approvalStatus === "pending")
        ? `На согласовании: ${result.bookings.length} записей.`
        : `Серия сохранена. В «Моих записях» появилось ${result.bookings.length} броней.`;
    })
  });
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
    body: JSON.stringify({ resourceId: state.resourceId, startsAt: slot.startsAt, durationMinutes: state.calendar.durationMinutes, ...fields })
  };
}

function refreshCalendar() {
  return state.calendar ? loadCalendar(state.resourceId, state.calendar.week, true) : Promise.resolve();
}

function withBookingQuestions({ resourceId, form, context, label, submit, onBooked, onError }) {
  const failure = onError ?? (async error => { throw error; });
  const open = current => openBookingQuestionDialog({
    form: current, context, label, submit,
    onBooked: result => run(() => onBooked(result)),
    onError: error => run(() => failure(error))
  });
  return run(async () => {
    let current = form;
    if (!current) {
      try {
        current = await api(`/api/resources/${encodeURIComponent(resourceId)}/booking-form`);
      } catch (error) {
        await failure(error);
        return;
      }
    }
    if (current.questions.length) {
      open(current);
      return;
    }
    let result;
    try {
      result = await submit(null);
    } catch (error) {
      if (error.bookingForm) open(error.bookingForm);
      else await failure(error);
      return;
    }
    await onBooked(result);
  });
}

async function finishCalendarBooking(result, message, context) {
  state.race = null;
  const pending = result?.approvalStatus === "pending";
  const text = pending ? "Заявка отправлена на согласование." : message;
  if (context) {
    setCalendarFeedback(context, pending ? "review" : "success", text);
    focusCalendarFeedback();
  } else ui.status.textContent = text;
  await refreshCalendar();
  await loadWorkspace();
}

async function recoverCalendarBooking(error, context, allowAlternatives = false) {
  if (!context) {
    await refreshCalendar();
    await loadWorkspace();
    throw error;
  }
  const message = error instanceof TypeError ? "Не удалось связаться с сервером. Повторите попытку." : error.message;
  const conflict = allowAlternatives && error.status === 409;
  setCalendarFeedback(context, conflict ? "conflict" : "error", message, [],
    conflict ? "Проверяем ближайшие свободные слоты…" : "");
  focusCalendarFeedback();
  if (conflict) {
    await Promise.allSettled([showCalendarAlternatives(context, message), refreshCalendar()]);
  } else await refreshCalendar().catch(() => {});
}

function bookCalendarSlot(slot) {
  const { calendar, resourceId, organizationId } = state;
  const context = calendarBookingContext(slot.startsAt);
  const payload = { resourceId, startsAt: slot.startsAt, durationMinutes: calendar.durationMinutes };
  return withBookingQuestions({
    resourceId, form: calendar.bookingForm, context: state.resources.find(item => item.id === resourceId)?.name,
    label: calendar.requiresApproval ? "Отправить заявку" : "Забронировать",
    submit: bookingForm => submitCalendarChange(context,
      () => api("/api/bookings", jsonRequest("POST", { ...payload, bookingForm }), organizationId)),
    onBooked: result => finishCalendarBooking(result, "Бронирование подтверждено.", context),
    onError: error => recoverCalendarBooking(error, context, true)
  });
}

function confirmBookingHold(id, resourceId, form, requiresApproval) {
  const organization = state.organizationId;
  const context = currentScreen() === "calendar" && selectedSlot()?.bookingId === id ? calendarBookingContext() : null;
  return withBookingQuestions({
    resourceId, form, context: state.resources.find(item => item.id === resourceId)?.name,
    label: requiresApproval ? "Отправить заявку" : "Подтвердить бронь",
    submit: answers => submitCalendarChange(context, () => api(`/api/bookings/${id}/confirm`,
      answers ? jsonRequest("POST", answers) : { method: "POST" }, organization)),
    onBooked: result => finishCalendarBooking(result, requiresApproval ? "Заявка отправлена на согласование." : "Бронирование подтверждено.", context),
    onError: error => recoverCalendarBooking(error, context)
  });
}

function openCalendarAnswers(id, context) {
  return run(async () => {
    const answers = await api(`/api/bookings/${id}/answers`);
    const dialog = questionDialog("Ответы гостя", context);
    dialog.querySelector("[data-fields]").append(renderBookingAnswers(answers));
    dialog.querySelector("[data-save]").remove();
    document.body.append(dialog);
    dialog.showModal();
  });
}

function updateBooking(url, options, message) {
  const context = currentScreen() === "calendar" && selectedSlot() ? calendarBookingContext() : null;
  return run(async () => {
    state.race = null;
    let result;
    try {
      result = await submitCalendarChange(context, () => api(url, options));
    } catch (error) {
      await recoverCalendarBooking(error, context);
      return;
    }
    await finishCalendarBooking(result, message, context);
  }, () => run(refreshCalendar));
}

function raceForSlot() {
  const slot = selectedSlot();
  if (slot?.status !== "available") return;
  return withBookingQuestions({
    resourceId: state.resourceId, form: state.calendar.bookingForm, label: "Запустить 20 запросов",
    submit: bookingForm => Promise.allSettled(Array.from({ length: 20 }, async () => {
      const response = await request("/api/bookings", bookingPayload(slot, { bookingForm }));
      const body = await response.json().catch(() => null);
      if (body?.bookingForm) throw Object.assign(new Error(body.title), body);
      return response.status;
    })).then(results => {
      const stale = results.find(result => result.status === "rejected" && result.reason?.bookingForm);
      if (stale && !results.some(result => result.status === "fulfilled" && result.value === 201)) throw stale.reason;
      return results;
    }),
    onBooked: async results => {
      const created = results.filter(result => result.status === "fulfilled" && result.value === 201).length;
      const conflicts = results.filter(result => result.status === "fulfilled" && result.value === 409).length;
      state.race = { created, conflicts, other: results.length - created - conflicts };
      await refreshCalendar();
    }
  });
}

async function enterOrganization(id) {
  const resources = await api("/api/resources", undefined, id);
  if (resources.length) await loadCalendar(resources[0].id, undefined, false, id);
  else Object.assign(state, { resourceId: null, calendar: null, selected: null, selectedDay: null, race: null });
  state.resources = resources;
  if (id !== state.organizationId) ui["calendar-search"].value = "";
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
    resources: [], resourceId: null, calendar: null, selected: null, selectedDay: null, race: null
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
  return run(async () => {
    await enterSession(await api("/api/session", { method: "POST" }));
    openWorkspaceEntry();
  }, start);
}

async function changeViewerZone(preference) {
  const zone = preference || browserTimeZone;
  let calendar = state.calendar;
  if (calendar && zone !== viewerZone) {
    const week = state.selected ? weekOf(state.selected, zone) : calendar.week;
    try {
      calendar = await fetchCalendar(state.resourceId, week, state.organizationId, zone, calendar.durationMinutes);
    } catch (error) {
      if (error.status !== 400) throw error;
      calendar = await fetchCalendar(state.resourceId, undefined, state.organizationId, zone, calendar.durationMinutes);
    }
  }
  viewerPreference = preference;
  saveTimeZone(preference);
  if (zone === viewerZone) return;
  viewerZone = zone;
  state.selectedDay = null;
  state.calendar = calendar;
  if (calendar) reconcileCalendarSelection(true);
  workspace.week = null;
  workspace.agenda = null;
  renderNotifications([]);
  await availabilityZoneChanged();
  await loadWorkspace();
  scrollSelectionIntoView();
}

const viewerSelector = section("display-time-zone");
section("calendar-duration").addEventListener("change", event => {
  const duration = Number(event.currentTarget.value);
  run(() => loadCalendar(state.resourceId, state.calendar.week, true, state.organizationId, duration));
});
fillTimeZones(viewerSelector, viewerPreference, true);
viewerSelector.addEventListener("change", event => {
  const preference = event.currentTarget.value;
  run(() => changeViewerZone(preference));
});

ui.organization.addEventListener("change", event => {
  const id = event.currentTarget.value;
  run(() => enterOrganization(id), () => run(() => enterOrganization(id)));
});
function showDemoLogin(login) {
  const form = ui["login-form"];
  if (login) form.elements.login.value = login;
  ui["login-error"].hidden = true;
  ui["login-dialog"].showModal();
  if (login) form.elements.password.focus();
}

ui["session-button"].addEventListener("click", () => {
  if (state.session?.isDemo) {
    run(async () => enterSession(await api("/api/session/guest", { method: "POST" })), start);
    return;
  }
  showDemoLogin();
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
ui["race-button"].addEventListener("click", raceForSlot);
ui["calendar-search"].addEventListener("input", renderResources);
const calendarPicker = section("calendar-picker");
calendarPicker.addEventListener("toggle", () => {
  if (calendarPicker.open || !ui["calendar-search"].value) return;
  ui["calendar-search"].value = "";
  renderResources();
});
calendarPicker.addEventListener("keydown", event => {
  if (event.key !== "Escape" || !calendarPicker.open) return;
  calendarPicker.open = false;
  section("calendar-picker-toggle").focus({ preventScroll: true });
});
document.addEventListener("click", event => {
  if (calendarPicker.open && !calendarPicker.contains(event.target)) calendarPicker.open = false;
});
window.addEventListener("hashchange", () => { calendarPicker.open = false; });
setInterval(() => { if (!liveReady) pollWaitlist(); }, 2000);
setInterval(pollNotifications, 2000);
setInterval(updateCountdown, 1000);
bindWorkspace();
showGoogleCalendarFeedback();
bindAvailability();
bindCalendarViews();
bindWeekScene();
bindAccounts();
start();
