const analytics = { params: null, data: null, request: 0, loading: false };
const reportNumbers = new Intl.NumberFormat("ru-RU");
const reportPercent = new Intl.NumberFormat("ru-RU", { style: "percent", maximumFractionDigits: 1 });
const reportHours = new Intl.NumberFormat("ru-RU", { maximumFractionDigits: 1 });
const reportDayLabel = new Intl.DateTimeFormat("ru-RU", { day: "numeric", month: "short", timeZone: "UTC" });
const reportWeekdays = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

function reportDate(date) {
  return reportDayLabel.format(new Date(date + "T12:00:00Z"));
}
function reportNumber(value) { return reportNumbers.format(value); }
function reportRatio(numerator, denominator) {
  return denominator ? reportPercent.format(numerator / denominator) : "—";
}
function hoursLabel(minutes) { return `${reportHours.format(minutes / 60)} ч`; }

function resetAnalytics() {
  analytics.request++;
  analytics.params = analytics.data = null;
  analytics.loading = false;
  const form = section("analytics-form");
  form.reset();
  form.elements.resourceId.replaceChildren(new Option("Все ресурсы", ""));
  section("analytics-content").replaceChildren();
  analyticsNotice("");
}

function analyticsNotice(text, error = false) {
  const notice = section("analytics-message");
  notice.textContent = text;
  notice.classList.toggle("error", error);
  notice.hidden = !text;
}

function renderAnalyticsControls() {
  const form = section("analytics-form");
  for (const control of form.elements)
    control.disabled = state.busy || analytics.loading || Boolean(control.dataset.reportPeriod && !analytics.data);
}

function analyticsFormParams() {
  const { from, through, resourceId } = section("analytics-form").elements;
  return { from: from.value, through: through.value, resourceId: resourceId.value };
}

function bindAnalytics() {
  const form = section("analytics-form");
  form.addEventListener("submit", event => {
    event.preventDefault();
    loadAnalytics(analyticsFormParams());
  });
  for (const control of form.querySelectorAll("[data-report-period]")) control.addEventListener("click", () => {
    const today = analytics.data.today;
    const value = control.dataset.reportPeriod;
    const weekday = (new Date(today + "T12:00:00Z").getUTCDay() + 6) % 7;
    const from = isoPlusDays(today, value === "week" ? -weekday : 1 - Number(value));
    const through = value === "week" ? isoPlusDays(from, 6) : today;
    const resourceId = form.elements.resourceId.value;
    form.elements.from.value = from;
    form.elements.through.value = through;
    loadAnalytics({ from, through, resourceId });
  });
}

async function loadAnalytics(params = analytics.params) {
  if (!state.organizationId || !isAdmin()) return;
  const request = ++analytics.request;
  const organization = state.organizationId;
  const zone = viewerZone;
  const query = new URLSearchParams({ timeZone: zone });
  for (const [name, value] of Object.entries(params ?? {}))
    if (value) query.set(name, value);
  analytics.loading = true;
  renderAnalyticsControls();
  analyticsNotice("Собираем отчёт…");
  try {
    const data = await api(`/api/workspace/analytics?${query}`, undefined, organization);
    if (request !== analytics.request || currentScreen() !== "analytics") return;
    analytics.data = data;
    analytics.params = { from: data.from, through: data.through, resourceId: data.resourceId ?? "" };
    const form = section("analytics-form");
    for (const field of [form.elements.from, form.elements.through]) {
      field.min = data.earliestDate;
      field.max = data.lastDate;
    }
    form.elements.from.value = data.from;
    form.elements.through.value = data.through;
    const locations = new Map(state.resources.map(resource => [resource.id, resource.location]));
    form.elements.resourceId.replaceChildren(new Option("Все ресурсы", ""), ...data.catalog.map(resource => {
      const suffix = resource.isArchived ? "архив" : locations.get(resource.id);
      return new Option(suffix ? `${resource.name} · ${suffix}` : resource.name, resource.id);
    }));
    form.elements.resourceId.value = data.resourceId ?? "";
    section("analytics-zone").textContent = `Часовой пояс отчёта · ${timeZoneLabel(data.timeZone)}`;
    renderAnalytics(data);
    analyticsNotice("");
  } catch (error) {
    if (request === analytics.request) analyticsNotice(error.message, true);
  } finally {
    if (request === analytics.request) {
      analytics.loading = false;
      renderAnalyticsControls();
    }
  }
}

function analyticsKpi(title, value, detail, icon) {
  const tile = element("article", "panel analytics-kpi");
  const marker = icon === "×" ? element("span", "", icon) : symbol(icon);
  marker.setAttribute("aria-hidden", "true");
  marker.classList.add("analytics-icon");
  tile.append(marker, element("h2", "", title), element("strong", "", value), element("p", "", detail));
  return tile;
}

function renderAnalytics(data) {
  const { totals, daily, peak, resources } = data;
  const { usage } = totals;
  const metrics = element("div", "analytics-kpis");
  metrics.append(
    analyticsKpi("Загрузка", reportRatio(usage.bookedMinutes, usage.availableMinutes),
      `${hoursLabel(usage.bookedMinutes)} из ${hoursLabel(usage.availableMinutes)}`, "schedule"),
    analyticsKpi("Забронировано интервалов", reportNumber(totals.confirmed), "Подтверждённые брони и занятия", "event_available"),
    analyticsKpi("Отказы", reportRatio(totals.rejected, totals.approved + totals.rejected),
      `${reportNumber(totals.rejected)} из ${reportNumber(totals.approved + totals.rejected)} решений`, "×"),
    analyticsKpi("Неявки", reportRatio(totals.noShow, totals.present + totals.noShow),
      `${reportNumber(totals.unmarked)} без отметки`, "group"),
    analyticsKpi("Конверсия очереди", reportRatio(totals.converted, totals.waitlisted),
      `${reportNumber(totals.converted)} броней из ${reportNumber(totals.waitlisted)} заявок`, "arrow_outward")
  );
  const charts = element("div", "analytics-secondary");
  charts.append(analyticsQueue(totals), analyticsOutcomes(totals));
  const method = element("details", "panel analytics-method");
  method.append(element("summary", "", "Как считаются показатели"),
    element("p", "", "Загрузка — время подтверждённых броней в пределах действующего графика. Из доступного времени вычтены перерывы и обслуживание. Буферы и события внешних календарей не входят в забронированные часы."),
    element("p", "", "Для отказов учитываются решения по заявкам. Доля неявок считается по отмеченным участникам завершённых занятий. Очередь отнесена к дате запрошенного слота, а конверсия учитывает подтверждённые резервы."));
  section("analytics-content").replaceChildren(metrics, analyticsDaily(daily), analyticsPeak(peak), charts, analyticsResources(resources), method);
}

function analyticsDaily(days) {
  const panel = element("section", "panel analytics-panel");
  panel.append(element("h2", "", "Загрузка по дням"));
  const legend = element("div", "analytics-legend");
  legend.append(element("span", "analytics-key booked", "Забронировано"), element("span", "analytics-key open", "Свободно"));
  panel.append(legend);
  const scroller = element("div", "analytics-scroll daily-scroll");
  const plot = element("div", "analytics-daily");
  plot.style.setProperty("--days", days.length);
  plot.style.setProperty("--min-chart-width", `${days.length * 27}px`);
  const max = Math.max(1, ...days.map(day => day.usage.availableMinutes));
  const readout = element("p", "analytics-day-readout");
  readout.setAttribute("role", "status");
  const points = [];
  function select(day, point) {
    readout.textContent = `${reportDate(day.date)} · ${hoursLabel(day.usage.bookedMinutes)} забронировано из ${hoursLabel(day.usage.availableMinutes)} · ${hoursLabel(day.usage.maintenanceMinutes)} обслуживания`;
    for (const marker of points) marker.setAttribute("aria-pressed", String(marker === point));
  }
  for (const day of days) {
    const point = button("analytics-point", undefined, () => select(day, point));
    const area = element("span", "analytics-bar-area");
    const bar = element("span", "analytics-bar");
    const fill = element("span", "analytics-bar-fill");
    bar.style.height = `${day.usage.availableMinutes / max * 100}%`;
    fill.style.height = `${day.usage.availableMinutes ? day.usage.bookedMinutes / day.usage.availableMinutes * 100 : 0}%`;
    bar.append(fill);
    area.append(bar);
    const label = element("span", "analytics-date-tick", day.date.slice(-2));
    point.append(area, label);
    point.setAttribute("aria-label", `${reportDate(day.date)} · ${hoursLabel(day.usage.bookedMinutes)} из ${hoursLabel(day.usage.availableMinutes)}`);
    points.push(point);
    plot.append(point);
  }
  const index = days.reduce((best, day, i) => day.usage.bookedMinutes > days[best].usage.bookedMinutes ? i : best, 0);
  select(days[index], points[index]);
  scroller.append(plot);
  panel.append(scroller, readout);
  return panel;
}

function reportTime(hour) { return `${String(hour).padStart(2, "0")}:00`; }

function analyticsPeak(values) {
  const panel = element("section", "panel analytics-panel");
  const top = Math.max(...values);
  const index = values.indexOf(top);
  panel.append(element("h2", "", "Часы пик"));
  const caption = top ? `Пик · ${reportWeekdays[Math.floor(index / 24)]}, ${reportTime(index % 24)} · ${reportNumber(top)} стартов`
    : "За этот период подтверждённых стартов нет.";
  panel.append(element("p", "analytics-caption", caption));
  const wrapper = element("div", "analytics-scroll");
  const table = element("table", "analytics-heatmap");
  table.setAttribute("aria-label", "Подтверждённые старты по дням недели и часам");
  const header = table.createTHead().insertRow();
  header.append(element("th", "", "День"));
  for (let hour = 0; hour < 24; hour++) {
    const cell = element("th", "", reportTime(hour).slice(0, 2));
    cell.scope = "col";
    header.append(cell);
  }
  const body = table.createTBody();
  for (const [day, name] of reportWeekdays.entries()) {
    const row = body.insertRow();
    const label = element("th", "", name);
    label.scope = "row";
    row.append(label);
    for (let hour = 0; hour < 24; hour++) {
      const value = values[day * 24 + hour];
      const cell = row.insertCell();
      const level = value ? Math.ceil(value / top * 4) : 0;
      cell.dataset.level = level;
      cell.textContent = value ? reportNumber(value) : "·";
      cell.title = `${name}, ${reportTime(hour)} · ${reportNumber(value)}`;
    }
  }
  wrapper.append(table);
  panel.append(wrapper);
  return panel;
}

function analyticsQueue(totals) {
  const panel = element("section", "panel analytics-panel analytics-queue");
  panel.append(element("h2", "", "Воронка листа ожидания"));
  for (const [label, count] of [["Запросили слот", totals.waitlisted], ["Получили резерв", totals.offered], ["Получили бронь", totals.converted]]) {
    const stage = element("div", "analytics-stage");
    stage.append(element("span", "", label), element("strong", "", reportNumber(count)));
    const progress = element("progress", "");
    progress.max = totals.waitlisted || 1;
    progress.value = count;
    progress.setAttribute("aria-label", label);
    stage.append(progress);
    panel.append(stage);
  }
  return panel;
}

function analyticsOutcomes(totals) {
  const panel = element("section", "panel analytics-panel");
  panel.append(element("h2", "", "Решения и посещаемость"));
  const results = element("dl", "analytics-outcomes");
  for (const [label, value] of [
    ["Одобрено", totals.approved], ["Отклонено", totals.rejected], ["Брони отменены", totals.cancelled],
    ["Пришли", totals.present], ["Не явились", totals.noShow], ["Без отметки", totals.unmarked]
  ]) {
    const pair = element("div", "");
    pair.append(element("dt", "", label), element("dd", "", reportNumber(value)));
    results.append(pair);
  }
  panel.append(results);
  return panel;
}

function analyticsResources(resources) {
  const panel = element("section", "panel analytics-panel");
  panel.append(element("h2", "", "По ресурсам"));
  if (!resources.length) {
    panel.append(element("p", "analytics-caption", "В организации пока нет ресурсов."));
    return panel;
  }
  const wrapper = element("div", "analytics-scroll");
  const table = element("table", "analytics-table");
  const header = table.createTHead().insertRow();
  for (const label of ["Ресурс", "Загрузка", "Интервалы", "Отказы", "Неявки", "Из очереди"]) {
    const column = element("th", "", label);
    column.scope = "col";
    header.append(column);
  }
  const body = table.createTBody();
  const locations = new Map(state.resources.map(resource => [resource.id, resource.location]));
  for (const { id, name, isArchived, metrics } of resources) {
    const { usage, confirmed, rejected, noShow, converted } = metrics;
    const row = body.insertRow();
    const identity = element("th", "", name);
    identity.scope = "row";
    const subtitle = isArchived ? "Архив" : locations.get(id);
    if (subtitle) identity.append(element("span", "analytics-resource-location", subtitle));
    row.append(identity);
    for (const text of [reportRatio(usage.bookedMinutes, usage.availableMinutes), reportNumber(confirmed),
      reportNumber(rejected), reportNumber(noShow), reportNumber(converted)])
      row.insertCell().textContent = text;
  }
  wrapper.append(table);
  panel.append(wrapper);
  return panel;
}
