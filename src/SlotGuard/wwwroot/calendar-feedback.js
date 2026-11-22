let calendarFeedback = null;

function calendarBookingContext(startsAt = state.selected) {
  const { organizationId, resourceId, calendar } = state;
  return { organizationId, resourceId, startsAt, duration: calendar.durationMinutes, timeZone: viewerZone };
}

function sameCalendarContext(context) {
  return state.organizationId === context.organizationId && state.resourceId === context.resourceId
    && state.calendar?.durationMinutes === context.duration && viewerZone === context.timeZone;
}

function clearCalendarFeedback() {
  calendarFeedback = null;
  section("calendar-feedback").hidden = true;
  section("calendar-feedback-message").textContent = "";
  section("calendar-feedback-list").replaceChildren();
}

function syncCalendarFeedback() {
  if (calendarFeedback && (!sameCalendarContext(calendarFeedback.context)
    || calendarFeedback.context.startsAt !== state.selected)) clearCalendarFeedback();
  for (const item of section("calendar-feedback-list").children) item.disabled = state.busy;
}

function setCalendarFeedback(context, tone, message, alternatives = [], note = "") {
  if (!sameCalendarContext(context) || context.startsAt !== state.selected) return;
  const panel = section("calendar-feedback");
  const status = section("calendar-feedback-message");
  const changed = panel.hidden || panel.dataset.tone !== tone || status.textContent !== message;
  calendarFeedback = { context, alternatives };
  panel.hidden = false;
  panel.dataset.tone = tone;
  if (status.textContent !== message) status.textContent = message;
  section("calendar-feedback-options").hidden = !alternatives.length;
  const formats = zoneFormats(context.timeZone);
  section("calendar-feedback-list").replaceChildren(...alternatives.map(({ startsAt, endsAt }) => {
    const start = new Date(startsAt);
    const end = new Date(endsAt);
    const time = appointmentTime(startsAt, context.timeZone, true, endsAt);
    const hours = utcOffset(start, context.timeZone) === utcOffset(end, context.timeZone)
      ? `${formats.time.format(start)}–${formats.time.format(end)}` : slotTimeRange(start, context.timeZone, end);
    const label = `${formats.shortDate.format(start)} · ${hours}`;
    const control = button("outline-action", label, () => chooseCalendarFeedbackOption(context, startsAt));
    control.setAttribute("aria-label", `Выбрать свободное время · ${time}`);
    return control;
  }));
  const caption = section("calendar-feedback-note");
  caption.textContent = note;
  caption.hidden = !note;
  syncCalendarFeedback();
  if (changed) reveal(panel, 3);
}

async function submitCalendarChange(context, submit) {
  if (context) setCalendarFeedback(context, "pending", "Сохраняем…");
  try {
    return await submit();
  } catch (error) {
    if (context && calendarFeedback?.context === context) clearCalendarFeedback();
    throw error;
  }
}

function focusCalendarFeedback() {
  if (currentScreen() !== "calendar" || section("calendar-feedback").hidden) return;
  const message = section("calendar-feedback-message");
  message.focus({ preventScroll: true });
  message.scrollIntoView({ block: "nearest", behavior: reducedMotion.matches ? "auto" : "smooth" });
}

function checkCalendarSlot(context, parameters, signal) {
  const { resourceId, organizationId, duration, timeZone } = context;
  const query = new URLSearchParams({ durationMinutes: duration, timeZone, ...parameters });
  return api(`/api/resources/${encodeURIComponent(resourceId)}/slot-diagnostics?${query}`,
    { cache: "no-store", signal }, organizationId);
}

async function readAvailableCalendarSlot(context, startsAt, signal) {
  const { resourceId, organizationId, duration, timeZone } = context;
  const week = weekOf(startsAt, timeZone);
  const query = new URLSearchParams({ resourceId, week, timeZone, durationMinutes: duration });
  const calendar = await api(`/api/calendar?${query}`, { cache: "no-store", signal }, organizationId);
  const time = Date.parse(startsAt);
  const day = calendar.days.find(item => item.slots.some(slot => Date.parse(slot.startsAt) === time));
  const slot = day?.slots.find(item => Date.parse(item.startsAt) === time);
  return slot?.status === "available" ? { calendar, slot, date: day.date } : null;
}

function applyAvailableCalendarSlot({ calendar, slot, date }) {
  state.calendar = calendar;
  state.selected = slot.startsAt;
  state.selectedDay = date;
  state.race = null;
  renderCalendar();
  renderDetails();
  reveal(ui.details);
  scrollSelectionIntoView();
  requestAnimationFrame(() => (calendarSlotControl(state.selected)
    ?? calendarDayControl(state.selectedDay))?.focus({ preventScroll: true }));
}

async function showCalendarAlternatives(context, message) {
  try {
    const view = await checkCalendarSlot(context, { startsAt: context.startsAt });
    const note = view.alternatives.length ? "" : view.bookingMessage ?? "В ближайшее время свободных слотов нет.";
    setCalendarFeedback(context, "conflict", message, view.alternatives, note);
  } catch {
    setCalendarFeedback(context, "conflict", message, [], "Не удалось загрузить варианты. Проверьте время ещё раз.");
  }
}

function chooseCalendarFeedbackOption(context, startsAt) {
  return run(async () => {
    let choice;
    try {
      choice = await readAvailableCalendarSlot(context, startsAt);
    } catch (error) {
      setCalendarFeedback(context, "error",
        error instanceof TypeError ? "Не удалось проверить время. Повторите попытку." : error.message,
        calendarFeedback?.alternatives);
      focusCalendarFeedback();
      return;
    }
    if (!sameCalendarContext(context) || state.selected !== context.startsAt) return;
    if (!choice) {
      await showCalendarAlternatives(context, "Этот вариант уже недоступен. Список обновлён.");
      focusCalendarFeedback();
      return;
    }
    applyAvailableCalendarSlot(choice);
    ui.status.textContent = "Свободный слот выбран.";
  });
}
