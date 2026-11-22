const workspace = { week: null, agendaFilter: "upcoming", agenda: null, catalog: [], templates: [], editing: null, sharing: null };
let workspaceRequest = 0;

const routes = new Set(["overview", "calendar", "availability", "wizard", "sessions", "mine", "analytics", "manage", "integrations", "approvals", "team", "structure", "branding", "calendar-import", "p3"]);
const adminRoutes = new Set(["analytics", "manage", "integrations", "team", "structure", "branding", "calendar-import"]);
const activityLabels = {
  "resource.created": "Добавлен ресурс",
  "resource.updated": "Обновлён ресурс",
  "resource.template-updated": "Применены настройки шаблона",
  "resource.template-detached": "Ресурс отвязан от шаблона",
  "resource.archived": "Ресурс перемещён в архив",
  "resource.restored": "Ресурс восстановлен",
  "resource.public-link": "Настроен доступ по ссылке",
  "resource.embed": "Настроен виджет бронирования",
  "resource.schedule": "Обновлён график",
  "resource.booking-policy": "Изменены правила бронирования",
  "resource.booking-form": "Обновлены вопросы к брони",
  "resource.private-link": "Изменены приватные ссылки",
  "structure.unit": "Обновлено подразделение",
  "structure.group": "Обновлена группа ресурсов",
  "structure.member": "Обновлены права участника",
  "structure.resource": "Изменена группа ресурса",
  "structure.invited": "Отправлено приглашение",
  "structure.joined": "Участник принял приглашение",
  "organization.branding": "Обновлён брендинг организации",
  "payment.price": "Изменена цена бронирования",
  "payment.paid": "Принята демо-оплата",
  "payment.refunded": "Оформлен демо-возврат",
  "calendar.imported": "Импортирован календарь",
  "event.published": "Изменена публикация мероприятия",
  "event.announcement": "Сообщение гостям поставлено в очередь",
  "discover.listing": "Изменена карточка площадки",
  "maintenance.created": "Запланировано обслуживание",
  "maintenance.removed": "Снята блокировка",
  "booking.approved": "Заявка одобрена",
  "booking.rejected": "Заявка отклонена",
  "booking.rescheduled": "Бронь перенесена",
  "group-session.created": "Открыто групповое занятие",
  "group-session.cancelled": "Групповое занятие отменено",
  "group-seat.confirmed": "Заявки на места одобрены",
  "group-seat.rejected": "Заявки на места отклонены",
  "group-seat.present": "Отмечен приход",
  "group-seat.no-show": "Отмечена неявка",
  "group-seat.clear-attendance": "Исправлена отметка посещаемости"
};
const outlookLabels = {
  free: "Свободно", booked: "Бронь", held: "Резерв", pending: "На согласовании", maintenance: "Обслуживание"
};
const weekFormat = new Intl.DateTimeFormat("ru-RU", { day: "numeric", month: "long", timeZone: "UTC" });
const dayFormat = new Intl.DateTimeFormat("ru-RU", { weekday: "short", timeZone: "UTC" });

function section(id) {
  return document.getElementById(id);
}

function currentScreen() {
  if (state.session?.isAccount && !state.organizationId) return "welcome";
  const route = location.hash.slice(1);
  if (adminRoutes.has(route) && !isAdmin() || route === "team" && !state.session?.isAccount
    || route === "approvals" && !canReviewBookings()) return "overview";
  return routes.has(route) ? route : "overview";
}

function renderWorkspaceScreen() {
  const route = currentScreen();
  const links = document.querySelectorAll("[data-nav]");
  const nextLink = Array.from(links).find(link => link.dataset.nav === route);
  const enteringLink = nextLink && nextLink.getAttribute("aria-current") !== "page";
  for (const pane of document.querySelectorAll("[data-screen]")) {
    const entering = pane.hidden && pane.dataset.screen === route;
    pane.hidden = pane.dataset.screen !== route;
    if (entering) reveal(pane);
  }
  for (const link of links) {
    link.hidden = route === "welcome" || (adminRoutes.has(link.dataset.nav) && !isAdmin())
      || (link.dataset.nav === "team" && !state.session?.isAccount)
      || (link.dataset.nav === "approvals" && !canReviewBookings());
    if (link.dataset.nav === route) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
  if (enteringLink && !nextLink.hidden) {
    const nav = nextLink.parentElement;
    if (nav.scrollWidth > nav.clientWidth) {
      const viewport = nav.getBoundingClientRect();
      const item = nextLink.getBoundingClientRect();
      if (item.left < viewport.left) nav.scrollLeft += item.left - viewport.left - 3;
      else if (item.right > viewport.right) nav.scrollLeft += item.right - viewport.right + 3;
    }
  }
  const service = section("service-form");
  const serviceUnavailable = state.busy || service.elements.resourceId.options.length === 0;
  for (const control of service.elements) control.disabled = serviceUnavailable;
  section("resource-form").querySelector('button[type="submit"]').disabled = state.busy;
  section("public-link-form").querySelector('button[type="submit"]').disabled = state.busy;
  section("add-resource").disabled = state.busy;
  for (const control of section("catalog-content").querySelectorAll(".catalog-row button"))
    control.disabled = state.busy;
  renderAvailabilityControls();
  renderApprovalControls();
  document.querySelector("[data-admin-approval-link]").hidden = !isAdmin();
  renderGroupControls();
  renderAnalyticsControls();
  renderTemplateControls();
  renderRoutingControls();
  renderIntegrationControls();
}

function resetWorkspace() {
  section("agenda-transfer").hidden = true;
  workspaceRequest++;
  workspace.week = null;
  workspace.agenda = null;
  workspace.catalog = [];
  workspace.templates = [];
  section("template-cards").replaceChildren();
  section("overview-content").replaceChildren();
  section("agenda-content").replaceChildren();
  section("catalog-content").replaceChildren();
  section("activity-content").replaceChildren();
  section("team-members").replaceChildren();
  section("team-invitations").replaceChildren();
  section("team-message").hidden = true;
  section("team-invite-results").replaceChildren();
  section("overview-period").textContent = "Загрузка расписания…";
  section("service-message").hidden = true;
  section("service-form").elements.date.value = "";
  section("resource-search").value = "";
  for (const id of ["resource-dialog", "public-link-dialog", "pricing-dialog"]) {
    const dialog = section(id);
    if (dialog.open) dialog.close();
  }
  closeSchedule();
  document.querySelector(".resource-template-editor")?.close();
  document.querySelector(".embed-settings-dialog")?.close();
  resetAvailability();
  resetApprovals();
  resetGroupSessions();
  resetAnalytics();
  resetRouting();
  resetIntegrations();
  resetCalendarImport();
  resetP3();
  resetOrganizationStructure();
  resetOrganizationBranding();
  renderWorkspaceScreen();
}

async function loadWorkspace() {
  const route = currentScreen();
  const requestId = ++workspaceRequest;
  if (!state.organizationId || route === "calendar" || route === "availability") return;
  try {
    if (route === "overview") {
      const query = new URLSearchParams({ timeZone: viewerZone });
      if (workspace.week) query.set("week", workspace.week);
      const [overview, agenda] = await Promise.all([api(`/api/workspace/overview?${query}`), api("/api/workspace/me")]);
      if (requestId === workspaceRequest) renderOverview(overview, agenda);
    } else if (route === "mine") {
      const agenda = await api("/api/workspace/me");
      if (requestId === workspaceRequest && JSON.stringify(agenda) !== JSON.stringify(workspace.agenda)) {
        workspace.agenda = agenda;
        renderAgenda();
      }
    } else if (route === "wizard") {
      await refreshWizardZone();
    } else if (route === "integrations") {
      await loadIntegrations();
    } else if (route === "calendar-import") {
      const imports = await api(calendarImportAddress);
      if (requestId === workspaceRequest) renderCalendarImport(imports);
    } else if (route === "p3") {
      const events = await api("/api/events");
      if (requestId === workspaceRequest) renderP3Events(events);
    } else if (route === "analytics") {
      await loadAnalytics();
    } else if (route === "sessions") {
      await loadGroupSessions();
    } else if (route === "team") {
      const team = await api("/api/team");
      if (requestId === workspaceRequest) renderTeam(team);
    } else if (route === "structure") {
      const structure = await api("/api/structure", { cache: "no-store" });
      if (requestId === workspaceRequest) renderOrganizationStructure(structure);
    } else if (route === "branding") {
      const branding = await api(orgBrandingAddress, { cache: "no-store" });
      if (requestId === workspaceRequest) renderOrganizationBranding(branding);
    } else if (route === "approvals") {
      const requests = await api("/api/approvals");
      if (requestId === workspaceRequest) renderApprovals(requests);
    } else {
      const [catalog, activity, templates, routing] = await Promise.all([
        api("/api/workspace/resources"), api("/api/workspace/activity"), api(templateAddress), api(routingSettingsAddress)
      ]);
      if (requestId === workspaceRequest) {
        workspace.catalog = catalog;
        workspace.templates = templates;
        renderResourceTemplates();
        renderRoutingSettings(routing);
        renderCatalog();
        renderActivity(activity);
      }
    }
  } catch (error) {
    if (requestId !== workspaceRequest) return;
    if (route === "approvals" && error.status === 403 && !isAdmin()) {
      await refreshReviewerAccess();
      if (!canReviewBookings()) return loadWorkspace();
    }
    showError(error, loadWorkspace);
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
  const bookings = statsTile("Подтверждённые брони", data.totals.booked, "stat-booked", "check_circle");
  if (data.totals.pending) bookings.append(element("span", "stat-label", `${data.totals.pending} на согласовании`));
  metrics.append(
    statsTile("Свободные слоты", data.totals.free, "stat-free", "calendar_month"),
    bookings,
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
    for (const kind of ["booked", "held", "pending", "maintenance", "free"]) {
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
    for (const item of agendaGroups(agenda.upcoming).slice(0, 4)) {
      const action = button("next-booking", undefined, () => {
        if (item.bundleId) location.hash = "mine";
        else goToCalendar(item);
      });
      const time = element("div", "data-title");
      time.append(element("strong", "", item.resourceName), element("span", "", appointmentTime(item.startsAt, viewerZone, true, item.endsAt)));
      if (item.approvalStatus === "pending") time.append(element("span", "", "На согласовании"));
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
    const occupied = resource.counts.booked + resource.counts.held + resource.counts.maintenance + (resource.counts.pending ?? 0);
    values.append(element("span", "", `${resource.counts.free} свободно`), element("span", "", `${occupied} занято`));
    const action = button("outline-action", "К расписанию", () => goToCalendar({ resourceId: resource.id, week: workspace.week }));
    row.append(resourceLabel, values, action);
    panel.append(row);
  }
  return panel;
}

function appointmentTime(value, zone = viewerZone, showOffset = true, endsAt) {
  const date = new Date(value);
  const formats = zoneFormats(zone);
  const time = endsAt ? slotTimeRange(value, zone, endsAt) : showOffset ? timeWithOffset(date, zone) : formats.time.format(date);
  return `${formats.date.format(date)} · ${time}`;
}

function goToCalendar(item) {
  const week = item.startsAt ? weekOf(item.startsAt) : item.week;
  return run(async () => {
    location.hash = "calendar";
    await loadCalendar(item.resourceId, week, false, state.organizationId, item.durationMinutes ?? durationOf(item.startsAt, item.endsAt));
    const slot = allSlots().find(candidate => candidate.startsAt === item.startsAt);
    if (slot) state.selected = slot.startsAt;
    scrollSelectionIntoView();
  }, () => goToCalendar(item));
}

function agendaGroups(items) {
  const groups = new Map();
  for (const item of items) {
    const key = item.bundleId ? `${item.bundleId}|${item.startsAt}|${item.cancelledAt ?? ""}` : item.id;
    const members = groups.get(key) ?? [];
    members.push(item);
    groups.set(key, members);
  }
  return [...groups.values()].map(members => {
    const first = members[0];
    return first.bundleId ? { ...first, bundleMembers: members,
      resourceName: members.map(item => item.resourceName).join(" + ") } : first;
  });
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
  content.replaceChildren(...agendaGroups(items).map(item => {
    const row = element("div", "data-row agenda-row");
    const description = element("div", "data-title");
    description.append(element("strong", "", item.resourceName));
    if (item.bundleMembers) {
      description.append(element("span", "", `Комплект · ${item.bundleMembers.length} ресурса`));
      for (const member of item.bundleMembers)
        description.append(element("span", "", `${member.resourceName} · ${member.location}`));
    } else description.append(element("span", "", item.location));
    if (item.seriesId) description.append(element("span", "", "Еженедельная серия"));
    if (item.routing) {
      description.append(element("span", "", `Цель · ${item.routing.purpose}`));
      if (item.approvalStatus === "pending" && item.reviewer)
        description.append(element("span", "", `Согласующий · ${item.reviewer}`));
    }
    const time = element("div", "data-title");
    const end = item.endsAt ?? (item.durationMinutes ? new Date(Date.parse(item.startsAt) + item.durationMinutes * 60000) : null);
    time.append(element("strong", "", appointmentTime(item.startsAt, viewerZone, true, end)));
    const actions = element("div", "row-actions");
    if (workspace.agendaFilter === "history") {
      const expired = item.holdExpiresAt && (!item.cancelledAt || Date.parse(item.holdExpiresAt) <= Date.parse(item.cancelledAt));
      const label = item.isRescheduled ? "Перенесено" : item.approvalStatus === "rejected" ? "Отклонено"
        : item.approvalStatus === "pending" ? (item.cancelledAt ? "Заявка отозвана" : "Срок заявки истёк")
        : expired ? "Резерв истёк" : item.cancelledAt ? "Отменено" : "Завершено";
      time.append(element("span", item.approvalStatus === "rejected" ? "pill rejected" : "", label));
    } else if (workspace.agendaFilter === "waiting") {
      time.append(element("span", "", `Место в очереди — ${item.position}`));
      actions.append(button("outline-action", "Выйти из очереди", () =>
        updateBooking(`/api/waitlist/${item.id}`, { method: "DELETE" }, "Вы вышли из очереди.")));
    } else {
      const pending = item.approvalStatus === "pending";
      const label = pending ? "На согласовании" : item.approvalStatus === "approved" ? "Одобрено"
        : item.holdExpiresAt ? `Резерв до ${zoneFormats(viewerZone).time.format(new Date(item.holdExpiresAt))}` : "Подтверждено";
      const style = pending ? "pill pending" : item.approvalStatus === "approved" ? "pill approved" : "";
      time.append(element("span", style, label));
      const deadlines = bookingDeadlines(item.changes, item.startsAt, viewerZone);
      if (deadlines) time.append(element("span", "", deadlines));
      if (item.holdExpiresAt) {
        actions.append(button("page-action small", item.requiresApproval ? "Отправить заявку" : "Подтвердить", () =>
          confirmBookingHold(item.id, item.resourceId, null, item.requiresApproval)));
      } else if (item.changes?.canReschedule !== false)
        actions.append(button("outline-action", item.bundleId ? "Перенести комплект" : "Перенести", () => showWorkspaceReschedule(item)));
      const action = pending ? "Отозвать заявку" : item.holdExpiresAt ? "Отказаться"
        : item.bundleId ? "Отменить комплект" : "Отменить бронь";
      if (item.changes?.canCancel !== false) actions.append(button("outline-action", action, () =>
        updateBooking(`/api/bookings/${item.id}`, { method: "DELETE" }, pending ? "Заявка отозвана." : "Запись отменена.")));
    }
    if (item.approvalReason) {
      const note = element("div", "decision-note");
      note.classList.toggle("decision-note-rejected", item.approvalStatus === "rejected");
      note.append(element("strong", "", item.approvalStatus === "rejected" ? "Причина отказа" : "Причина одобрения"),
        element("p", "", item.approvalReason));
      time.append(note);
    }
    row.append(description, time, actions);
    return row;
  }));
}

function showWorkspaceReschedule(booking) {
  if (state.busy) return;
  section("agenda-transfer").hidden = true;
  const address = `/api/bookings/${booking.id}/reschedule`;
  const organization = state.organizationId;
  openBookingReschedule({
    booking, zone: viewerZone,
    load: () => api(address, undefined, organization),
    save: startsAt => api(address, jsonRequest("POST", { startsAt }), organization),
    onMoved: result => run(async () => {
      if (currentScreen() === "calendar") {
        await loadCalendar(result.resourceId, weekOf(result.startsAt), false, organization, durationOf(result.startsAt, result.endsAt));
        state.selected = result.startsAt;
        scrollSelectionIntoView();
      } else await refreshCalendar();
      await loadWorkspace();
      const status = result.approvalStatus === "pending" ? "Новое время отправлено на согласование" : "Бронь перенесена";
      const notice = `${status} · ${appointmentTime(result.startsAt, viewerZone, true, result.endsAt)}.`;
      const banner = section("agenda-transfer");
      banner.textContent = notice;
      banner.hidden = false;
      ui.status.textContent = notice;
    })
  });
}

function renderCatalog() {
  const search = section("resource-search").value.trim().toLocaleLowerCase("ru");
  const visible = workspace.catalog.filter(item => `${item.name} ${item.location}`.toLocaleLowerCase("ru").includes(search));
  section("catalog-content").replaceChildren(...(visible.length ? visible.map(item => {
    const row = element("div", "data-row catalog-row");
    const title = element("div", "data-title");
    title.append(element("strong", "", item.name), element("span", "", item.location));
    if (item.priceMinor != null) title.append(element("span", "", `${bookingPrice(item.priceMinor)} за бронь`));
    const template = templateByKey(item.templateId);
    if (template) title.append(element("span", "template-origin", `Шаблон · ${template.name}`));
    const edit = button("outline-action", "Изменить", () => editResource(item));
    edit.disabled = state.busy;
    const actions = element("div", "catalog-actions");
    actions.append(edit);
    if (!item.isArchived) {
      const schedule = button("outline-action", "График", () => editSchedule(item));
      schedule.setAttribute("aria-label", `График: ${item.name}`);
      schedule.disabled = state.busy;
      const share = button("outline-action", "Ссылка", () => editPublicLink(item));
      share.setAttribute("aria-label", `Публичная ссылка: ${item.name}`);
      share.disabled = state.busy;
      const embed = button("outline-action", "Виджет", () => editEmbedSettings(item));
      embed.setAttribute("aria-label", `Виджет: ${item.name}`);
      embed.disabled = state.busy;
      const questions = button("outline-action", "Вопросы", () => editBookingQuestions(item));
      questions.setAttribute("aria-label", `Вопросы к брони: ${item.name}`);
      questions.disabled = state.busy;
      const invites = button("outline-action", "Приватные ссылки", () => editPrivateLinks(item));
      invites.setAttribute("aria-label", `Приватные ссылки: ${item.name}`);
      invites.disabled = state.busy;
      const integration = button("outline-action", "Google Calendar", () => editGoogleCalendar(item));
      integration.setAttribute("aria-label", `Google Calendar: ${item.name}`);
      integration.disabled = state.busy;
      const copy = button("outline-action", "В шаблон", () => templateFromResource(item));
      copy.setAttribute("aria-label", `Сохранить как шаблон: ${item.name}`);
      copy.disabled = state.busy;
      const price = button("outline-action", "Цена", () => editResourcePrice(item));
      price.setAttribute("aria-label", `Цена: ${item.name}`);
      price.disabled = state.busy;
      const listing = button("outline-action", "Каталог", () => editDiscoverListing(item));
      listing.setAttribute("aria-label", `Каталог: ${item.name}`);
      listing.disabled = state.busy;
      actions.append(schedule, share, price, listing, embed, questions, invites, integration, copy);
    }
    if (template) {
      const detach = button("outline-action", "Отвязать шаблон", () => detachResourceTemplate(item, template));
      detach.setAttribute("aria-label", `Отвязать шаблон: ${item.name}`);
      detach.disabled = state.busy;
      actions.append(detach);
    }
    const status = item.isArchived ? "В архиве" : item.publicLinkId ? "По ссылке" : "Активен";
    row.append(title, element("span", `pill ${item.isArchived ? "past" : "available"}`, status), actions);
    return row;
  }) : [element("p", "list-empty", search ? "По запросу ничего не найдено." : "Добавьте первый ресурс.")]));

  const form = section("service-form");
  const field = form.elements;
  const selected = field.resourceId.value;
  const active = workspace.catalog.filter(item => !item.isArchived);
  field.resourceId.replaceChildren(...active.map(item => new Option(item.name, item.id)));
  if (active.some(item => item.id === selected)) field.resourceId.value = selected;
  for (const input of form.elements) input.disabled = state.busy || !active.length;
  renderServiceZone();
}

function editBookingQuestions(resource) {
  const organization = state.organizationId;
  const id = encodeURIComponent(resource.id);
  return run(async () => {
    const current = await api(`/api/resources/${id}/booking-form`, undefined, organization);
    openBookingFormEditor(resource, current,
      payload => api(`/api/workspace/resources/${id}/booking-form`, jsonRequest("PUT", payload), organization),
      () => run(async () => {
        ui.status.textContent = "Вопросы к брони сохранены.";
        await refreshCalendar();
        await loadWorkspace();
      }));
  });
}

function serviceTimeZone() {
  return serviceResource()?.timeZone ?? defaultTimeZone;
}

function serviceResource() {
  const selected = section("service-form").elements.resourceId.value;
  return workspace.catalog.find(item => item.id === selected);
}

function renderServiceZone() {
  const field = section("service-form").elements;
  const zone = serviceTimeZone();
  section("service-end-caption").textContent = serviceResource()?.flexibleBooking ? "До (конец работ)" : "Последний старт";
  const today = zoneFormats(zone).day.format(new Date());
  section("service-zone").textContent = field.resourceId.value ? `Время ресурса · ${timeZoneLabel(zone)}` : "";
  field.date.min = today;
  if (!field.date.value || field.date.value < today) field.date.value = isoPlusDays(today, 1);
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

function editResource(item, template = null) {
  workspace.editing = item;
  section("resource-dialog-title").textContent = item ? "Изменить ресурс" : "Новый ресурс";
  section("archive-field").hidden = !item;
  section("resource-error").hidden = true;
  const fields = section("resource-form").elements;
  fields.name.value = item?.name ?? "";
  fields.location.value = item?.location ?? "";
  fields.type.value = item?.type ?? "other";
  fields.capacity.value = item?.capacity ?? "";
  fields.equipment.value = item?.equipment?.join(", ") ?? "";
  fields.requiresApproval.checked = item?.requiresApproval ?? false;
  fields.isArchived.checked = item?.isArchived ?? false;
  prepareResourceTemplate(item, template);
  section("resource-dialog").showModal();
}

function formProblem(node, error, zone = viewerZone) {
  const conflicts = error.conflicts?.map(value => timeWithOffset(value, zone)).join(", ");
  node.textContent = conflicts ? `${error.message} Занято — ${conflicts}.` : error.message;
  node.classList.add("error");
  node.hidden = false;
}

function editPublicLink(item) {
  workspace.sharing = item;
  section("public-link-resource").textContent = item.name;
  const fields = section("public-link-form").elements;
  fields.enabled.checked = Boolean(item.publicLinkId);
  fields.photoUrl.value = item.publicPhotoUrl ?? "";
  section("public-link-message").hidden = true;
  showPublicLink(item.publicLinkId);
  section("public-link-dialog").showModal();
}

function showPublicLink(linkId) {
  section("public-link-result").hidden = !linkId;
  const url = linkId ? new URL(`/book/${linkId}`, location.origin).href : "";
  section("public-link-url").value = url;
  section("public-link-open").href = url;
}

function publicLinkMessage(text) {
  const message = section("public-link-message");
  message.hidden = false;
  message.classList.remove("error");
  message.textContent = text;
}

function bindPublicLinks() {
  const dialog = section("public-link-dialog");
  section("public-link-close").addEventListener("click", () => dialog.close());
  section("public-link-copy").addEventListener("click", async () => {
    const field = section("public-link-url");
    try {
      await navigator.clipboard.writeText(field.value);
      publicLinkMessage("Ссылка скопирована.");
    } catch {
      field.select();
      publicLinkMessage("Ссылка выделена. Скопируйте её из поля.");
    }
  });
  section("public-link-form").addEventListener("submit", event => {
    event.preventDefault();
    const fields = event.currentTarget.elements;
    const resource = workspace.sharing;
    const payload = { enabled: fields.enabled.checked, photoUrl: fields.photoUrl.value };
    section("public-link-message").hidden = true;
    run(async () => {
      try {
        const path = `/api/workspace/resources/${encodeURIComponent(resource.id)}/public-link`;
        const settings = await api(path, jsonRequest("PUT", payload));
        showPublicLink(settings.publicLinkId);
        publicLinkMessage(settings.publicLinkId ? "Доступ по ссылке открыт." : "Доступ по ссылке закрыт.");
        await loadWorkspace();
      } catch (error) {
        formProblem(section("public-link-message"), error);
      }
    });
  });
}

function jsonRequest(method, payload) {
  return { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) };
}

function bindWorkspace() {
  bindPublicLinks();
  bindResourceSchedules();
  bindGroupSessions();
  bindAnalytics();
  bindResourceTemplates();
  bindRouting();
  bindIntegrations();
  document.querySelector(".skip-link").addEventListener("click", event => {
    event.preventDefault();
    section("main-content").focus();
  });
  window.addEventListener("hashchange", () => {
    renderWorkspaceScreen();
    if (currentScreen() === "availability") refreshAvailability();
    else loadWorkspace();
  });
  for (const control of document.querySelectorAll("[data-agenda]")) control.addEventListener("click", () => {
    workspace.agendaFilter = control.dataset.agenda;
    renderAgenda();
  });
  setInterval(() => {
    if (state.busy) return;
    const screen = currentScreen();
    if (screen === "mine" && (workspace.agenda?.waiting.length
      || workspace.agenda?.upcoming.some(item => item.holdExpiresAt || item.approvalStatus === "pending"))
      || screen === "approvals" && approvalQueue.length) loadWorkspace();
  }, 4000);
  section("resource-search").addEventListener("input", renderCatalog);
  section("add-resource").addEventListener("click", () => editResource(null));
  section("resource-cancel").addEventListener("click", () => section("resource-dialog").close());
  section("resource-form").addEventListener("submit", event => {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const equipment = data.get("equipment").split(",").map(item => item.trim()).filter(Boolean);
    const payload = {
      name: data.get("name"), location: data.get("location"), isArchived: data.has("isArchived"),
      requiresApproval: form.elements.requiresApproval.checked,
      details: { type: data.get("type"), capacity: data.get("capacity") ? Number(data.get("capacity")) : null, equipment }
    };
    if (!workspace.editing && form.elements.templateKey.value) {
      const template = templateByKey(form.elements.templateKey.value);
      payload.templateKey = form.elements.templateKey.value;
      payload.templateRevision = template?.revision;
    }
    const message = section("resource-error");
    message.hidden = true;
    run(async () => {
      try {
        const suffix = workspace.editing ? `/${encodeURIComponent(workspace.editing.id)}` : "";
        await api("/api/workspace/resources" + suffix, jsonRequest(workspace.editing ? "PUT" : "POST", payload));
      } catch (error) {
        if (!workspace.editing && payload.templateKey && error.status === 409) {
          await loadWorkspace();
          prepareResourceTemplate(null, null);
        }
        formProblem(message, error);
        return;
      }
      section("resource-dialog").close();
      await enterOrganization(state.organizationId);
    });
  });
  const serviceForm = section("service-form");
  serviceForm.elements.resourceId.addEventListener("change", renderServiceZone);
  serviceForm.addEventListener("submit", event => {
    event.preventDefault();
    const data = Object.fromEntries(new FormData(serviceForm));
    if (serviceResource()?.flexibleBooking) {
      data.untilTime = data.throughTime;
      delete data.throughTime;
    }
    const message = section("service-message");
    message.hidden = true;
    run(async () => {
      try {
        const result = await api("/api/maintenance/window", jsonRequest("POST", data));
        message.classList.remove("error");
        message.textContent = data.untilTime ? "Интервал обслуживания закрыт." : `Закрыто слотов — ${result.count}.`;
        message.hidden = false;
        serviceForm.elements.reason.value = "";
        if (state.calendar) await refreshCalendar();
        await loadWorkspace();
      } catch (error) {
        formProblem(message, error, serviceTimeZone());
      }
    });
  });
}
