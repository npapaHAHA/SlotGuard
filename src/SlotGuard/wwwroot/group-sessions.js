const groupSessions = { mine: false, active: null, data: null, load: 0, seats: new Set() };
const sessionPath = "/api/group-sessions";

function groupNotice(text, error = false) {
  const notice = section("group-session-message");
  notice.textContent = text;
  notice.dataset.tone = error ? "error" : "success";
  notice.hidden = !text;
}

function resetGroupSessions() {
  groupSessions.load++;
  groupSessions.mine = false;
  groupSessions.active = null;
  groupSessions.data = null;
  groupSessions.seats.clear();
  section("group-sessions-list").replaceChildren();
  section("group-session-detail").replaceChildren();
  groupNotice("");
}

function renderGroupControls() {
  const create = section("new-group-session");
  create.hidden = !isAdmin();
  create.disabled = state.busy || !groupResources().length;
  for (const control of document.querySelectorAll("[data-session-list]")) {
    control.disabled = state.busy;
    control.setAttribute("aria-pressed", String((control.dataset.sessionList === "mine") === groupSessions.mine));
  }
  for (const control of document.querySelectorAll("[data-session-control]")) control.disabled = state.busy;
}

async function loadGroupSessions(force = false) {
  const load = ++groupSessions.load;
  const organization = state.organizationId;
  const mine = groupSessions.mine;
  const sessions = await api(sessionPath + (mine ? "?mine=true" : ""), undefined, organization);
  if (load !== groupSessions.load || currentScreen() !== "sessions") return;
  const id = groupSessions.active ?? sessions[0]?.id;
  let detail = null;
  if (id) {
    try {
      detail = await api(`${sessionPath}/${id}`, undefined, organization);
    } catch (error) {
      if (error.status !== 404) throw error;
      if (load !== groupSessions.load) return;
      groupSessions.active = null;
      if (sessions.length && !sessions.some(session => session.id === id)) return loadGroupSessions();
    }
  }
  if (load !== groupSessions.load || currentScreen() !== "sessions" || organization !== state.organizationId) return;
  const now = Date.now();
  const phase = detail ? sessionPhase(detail.session, now) : "";
  const data = JSON.stringify({ sessions, detail, phase, mine, zone: viewerZone });
  if (!force && data === groupSessions.data) return;
  groupSessions.data = data;
  renderGroupSessions(sessions, detail, phase);
}

function sessionPhase(session, now) {
  if (session.cancelledAt) return "cancelled";
  if (Date.parse(session.endsAt) <= now) return "finished";
  if (Date.parse(session.startsAt) <= now) return "started";
  return Date.parse(session.startsAt) - 900000 <= now ? "arrival" : "open";
}

function openGroupSession(id = null, mine = false) {
  groupSessions.load++;
  groupSessions.active = id;
  groupSessions.mine = mine;
  groupSessions.seats.clear();
  groupNotice("");
  if (currentScreen() === "sessions") return loadWorkspace();
  location.hash = "sessions";
}

function seatLabel(seat, session) {
  if (seat.attendance) return seat.attendance === "present" ? "Пришёл" : "Неявка";
  if (seat.status === "pending" && Date.parse(session.startsAt) <= Date.now()) return "Срок заявки истёк";
  return { pending: "На согласовании", confirmed: "Место подтверждено", waiting: "В листе ожидания",
    rejected: "Отклонено", cancelled: "Отменено" }[seat.status];
}

function seatPill(seat, session) {
  const color = seat.attendance === "no-show" ? "rejected" : seat.status;
  return element("span", `pill seat-badge ${color}`, seatLabel(seat, session));
}

function seatsRemaining(session) {
  const meter = element("span", "group-seats-meter");
  const text = session.cancelledAt ? "Занятие отменено" : !session.remainingSeats ? "Все места заняты"
    : `${session.remainingSeats} из ${session.capacity} мест свободно`;
  const progress = element("progress", "");
  progress.max = session.capacity;
  progress.value = session.cancelledAt ? 0 : session.capacity - session.remainingSeats;
  progress.setAttribute("aria-label", "Заполненность занятия");
  meter.append(element("span", "", text), progress);
  return meter;
}

function groupControl(style, label, action) {
  const control = button(style, label, action);
  control.dataset.sessionControl = "";
  return control;
}

function renderGroupSessions(sessions, detail, phase) {
  const list = section("group-sessions-list");
  const panel = section("group-session-detail");
  const selected = detail?.session.id;
  const focused = document.activeElement?.dataset.sessionId;

  if (sessions.length === 0) {
    list.replaceChildren(availabilityEmpty(groupSessions.mine ? "У вас пока нет мест" : "Занятий пока нет",
      groupSessions.mine ? "Запишитесь на занятие, чтобы следить за его статусом здесь."
        : isAdmin() ? "Создайте занятие и откройте запись для участников." : "Здесь появятся занятия вашей организации."));
  } else {
    list.replaceChildren(...sessions.map(session => {
      const card = groupControl("panel group-session-card", undefined, () => openGroupSession(session.id, groupSessions.mine));
      card.setAttribute("aria-controls", "group-session-detail");
      card.dataset.sessionId = session.id;
      card.setAttribute("aria-pressed", String(session.id === selected));
      card.append(element("time", "group-card-time", appointmentTime(session.startsAt, viewerZone, true, session.endsAt)),
        element("strong", "group-card-title", session.title), element("span", "group-card-location", session.resourceName));
      if (session.mine) card.append(seatPill(session.mine, session));
      if (Date.parse(session.startsAt) > Date.now() || session.cancelledAt) card.append(seatsRemaining(session));
      return card;
    }));
  }

  panel.replaceChildren(...(detail ? [groupSessionDetail(detail, phase)] : []));
  renderGroupControls();
  if (focused) [...list.children].find(item => item.dataset.sessionId === focused)?.focus({ preventScroll: true });
}

function groupSessionDetail(view, phase) {
  const { session, form, participants } = view;
  const card = element("article", "panel group-detail-card");
  const heading = element("header", "group-detail-heading");
  const date = element("time", "group-card-time", appointmentTime(session.startsAt, viewerZone, true, session.endsAt));
  date.dateTime = session.startsAt;
  heading.append(date, element("h2", "", session.title),
    element("p", "", `${session.resourceName} · ${session.location}`));
  if (phase === "open" || phase === "arrival") heading.append(seatsRemaining(session));
  else heading.append(element("span", "pill", { started: "Идёт занятие", finished: "Занятие завершено",
    cancelled: "Занятие отменено" }[phase]));
  card.append(heading);

  if (session.mine) {
    const receipt = element("div", "group-seat-receipt");
    receipt.append(element("strong", "", "Моё место"), seatPill(session.mine, session));
    if (session.mine.decisionReason) receipt.append(element("p", "", session.mine.decisionReason));
    card.append(receipt);
  }

  const actions = element("div", "group-detail-actions");
  const open = phase === "open" || phase === "arrival";
  if (view.canJoin && open) {
    const label = session.requiresApproval ? "Отправить заявку" : "Забронировать место";
    actions.append(groupControl("page-action", label, () => joinGroupSession(session, form, label)));
  }
  if (view.canCancelSeat && open) actions.append(groupControl("outline-action", "Отменить своё место",
    () => changeGroupSession(`${sessionPath}/${session.id}/seat`, { method: "DELETE" }, "Место освобождено.")));
  if (participants && open) actions.append(groupControl("outline-action", "Отменить занятие", () => {
    if (window.confirm(`Отменить занятие «${session.title}» и освободить все места?`))
      changeGroupSession(`${sessionPath}/${session.id}`, { method: "DELETE" }, "Занятие отменено.");
  }));
  if (actions.children.length) card.append(actions);
  if (view.bookingMessage && open) card.append(element("p", "form-message", view.bookingMessage));
  if (view.canJoin && open && session.requiresApproval)
    card.append(element("p", "group-card-location", "Заявка удержит место до решения администратора."));
  if (participants) card.append(groupRoster(session, participants, phase));
  return card;
}

function joinGroupSession(session, form, label) {
  const org = state.organizationId;
  return withBookingQuestions({
    resourceId: session.resourceId, form, context: session.title, label,
    submit: bookingForm => api(`${sessionPath}/${session.id}/seat`, jsonRequest("POST", { bookingForm }), org),
    onBooked: async seat => {
      groupNotice(seat.status === "pending" ? "Заявка на место отправлена." : "Место забронировано.");
      await loadGroupSessions();
    },
    onError: async error => {
      groupNotice(error.message, true);
      await loadGroupSessions();
    }
  });
}

function changeGroupSession(path, options, success) {
  const organization = state.organizationId;
  return run(async () => {
    try {
      await api(path, options, organization);
      groupSessions.seats.clear();
      groupNotice(success);
    } catch (error) {
      groupNotice(error.message, true);
    }
    await loadGroupSessions(true);
  });
}

function groupRoster(session, participants, phase) {
  const roster = element("section", "group-roster");
  roster.append(element("h3", "", "Участники"));
  const confirmed = participants.filter(item => item.seat.status === "confirmed");
  const pending = participants.filter(item => item.seat.status === "pending");
  const open = phase === "open" || phase === "arrival";
  const attendance = ["arrival", "started", "finished"].includes(phase);
  const selectable = participants.filter(item => open && item.seat.status === "pending"
    || attendance && item.seat.status === "confirmed");
  const eligibleIds = new Set(selectable.map(item => item.seat.id));
  for (const selected of groupSessions.seats)
    if (!eligibleIds.has(selected)) groupSessions.seats.delete(selected);

  const counts = element("div", "group-roster-totals");
  const chips = [
    [confirmed.length, "Подтверждено"], [open ? pending.length : 0, "На согласовании"],
    [confirmed.filter(item => item.seat.attendance === "present").length, "Пришли"],
    [confirmed.filter(item => item.seat.attendance === "no-show").length, "Неявки"]
  ];
  for (const [count, label] of chips)
    counts.append(element("span", "group-roster-count", `${label} · ${count}`));
  roster.append(counts);
  if (!participants.length) {
    roster.append(element("p", "list-empty", "Записей на занятие пока нет."));
    return roster;
  }

  const toolbar = element("div", "group-roster-actions");
  const list = element("ul", "group-roster-list");
  const choices = new Map();

  function refreshToolbar() {
    toolbar.replaceChildren();
    const chosen = selectable.filter(item => groupSessions.seats.has(item.seat.id));
    const click = (style, label, action) => {
      const control = groupControl(style, label, action);
      control.disabled = state.busy;
      return control;
    };
    const applicable = [open ? pending : [], attendance ? confirmed : []];
    const titles = ["Выбрать все заявки", "Выбрать всех подтверждённых"];
    for (let i = 0; i < applicable.length; i++) {
      if (applicable[i].length < 2) continue;
      toolbar.append(click("outline-action", titles[i], () => {
        groupSessions.seats.clear();
        for (const item of applicable[i]) groupSessions.seats.add(item.seat.id);
        for (const [id, input] of choices) input.checked = groupSessions.seats.has(id);
        refreshToolbar();
      }));
    }
    if (!chosen.length) return;
    const ids = chosen.map(item => item.seat.id);
    toolbar.append(element("span", "group-selection-count", `Выбрано · ${chosen.length}`));
    if (chosen.every(item => item.seat.status === "pending")) {
      toolbar.append(click("page-action", "Одобрить", () => decideGroupSeats(session.id, ids, "confirm")),
        click("outline-action", "Отклонить", () => rejectGroupSeats(session.id, ids)));
    } else if (chosen.every(item => item.seat.status === "confirmed")) {
      toolbar.append(click("page-action", "Отметить приход", () => decideGroupSeats(session.id, ids, "present")));
      if (phase === "finished")
        toolbar.append(click("outline-action", "Отметить неявку", () => decideGroupSeats(session.id, ids, "no-show")));
      if (chosen.some(item => item.seat.attendance))
        toolbar.append(click("outline-action", "Снять отметку", () => decideGroupSeats(session.id, ids, "clear-attendance")));
    } else toolbar.append(element("p", "group-card-location", "Выберите заявки или подтверждённых участников отдельно."));
  }

  for (const { seat, name, answers } of participants) {
    const item = element("li", "group-roster-entry");
    const row = element("div", "group-roster-row");
    const label = element("label", "group-roster-name");
    if (eligibleIds.has(seat.id)) {
      const input = element("input", "");
      input.type = "checkbox";
      input.checked = groupSessions.seats.has(seat.id);
      input.dataset.sessionControl = "";
      input.addEventListener("change", () => {
        if (input.checked) groupSessions.seats.add(seat.id);
        else groupSessions.seats.delete(seat.id);
        refreshToolbar();
      });
      choices.set(seat.id, input);
      label.append(input);
    }
    label.append(element("strong", "", name));
    row.append(label, seatPill(seat, session));
    item.append(row);
    if (seat.decisionReason) item.append(element("p", "group-card-location", seat.decisionReason));
    if (answers.length) {
      const details = element("details", "group-roster-answers");
      details.append(element("summary", "", "Ответы к брони"), renderBookingAnswers(answers));
      item.append(details);
    }
    list.append(item);
  }
  refreshToolbar();
  roster.append(toolbar, list);
  return roster;
}

function decideGroupSeats(id, seatIds, action, reason = null) {
  const message = { confirm: "Места подтверждены.", reject: "Заявки отклонены.", present: "Приход отмечен.",
    "no-show": "Неявки отмечены.", "clear-attendance": "Отметки сняты." }[action];
  return changeGroupSession(`${sessionPath}/${id}/participants`, jsonRequest("POST", { seatIds, action, reason }), message);
}

function rejectGroupSeats(id, ids) {
  const dialog = questionDialog("Отклонить заявки", `Выбрано мест · ${ids.length}`);
  const label = element("label", "", "Причина");
  const input = element("textarea", "");
  Object.assign(input, { required: true, maxLength: 240, rows: 3 });
  label.append(input);
  dialog.querySelector("[data-fields]").append(label);
  dialog.querySelector("[data-save]").textContent = "Отклонить";
  dialog.querySelector("form").addEventListener("submit", event => {
    event.preventDefault();
    if (!input.value.trim()) {
      input.setCustomValidity("Укажите причину.");
      input.reportValidity();
      return;
    }
    dialog.close();
    decideGroupSeats(id, ids, "reject", input.value.trim());
  });
  input.addEventListener("input", () => input.setCustomValidity(""));
  document.body.append(dialog);
  dialog.showModal();
}

function groupResources() {
  return state.resources.filter(item => item.type !== "equipment" && (item.capacity ?? 100) >= 2);
}

function bindGroupSessions() {
  section("agenda-seats").addEventListener("click", () => openGroupSession(null, true));
  for (const tab of document.querySelectorAll("[data-session-list]"))
    tab.addEventListener("click", () => openGroupSession(null, tab.dataset.sessionList === "mine"));
  section("new-group-session").addEventListener("click", openGroupSessionEditor);
  // Attendance controls become available as the session moves through its time window.
  setInterval(() => {
    if (currentScreen() === "sessions" && !state.busy && groupSessions.data) {
      const previous = JSON.parse(groupSessions.data);
      if (previous.detail && sessionPhase(previous.detail.session, Date.now()) !== previous.phase)
        loadWorkspace();
    }
  }, 15000);
}

function openGroupSessionEditor() {
  const resources = groupResources();
  if (!isAdmin() || !resources.length || document.querySelector(".group-editor")) return;
  const org = state.organizationId;
  const zone = viewerZone;
  const formats = zoneFormats(zone);
  const dialog = questionDialog("Новое занятие", timeZoneLabel(zone));
  dialog.classList.add("group-editor");
  const fields = dialog.querySelector("[data-fields]");
  fields.className = "group-editor-fields";
  fields.innerHTML = `
    <label>Название<input name="title" required maxlength="100" placeholder="Например, практикум по звукозаписи"></label>
    <label>Ресурс<select name="resourceId" required></select></label>
    <div class="form-pair">
      <label>Длительность<select name="durationMinutes">
        <option value="30">30 минут</option><option value="60" selected>60 минут</option><option value="90">90 минут</option>
      </select></label>
      <label>Число мест<input type="number" name="seats" required min="2" max="100" value="4"></label>
    </div>
    <div class="form-pair">
      <label>Дата<select name="date" required></select></label>
      <label>Начало<select name="startsAt" required></select></label>
    </div>
    <label class="checkbox-label"><input type="checkbox" name="approval">Согласовывать заявки по участникам</label>
    <p class="group-card-location" data-required hidden>У ресурса включено обязательное согласование.</p>`;
  const form = dialog.querySelector("form");
  const { resourceId, durationMinutes, seats, date, startsAt, approval } = form.elements;
  const submit = dialog.querySelector("[data-save]");
  const close = dialog.querySelector("[data-close]");
  const message = dialog.querySelector("[data-message]");
  const required = dialog.querySelector("[data-required]");
  let loading = false;
  let saving = false;
  let rulesRequireApproval = false;
  let version = 0;
  let days = new Map();
  submit.textContent = "Открыть запись";
  resourceId.replaceChildren(...resources.map(item => new Option(item.name, item.id)));

  function showMessage(text, error = false) {
    message.textContent = text;
    message.hidden = !text;
    message.classList.toggle("error", error);
  }
  function updateControls() {
    date.disabled = loading || saving || !days.size;
    startsAt.disabled = date.disabled;
    for (const control of [resourceId, durationMinutes, form.elements.title, seats]) control.disabled = saving;
    approval.disabled = saving || rulesRequireApproval;
    close.disabled = saving;
    submit.disabled = saving || loading || !startsAt.value;
    required.hidden = !rulesRequireApproval;
  }
  function renderStarts(preferred) {
    const starts = days.get(date.value) ?? [];
    const times = starts.map(start => formats.time.format(new Date(start)));
    startsAt.replaceChildren(...starts.map((start, index) => new Option(
      times.indexOf(times[index]) === times.lastIndexOf(times[index]) ? times[index] : timeWithOffset(new Date(start), zone), start)));
    if (starts.includes(preferred)) startsAt.value = preferred;
    updateControls();
  }
  async function fetchChoices() {
    const request = ++version;
    const preferredDay = date.value;
    const preferredStart = startsAt.value;
    loading = true;
    updateControls();
    showMessage("Ищем свободное время…");
    try {
      const query = new URLSearchParams({ durationMinutes: durationMinutes.value });
      const result = await api(`${sessionPath}/resources/${encodeURIComponent(resourceId.value)}/starts?${query}`, undefined, org);
      if (request !== version || !dialog.open) return;
      const capacity = result.capacity;
      seats.max = capacity;
      seats.value = Math.min(Number(seats.value) || 4, capacity);
      rulesRequireApproval = result.requiresApproval;
      days = new Map();
      for (const start of result.starts) {
        const key = formats.day.format(new Date(start));
        const entries = days.get(key) ?? [];
        entries.push(start);
        days.set(key, entries);
      }
      date.replaceChildren(...[...days].map(([key, entries]) =>
        new Option(formats.longDate.format(new Date(entries[0])), key)));
      if (days.has(preferredDay)) date.value = preferredDay;
      renderStarts(preferredStart);
      showMessage(days.size ? "" : "Свободного времени для этой длительности пока нет.");
    } catch (error) {
      if (request !== version || !dialog.open) return;
      days.clear();
      date.replaceChildren();
      startsAt.replaceChildren();
      showMessage(error.message, true);
    } finally {
      if (request === version && dialog.open) {
        loading = false;
        updateControls();
      }
    }
  }

  for (const control of [resourceId, durationMinutes]) control.addEventListener("change", fetchChoices);
  date.addEventListener("change", () => renderStarts());
  dialog.addEventListener("cancel", event => { if (saving) event.preventDefault(); });
  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (loading || saving || !startsAt.value || org !== state.organizationId) return;
    const title = form.elements.title.value.trim();
    if (!title) {
      showMessage("Введите название занятия.", true);
      form.elements.title.focus();
      return;
    }
    const payload = { title, resourceId: resourceId.value, startsAt: startsAt.value, durationMinutes: Number(durationMinutes.value),
      seats: Number(seats.value), requiresApproval: rulesRequireApproval || approval.checked };
    saving = true;
    updateControls();
    try {
      const result = await api(sessionPath, jsonRequest("POST", payload), org);
      dialog.close();
      groupSessions.mine = false;
      groupSessions.active = result.id;
      groupSessions.seats.clear();
      groupNotice("Занятие создано. Запись открыта.");
      await loadWorkspace();
    } catch (error) {
      if ([409, 422].includes(error.status)) await fetchChoices();
      showMessage(error.message, true);
    } finally {
      saving = false;
      if (dialog.open) updateControls();
    }
  });
  document.body.append(dialog);
  dialog.showModal();
  fetchChoices();
}
