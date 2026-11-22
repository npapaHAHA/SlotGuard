const calendarPanels = {
  month: "calendar-month", week: "calendar-schedule", list: "calendar-list", map: "week-scene"
};
const calendarMonthFormat = new Intl.DateTimeFormat("ru-RU", { month: "long", year: "numeric", timeZone: "UTC" });
const calendarDayFormat = new Intl.DateTimeFormat("ru-RU", { weekday: "long", day: "numeric", month: "long", timeZone: "UTC" });
let calendarView = "week";
let calendarMonthSnapshot = null;

function calendarViewPanel() {
  return section(calendarPanels[calendarView]);
}

function calendarSlotControl(startsAt) {
  if (!startsAt) return null;
  return [...calendarViewPanel().querySelectorAll("[data-starts-at]")].find(item => item.dataset.startsAt === startsAt);
}

function calendarDayControl(date) {
  return [...calendarViewPanel().querySelectorAll("button[data-day]")].find(item => item.dataset.day === date);
}

function renderCalendarView() {
  const calendar = state.calendar;
  for (const [view, panel] of Object.entries(calendarPanels)) {
    const active = view === calendarView;
    const control = section(`calendar-view-${view}`);
    control.disabled = !calendar || state.busy;
    control.setAttribute("aria-pressed", String(active));
    section(panel).hidden = !active || !calendar;
  }
  renderCalendarPeriod();
  if (!calendar) {
    clearCalendarMonth();
    return;
  }
  if (calendarView === "week") renderWeekSchedule();
  else if (calendarView === "list") renderCalendarList();
  else if (calendarView === "map") {
    renderWeekScene();
    updateSceneSelection();
  } else renderCalendarMonth();
}

function renderCalendarPeriod() {
  const calendar = state.calendar;
  const monthly = calendarView === "month";
  const date = state.selectedDay;
  const month = date?.slice(0, 7);
  const lastDay = calendar ? isoPlusDays(calendar.lastWeek, 6) : null;
  ui.prev.disabled = state.busy || !calendar || (monthly ? month <= calendar.firstWeek.slice(0, 7) : calendar.week === calendar.firstWeek);
  ui.next.disabled = state.busy || !calendar || (monthly ? month >= lastDay.slice(0, 7) : calendar.week === calendar.lastWeek);
  ui.today.disabled = state.busy || !calendar || date === calendar.today;
  ui.prev.setAttribute("aria-label", monthly ? "Предыдущий месяц" : "Предыдущая неделя");
  ui.next.setAttribute("aria-label", monthly ? "Следующий месяц" : "Следующая неделя");
  let label = state.busy ? "Загрузка…" : "Выберите доступный ресурс";
  if (calendar) {
    const last = new Date(calendar.days.at(-1).date);
    label = monthly ? calendarMonthFormat.format(new Date(`${month}-01`))
      : `${dateFormat.formatRange(new Date(calendar.week), last)} ${last.getUTCFullYear()}`;
  }
  if (ui["week-label"].textContent !== label) ui["week-label"].textContent = label;
}

function renderCalendarList() {
  const days = state.calendar.days.map(day => {
    const group = element("section", "calendar-list-day");
    group.dataset.date = day.date;
    const heading = element("h3", "calendar-day-title", calendarDayFormat.format(new Date(day.date)));
    if (day.date === state.calendar.today) heading.append(element("span", "", "Сегодня"));
    const slots = element("div", "calendar-list-rows");
    slots.append(...calendarSlotButtons(day, true));
    group.append(heading, slots);
    return group;
  });
  section("calendar-list").replaceChildren(...days);
  markCalendarListDay();
}

function markCalendarListDay() {
  for (const group of section("calendar-list").children)
    group.dataset.active = String(group.dataset.date === state.selectedDay);
}

function monthFollowing(month, direction = 1) {
  const date = new Date(`${month}-01`);
  date.setUTCMonth(date.getUTCMonth() + direction);
  return date.toISOString().slice(0, 10);
}

function changeCalendarPeriod(direction) {
  const calendar = state.calendar;
  if (!calendar || state.busy) return;
  if (calendarView !== "month") {
    changeCalendar(state.resourceId, isoPlusDays(calendar.week, direction * 7));
    return;
  }
  const first = monthFollowing(state.selectedDay.slice(0, 7), direction);
  const date = first < calendar.firstWeek ? calendar.firstWeek : first;
  chooseCalendarDay(date);
}

async function chooseCalendarDay(date) {
  if (state.busy || date === state.selectedDay) return;
  const day = state.calendar.days.find(item => item.date === date);
  if (day) selectSlot(preferredCalendarSlot(day.slots), day.date);
  else {
    const focused = calendarDayControl(date) === document.activeElement;
    await changeCalendar(state.resourceId, mondayOfDay(date), date);
    if (focused && calendarView === "month")
      calendarDayControl(state.selectedDay)?.focus({ preventScroll: true });
  }
}

function clearCalendarMonth() {
  calendarMonthSnapshot?.controller.abort();
  calendarMonthSnapshot = null;
}

function calendarMonthKey(month) {
  const { durationMinutes, firstWeek, lastWeek } = state.calendar;
  return [state.organizationId, state.resourceId, viewerZone, durationMinutes, firstWeek, lastWeek, month].join("|");
}

function storeCalendarMonthDays(snapshot, days) {
  for (const day of days)
    snapshot.free.set(day.date, day.slots.filter(slot => slot.status === "available").length);
}

function renderCalendarMonth() {
  const month = state.selectedDay.slice(0, 7);
  if (calendarMonthSnapshot?.key !== calendarMonthKey(month)) loadCalendarMonth(month);
  else {
    storeCalendarMonthDays(calendarMonthSnapshot, state.calendar.days);
    renderCalendarMonthGrid(month, calendarMonthSnapshot);
  }
  renderCalendarMonthDay();
}

function loadCalendarMonth(month) {
  const key = calendarMonthKey(month);
  const previous = calendarMonthSnapshot?.key === key ? calendarMonthSnapshot : null;
  clearCalendarMonth();
  const calendar = state.calendar;
  const controller = new AbortController();
  const snapshot = { key, controller, free: previous?.free ?? new Map(), status: previous?.status === "ready" ? "ready" : "loading" };
  calendarMonthSnapshot = snapshot;
  storeCalendarMonthDays(snapshot, calendar.days);
  const weeks = [];
  const first = mondayOfDay(`${month}-01`);
  const next = monthFollowing(month);
  for (let week = first < calendar.firstWeek ? calendar.firstWeek : first;
    week < next && week <= calendar.lastWeek; week = isoPlusDays(week, 7)) {
    if (week !== calendar.week) weeks.push(week);
  }
  if (!weeks.length) snapshot.status = "ready";
  renderCalendarMonthGrid(month, snapshot);
  if (!weeks.length) return;
  const { resourceId, organizationId } = state;
  const duration = calendar.durationMinutes;
  Promise.all(weeks.map(week => fetchCalendar(resourceId, week, organizationId, viewerZone, duration, controller.signal)))
    .then(results => {
      if (controller.signal.aborted) return;
      for (const result of results) storeCalendarMonthDays(snapshot, result.days);
      snapshot.status = "ready";
    })
    .catch(() => { snapshot.status = "error"; })
    .finally(() => {
      if (controller.signal.aborted || calendarView !== "month" || !state.calendar
        || calendarMonthKey(state.selectedDay.slice(0, 7)) !== snapshot.key) return;
      storeCalendarMonthDays(snapshot, state.calendar.days);
      renderCalendarMonthGrid(month, snapshot);
    });
}

function refreshCalendarMonth() {
  if (calendarView === "month" && state.calendar && currentScreen() === "calendar") {
    if (!state.busy) loadCalendarMonth(state.selectedDay.slice(0, 7));
  } else clearCalendarMonth();
}

function renderCalendarMonthGrid(month, snapshot) {
  const grid = section("calendar-month-days");
  const focused = grid.contains(document.activeElement) ? document.activeElement.dataset.day : null;
  const first = `${month}-01`;
  const end = monthFollowing(month);
  const start = mondayOfDay(first);
  const last = isoPlusDays(mondayOfDay(isoPlusDays(end, -1)), 7);
  const calendar = state.calendar;
  const rangeEnd = isoPlusDays(calendar.lastWeek, 6);
  const cells = [];
  for (let day = start; day < last; day = isoPlusDays(day, 1)) {
    if (day < first || day >= end) {
      const empty = element("span", "calendar-month-empty");
      empty.setAttribute("aria-hidden", "true");
      cells.push(empty);
      continue;
    }
    const inRange = day >= calendar.firstWeek && day <= rangeEnd;
    const free = inRange ? snapshot.free.get(day) : undefined;
    const control = button("calendar-month-day", undefined, () => chooseCalendarDay(day));
    control.dataset.day = day;
    control.disabled = !inRange || state.busy;
    control.setAttribute("aria-pressed", String(day === state.selectedDay));
    if (day === calendar.today) control.setAttribute("aria-current", "date");
    const label = calendarDayFormat.format(new Date(day));
    control.setAttribute("aria-label", Number.isInteger(free) ? `${label}, свободных слотов: ${free}` : label);
    control.append(element("strong", "", Number(day.slice(-2))));
    if (Number.isInteger(free)) {
      const count = element("span", free ? "calendar-month-count free" : "calendar-month-count", free);
      count.setAttribute("aria-hidden", "true");
      control.append(count);
    }
    cells.push(control);
  }
  grid.replaceChildren(...cells);
  if (focused) calendarDayControl(focused)?.focus({ preventScroll: true });
  const status = snapshot.status === "loading" ? "Загружаем свободные слоты…"
    : snapshot.status === "error" ? "Не удалось загрузить доступность месяца." : "Число в ячейке — свободные слоты";
  const caption = section("calendar-month-status");
  if (caption.textContent !== status) caption.textContent = status;
  const retry = section("calendar-month-retry");
  retry.hidden = snapshot.status !== "error";
  retry.disabled = state.busy;
}

function renderCalendarMonthDay() {
  const day = state.calendar.days.find(item => item.date === state.selectedDay);
  const heading = element("h3", "calendar-day-title", calendarDayFormat.format(new Date(day.date)));
  heading.id = "calendar-month-day-title";
  const slots = element("div", "calendar-month-times");
  slots.append(...calendarSlotButtons(day));
  section("calendar-month-slots").replaceChildren(heading, slots);
}

function syncCalendarViewSelection(previousDay) {
  if (calendarView === "week") updateWeekDay();
  if (calendarView === "list") markCalendarListDay();
  if (calendarView === "map") updateSceneSelection();
  if (calendarView === "month") {
    if (previousDay.slice(0, 7) !== state.selectedDay.slice(0, 7)) renderCalendarMonth();
    else {
      for (const day of section("calendar-month-days").querySelectorAll("button"))
        day.setAttribute("aria-pressed", String(day.dataset.day === state.selectedDay));
      if (previousDay !== state.selectedDay) {
        renderCalendarMonthDay();
        reveal(section("calendar-month-slots"), 3);
      }
    }
  }
  for (const item of calendarViewPanel().querySelectorAll(".slot[data-starts-at]"))
    item.setAttribute("aria-pressed", String(item.dataset.startsAt === state.selected));
  renderCalendarPeriod();
}

function scrollSelectionIntoView() {
  requestAnimationFrame(() => {
    const selected = calendarSlotControl(state.selected);
    if (!selected?.offsetWidth) return;
    const viewport = selected.closest(".calendar-scroll, .calendar-month-times");
    if (!viewport) return;
    const rect = selected.getBoundingClientRect();
    const bounds = viewport.getBoundingClientRect();
    if (rect.left < bounds.left || rect.right > bounds.right) viewport.scrollLeft += rect.left - bounds.left - 7;
    if (rect.top < bounds.top || rect.bottom > bounds.bottom) viewport.scrollTop += rect.top - bounds.top - 36;
  });
}

function bindCalendarViews() {
  for (const view of Object.keys(calendarPanels)) {
    section(`calendar-view-${view}`).addEventListener("click", () => {
      if (calendarView === view) return;
      calendarView = view;
      renderCalendarView();
      reveal(calendarViewPanel(), 5);
      scrollSelectionIntoView();
    });
  }
  ui.prev.addEventListener("click", () => changeCalendarPeriod(-1));
  ui.next.addEventListener("click", () => changeCalendarPeriod(1));
  ui.today.addEventListener("click", () => chooseCalendarDay(state.calendar.today));
  section("calendar-month-retry").addEventListener("click", refreshCalendarMonth);
}
