const workspace = { week: null, agendaFilter: "upcoming", agenda: null, catalog: [], editing: null };
let workspaceRequest = 0;

const routes = new Set(["overview", "calendar", "mine", "manage", "team"]);
const activityLabels = {
  "resource.created": "Добавлен ресурс",
  "resource.updated": "Обновлён ресурс",
  "resource.archived": "Ресурс перемещён в архив",
  "resource.restored": "Ресурс восстановлен",
  "maintenance.created": "Запланировано обслуживание",
  "maintenance.removed": "Снята блокировка"
};
const outlookLabels = {
  free: "Свободно", booked: "Бронь", held: "Резерв", maintenance: "Обслуживание"
};
const weekFormat = new Intl.DateTimeFormat("ru-RU", { day: "numeric", month: "long", timeZone: "UTC" });
const dayFormat = new Intl.DateTimeFormat("ru-RU", { weekday: "short", timeZone: "UTC" });
const dateWithYear = new Intl.DateTimeFormat("ru-RU", {
  day: "numeric", month: "short", year: "numeric", timeZone: "Europe/Moscow"
});

function section(id) {
  return document.getElementById(id);
}

function currentScreen() {
  if (state.session?.isAccount && !state.organizationId) return "welcome";
  const route = location.hash.slice(1);
  if (route === "team" && (!isAdmin() || !state.session?.isAccount)) return "overview";
  return routes.has(route) && (route !== "manage" || isAdmin()) ? route : "overview";
}

function renderWorkspaceScreen() {
  const route = currentScreen();
  for (const pane of document.querySelectorAll("[data-screen]")) pane.hidden = pane.dataset.screen !== route;
  for (const link of document.querySelectorAll("[data-nav]")) {
    link.hidden = route === "welcome" || (["manage", "team"].includes(link.dataset.nav) && !isAdmin())
      || (link.dataset.nav === "team" && !state.session?.isAccount);
    if (link.dataset.nav === route) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
  const service = section("service-form");
  const serviceUnavailable = state.busy || service.elements.resourceId.options.length === 0;
  for (const control of service.elements) control.disabled = serviceUnavailable;
  section("resource-form").querySelector('button[type="submit"]').disabled = state.busy;
  section("add-resource").disabled = state.busy;
}

function resetWorkspace() {
  workspaceRequest++;
  workspace.week = null;
  workspace.agenda = null;
  workspace.catalog = [];
  section("overview-content").replaceChildren();
  section("agenda-content").replaceChildren();
  section("catalog-content").replaceChildren();
  section("activity-content").replaceChildren();
  section("team-members").replaceChildren();
  section("team-invitations").replaceChildren();
  section("team-message").hidden = true;
  section("overview-period").textContent = "Загрузка расписания…";
  section("service-message").hidden = true;
  section("service-form").elements.date.value = "";
  section("resource-search").value = "";
  const dialog = section("resource-dialog");
  if (dialog.open) dialog.close();
  renderWorkspaceScreen();
}

async function loadWorkspace() {
  const route = currentScreen();
  const requestId = ++workspaceRequest;
  if (!state.organizationId || route === "calendar") return;
  try {
    if (route === "overview") {
      const suffix = workspace.week ? `?week=${encodeURIComponent(workspace.week)}` : "";
      const [overview, agenda] = await Promise.all([api("/api/workspace/overview" + suffix), api("/api/workspace/me")]);
      if (requestId === workspaceRequest) renderOverview(overview, agenda);
    } else if (route === "mine") {
      const agenda = await api("/api/workspace/me");
      if (requestId === workspaceRequest && JSON.stringify(agenda) !== JSON.stringify(workspace.agenda)) {
        workspace.agenda = agenda;
        renderAgenda();
      }
    } else if (route === "team") {
      const team = await api("/api/team");
      if (requestId === workspaceRequest) renderTeam(team);
    } else {
      const [catalog, activity] = await Promise.all([api("/api/workspace/resources"), api("/api/workspace/activity")]);
      if (requestId === workspaceRequest) {
        workspace.catalog = catalog;
        renderCatalog();
        renderActivity(activity);
      }
    }
  } catch (error) {
    if (requestId === workspaceRequest) showError(error, loadWorkspace);
  }
}

function statsTile(label, value, className, iconName) {
  const item = element("div", `stat-tile ${className}`);
  const icon = symbol(iconName);
  icon.classList.add("stat-icon");
  item.append(icon, element("strong", "stat-value", value), element("span", "stat-label", label));
  return item;
}

function renderOverview(data, agenda) {
  workspace.week = data.week;
  section("overview-period").textContent = weekFormat.formatRange(new Date(data.week), new Date(isoPlusDays(data.week, 6)))
    + " · Москва";
  const metrics = element("div", "stat-row");
  metrics.append(
    statsTile("Свободные слоты", data.totals.free, "stat-free", "calendar_month"),
    statsTile("Подтверждённые брони", data.totals.booked, "stat-booked", "check_circle"),
    statsTile("В листе ожидания", data.waiting, "stat-held", "schedule"),
    statsTile("На обслуживании", data.totals.maintenance, "stat-maintenance", "build")
  );
  const columns = element("div", "overview-grid");
  columns.append(weeklyChart(data), nextBookings(agenda));
  section("overview-content").replaceChildren(metrics, columns, resourceBreakdown(data.resources));
}

function weeklyChart(data) {
  const panel = element("section", "panel weekly-panel");
  const header = element("div", "section-heading");
  const navigation = element("div", "week-controls");
  for (const [days, label, sign, boundary] of [
    [-7, "Предыдущая неделя обзора", "‹", data.firstWeek],
    [7, "Следующая неделя обзора", "›", data.lastWeek]
  ]) {
    const control = button("icon-button", sign, () => {
      workspace.week = isoPlusDays(data.week, days);
      loadWorkspace();
    });
    control.disabled = data.week === boundary;
    control.setAttribute("aria-label", label);
    navigation.append(control);
  }
  header.append(element("h2", "", "Загрузка по дням"), navigation);

  const chart = element("div", "week-chart");
  const totals = data.days.map(day => Object.values(day.counts).reduce((sum, count) => sum + count, 0));
  const step = Math.max(1, Math.ceil(Math.max(...totals) / 4));
  const axis = element("div", "chart-axis");
  axis.setAttribute("aria-hidden", "true");
  axis.append(...[4, 3, 2, 1, 0].map(value => element("span", "", value * step)));
  chart.append(axis);
  for (const day of data.days) {
    const total = Object.values(day.counts).reduce((sum, count) => sum + count, 0);
    const column = element("div", "chart-day");
    const bars = element("div", "chart-bars");
    const stack = element("div", "chart-stack");
    stack.style.height = `${total / (step * 4) * 100}%`;
    const breakdown = [];
    for (const kind of ["booked", "held", "maintenance", "free"]) {
      const count = day.counts[kind];
      if (!count) continue;
      const segment = element("div", `chart-segment ${kind}`);
      segment.style.flexGrow = count;
      segment.title = `${outlookLabels[kind]} — ${count}`;
      breakdown.push(segment.title);
      stack.append(segment);
    }
    bars.append(stack);
    column.setAttribute("role", "img");
    column.setAttribute("aria-label", `${weekFormat.format(new Date(day.date))}. ${breakdown.join(", ") || "Будущих слотов нет"}.`);
    column.append(bars, element("span", "chart-day-label", dayFormat.format(new Date(day.date))),
      element("strong", "", day.date.slice(-2).replace(/^0/, "")));
    chart.append(column);
  }
  const legend = element("ul", "legend chart-legend");
  for (const kind of Object.keys(outlookLabels)) {
    const line = element("li", "");
    line.append(element("span", `chart-dot ${kind}`), element("span", "", outlookLabels[kind]));
    legend.append(line);
  }
  panel.append(header, chart, legend);
  return panel;
}

function nextBookings(agenda) {
  const panel = element("section", "panel next-panel");
  const header = element("div", "section-heading");
  const link = element("a", "text-link", "Все записи");
  link.href = "#mine";
  header.append(element("h2", "", "Ближайшие записи"), link);
  panel.append(header);
  if (agenda.upcoming.length === 0) {
    const empty = element("div", "next-empty");
    const icon = symbol("event_available");
    icon.classList.add("empty-symbol");
    empty.append(icon, element("strong", "", "Свободное расписание"),
      element("p", "", "Здесь появятся ваши брони и резервы."));
    const action = element("a", "text-link", "Выбрать время ↗");
    action.href = "#calendar";
    empty.append(action);
    panel.append(empty);
  } else {
    for (const item of agenda.upcoming.slice(0, 4)) {
      const action = button("next-booking", undefined, () => goToCalendar(item));
      const time = element("div", "data-title");
      time.append(element("strong", "", item.resourceName), element("span", "", appointmentTime(item.startsAt)));
      const icon = symbol(item.holdExpiresAt ? "schedule" : "event_available");
      icon.classList.add("empty-symbol");
      action.append(icon, time, symbol("arrow_outward"));
      panel.append(action);
    }
  }
  return panel;
}

function resourceBreakdown(resources) {
  const panel = element("section", "panel listing overview-resources");
  panel.append(element("h2", "", "Доступность ресурсов"));
  if (!resources.length) panel.append(element("p", "list-empty", "В организации пока нет доступных ресурсов."));
  for (const resource of resources) {
    const row = element("div", "data-row overview-resource");
    const resourceLabel = element("div", "data-identity");
    const icon = element("span", `resource-icon ${resource.id}`, resourceIcons[resource.id] ?? "▦");
    icon.setAttribute("aria-hidden", "true");
    const identity = element("div", "data-title");
    identity.append(element("strong", "", resource.name), element("span", "", resource.location));
    resourceLabel.append(icon, identity);
    const values = element("div", "resource-counts");
    values.append(element("span", "", `${resource.counts.free} свободно`),
      element("span", "", `${resource.counts.booked + resource.counts.held + resource.counts.maintenance} занято`));
    const action = button("outline-action", "К расписанию", () => goToCalendar({ resourceId: resource.id, week: workspace.week }));
    row.append(resourceLabel, values, action);
    panel.append(row);
  }
  return panel;
}

function appointmentTime(value) {
  const date = new Date(value);
  return `${dateWithYear.format(date)} · ${timeFormat.format(date)}`;
}

function goToCalendar(item) {
  const week = item.week ?? (item.startsAt
    ? isoPlusDays(item.startsAt.slice(0, 10), -((new Date(item.startsAt).getUTCDay() + 6) % 7))
    : undefined);
  return run(async () => {
    location.hash = "calendar";
    await loadCalendar(item.resourceId, week);
    const slot = allSlots().find(candidate => candidate.startsAt === item.startsAt);
    if (slot) state.selected = slot.startsAt;
    scrollSelectionIntoView();
  }, () => goToCalendar(item));
}

function renderAgenda() {
  for (const control of document.querySelectorAll("[data-agenda]"))
    control.setAttribute("aria-pressed", control.dataset.agenda === workspace.agendaFilter);
  const items = workspace.agenda?.[workspace.agendaFilter] ?? [];
  const content = section("agenda-content");
  if (!items.length) {
    const message = { upcoming: "Предстоящих записей пока нет.", waiting: "Вы пока не записаны в очередь.",
      history: "Завершённые и отменённые записи появятся здесь." }[workspace.agendaFilter];
    content.replaceChildren(element("p", "list-empty", message));
    return;
  }
  content.replaceChildren(...items.map(item => {
    const row = element("div", "data-row agenda-row");
    const description = element("div", "data-title");
    description.append(element("strong", "", item.resourceName), element("span", "", item.location));
    const time = element("div", "data-title");
    time.append(element("strong", "", appointmentTime(item.startsAt)));
    const actions = element("div", "row-actions");
    if (workspace.agendaFilter === "history") {
      const expired = item.holdExpiresAt && (!item.cancelledAt || Date.parse(item.holdExpiresAt) <= Date.parse(item.cancelledAt));
      time.append(element("span", "", expired ? "Резерв истёк" : item.cancelledAt ? "Отменено" : "Завершено"));
    } else if (workspace.agendaFilter === "waiting") {
      time.append(element("span", "", `Место в очереди — ${item.position}`));
      actions.append(button("outline-action", "Выйти из очереди", () =>
        updateBooking(`/api/waitlist/${item.id}`, { method: "DELETE" }, "Вы вышли из очереди.")));
    } else {
      time.append(element("span", "", item.holdExpiresAt
        ? `Резерв до ${timeFormat.format(new Date(item.holdExpiresAt))}` : "Подтверждено"));
      if (item.holdExpiresAt) {
        actions.append(button("page-action small", "Подтвердить", () =>
          updateBooking(`/api/bookings/${item.id}/confirm`, { method: "POST" }, "Бронирование подтверждено.")));
      }
      actions.append(button("outline-action", item.holdExpiresAt ? "Отказаться" : "Отменить бронь", () =>
        updateBooking(`/api/bookings/${item.id}`, { method: "DELETE" }, "Запись отменена.")));
    }
    row.append(description, time, actions);
    return row;
  }));
}

function renderCatalog() {
  const search = section("resource-search").value.trim().toLocaleLowerCase("ru");
  const visible = workspace.catalog.filter(item => `${item.name} ${item.location}`.toLocaleLowerCase("ru").includes(search));
  section("catalog-content").replaceChildren(...(visible.length ? visible.map(item => {
    const row = element("div", "data-row catalog-row");
    const title = element("div", "data-title");
    title.append(element("strong", "", item.name), element("span", "", item.location));
    row.append(title, element("span", `pill ${item.isArchived ? "past" : "available"}`, item.isArchived ? "В архиве" : "Активен"),
      button("outline-action", "Изменить", () => editResource(item)));
    return row;
  }) : [element("p", "list-empty", search ? "По запросу ничего не найдено." : "Добавьте первый ресурс.")]));

  const form = section("service-form");
  const field = form.elements;
  const selected = field.resourceId.value;
  const active = workspace.catalog.filter(item => !item.isArchived);
  field.resourceId.replaceChildren(...active.map(item => new Option(item.name, item.id)));
  if (active.some(item => item.id === selected)) field.resourceId.value = selected;
  for (const input of form.elements) input.disabled = state.busy || !active.length;
  if (!field.date.value) {
    const today = state.calendar?.today ?? new Date().toISOString().slice(0, 10);
    field.date.min = today;
    field.date.value = isoPlusDays(today, 1);
  }
}

function renderActivity(entries) {
  section("activity-content").replaceChildren(...(entries.length ? entries.map(entry => {
    const row = element("div", "data-row activity-row");
    const action = element("div", "data-title");
    action.append(element("strong", "", activityLabels[entry.action] ?? entry.action),
      element("span", "", `${entry.resourceName} · ${entry.details}`));
    const author = element("div", "data-title");
    author.append(element("strong", "", entry.actor), element("span", "", appointmentTime(entry.occurredAt)));
    row.append(action, author);
    return row;
  }) : [element("p", "list-empty", "Изменений пока нет.")]));
}

function editResource(item) {
  workspace.editing = item;
  section("resource-dialog-title").textContent = item ? "Изменить ресурс" : "Новый ресурс";
  section("archive-field").hidden = !item;
  section("resource-error").hidden = true;
  const fields = section("resource-form").elements;
  fields.name.value = item?.name ?? "";
  fields.location.value = item?.location ?? "";
  fields.isArchived.checked = item?.isArchived ?? false;
  section("resource-dialog").showModal();
}

function formProblem(node, error) {
  const conflicts = error.conflicts?.map(value => timeFormat.format(new Date(value))).join(", ");
  node.textContent = conflicts ? `${error.message} Занято — ${conflicts}.` : error.message;
  node.classList.add("error");
  node.hidden = false;
}

function jsonRequest(method, payload) {
  return { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) };
}

function bindWorkspace() {
  document.querySelector(".skip-link").addEventListener("click", event => {
    event.preventDefault();
    section("main-content").focus();
  });
  window.addEventListener("hashchange", () => {
    renderWorkspaceScreen();
    loadWorkspace();
  });
  for (const control of document.querySelectorAll("[data-agenda]")) control.addEventListener("click", () => {
    workspace.agendaFilter = control.dataset.agenda;
    renderAgenda();
  });
  setInterval(() => {
    if (!state.busy && currentScreen() === "mine" && (workspace.agenda?.waiting.length
      || workspace.agenda?.upcoming.some(item => item.holdExpiresAt))) loadWorkspace();
  }, 4000);
  section("resource-search").addEventListener("input", renderCatalog);
  section("add-resource").addEventListener("click", () => editResource(null));
  section("resource-cancel").addEventListener("click", () => section("resource-dialog").close());
  section("resource-form").addEventListener("submit", event => {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const payload = { name: data.get("name"), location: data.get("location"), isArchived: data.has("isArchived") };
    const message = section("resource-error");
    message.hidden = true;
    run(async () => {
      try {
        const suffix = workspace.editing ? `/${encodeURIComponent(workspace.editing.id)}` : "";
        await api("/api/workspace/resources" + suffix, jsonRequest(workspace.editing ? "PUT" : "POST", payload));
      } catch (error) {
        formProblem(message, error);
        return;
      }
      section("resource-dialog").close();
      await enterOrganization(state.organizationId);
    });
  });
  const serviceForm = section("service-form");
  for (const name of ["fromHour", "throughHour"])
    serviceForm.elements[name].replaceChildren(...[9, 11, 13, 15, 17].map(hour => new Option(`${String(hour).padStart(2, "0")}:00`, hour)));
  serviceForm.addEventListener("submit", event => {
    event.preventDefault();
    const data = Object.fromEntries(new FormData(serviceForm));
    const message = section("service-message");
    message.hidden = true;
    run(async () => {
      try {
        const result = await api("/api/maintenance/window", jsonRequest("POST", {
          ...data, fromHour: Number(data.fromHour), throughHour: Number(data.throughHour)
        }));
        message.classList.remove("error");
        message.textContent = `Закрыто слотов — ${result.count}.`;
        message.hidden = false;
        serviceForm.elements.reason.value = "";
        if (state.calendar) await refreshCalendar();
        await loadWorkspace();
      } catch (error) {
        formProblem(message, error);
      }
    });
  });
}
