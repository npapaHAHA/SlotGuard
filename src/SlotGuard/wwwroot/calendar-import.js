const calendarImportAddress = "/api/workspace/calendar-import";
const calendarImportUi = { plan: null, busy: false, version: 0 };
const calendarImportLabels = {
  ready: "Будет добавлено",
  duplicate: "Уже есть",
  conflict: "Время занято",
  outside: "Вне будущего окна",
  recurring: "Повторяющееся событие",
  transparent: "Свободно или отменено",
  date: "Неподдерживаемая дата или длительность"
};

function importNotice(text, isError = false) {
  const note = section("calendar-import-message");
  note.textContent = text;
  note.classList.toggle("error", isError);
  note.hidden = !text;
}

function clearImportPlan() {
  calendarImportUi.plan = null;
  section("calendar-import-results").hidden = true;
  section("calendar-import-rows").replaceChildren();
}

function resetCalendarImport() {
  calendarImportUi.version++;
  calendarImportUi.busy = false;
  clearImportPlan();
  section("calendar-import-form").reset();
  section("calendar-import-fields").disabled = true;
  section("calendar-import-history").replaceChildren();
  importNotice("");
}

function renderCalendarImport({ resources, history }) {
  const select = section("calendar-import-form").elements.resourceId;
  const active = select.value;
  select.replaceChildren(...resources.map(row => new Option(`${row.name} · ${timeZoneLabel(row.timeZone)}`, row.id)));
  if (resources.some(row => row.id === active)) select.value = active;
  section("calendar-import-fields").disabled = calendarImportUi.busy || !resources.length;
  if (!resources.length) importNotice("Добавьте активный ресурс в каталоге.");
  section("calendar-import-history").replaceChildren(...(history.length ? history.map(row => {
    const item = element("li", "integration-row");
    const copy = element("div", "data-title");
    copy.append(element("strong", "", row.resourceName),
      element("span", "", `${appointmentTime(row.createdAt)} · Добавлено ${row.count} · Пропущено ${row.skipped}`));
    item.append(copy);
    return item;
  }) : [element("li", "list-empty", "Импортов пока нет.")]));
}

function renderImportPlan(plan) {
  section("calendar-import-count").textContent = `К импорту ${plan.ready} · Пропустить ${plan.skipped}`;
  section("calendar-import-rows").replaceChildren(...plan.rows.map(row => {
    const item = element("tr");
    item.insertCell().textContent = row.title;
    item.insertCell().textContent = row.start ? `${appointmentTime(row.start)} → ${appointmentTime(row.end)}` : "—";
    item.insertCell().append(element("span", `pill ${row.status === "ready" ? "approved" : row.status === "conflict" ? "rejected" : "pending"}`,
      calendarImportLabels[row.status]));
    return item;
  }));
  section("calendar-import-commit").textContent = plan.ready ? `Импортировать ${plan.ready}` : "Импортировать";
  section("calendar-import-results").hidden = false;
}

async function runCalendarImport(action, onError) {
  if (calendarImportUi.busy) return;
  const version = ++calendarImportUi.version;
  const org = state.organizationId;
  const isCurrent = () => version === calendarImportUi.version && org === state.organizationId;
  calendarImportUi.busy = true;
  section("calendar-import-fields").disabled = true;
  section("calendar-import-commit").disabled = true;
  importNotice("");
  try {
    await action(org, isCurrent);
  } catch (error) {
    if (!isCurrent()) return;
    onError?.(error);
    importNotice(error.message, true);
  } finally {
    if (isCurrent()) {
      calendarImportUi.busy = false;
      section("calendar-import-fields").disabled = section("calendar-import-form").elements.resourceId.options.length === 0;
      section("calendar-import-commit").disabled = !calendarImportUi.plan?.result.ready;
    }
  }
}

section("calendar-import-form").addEventListener("change", clearImportPlan);
section("calendar-import-form").addEventListener("submit", event => {
  event.preventDefault();
  const controls = event.currentTarget.elements;
  const file = controls.file.files[0];
  const resourceId = controls.resourceId.value;
  clearImportPlan();
  if (!file || file.size > 256 * 1024) return importNotice("Выберите файл .ics до 256 КБ.", true);
  runCalendarImport(async (org, isCurrent) => {
    const payload = { resourceId, content: await file.text() };
    if (!isCurrent()) return;
    const result = await api(`${calendarImportAddress}/preview`, jsonRequest("POST", payload), org);
    if (!isCurrent()) return;
    calendarImportUi.plan = { payload, result };
    renderImportPlan(result);
  });
});

section("calendar-import-commit").addEventListener("click", () => {
  const plan = calendarImportUi.plan;
  if (!plan?.result.ready) return;
  runCalendarImport(async (org, isCurrent) => {
    const receipt = await api(`${calendarImportAddress}/commit`,
      jsonRequest("POST", { ...plan.payload, revision: plan.result.revision }), org);
    if (!isCurrent()) return;
    clearImportPlan();
    importNotice(receipt.alreadyImported ? "Этот импорт уже сохранён."
      : `Добавлено интервалов: ${receipt.count}. Пропущено: ${receipt.skipped}.`);
    await loadWorkspace();
  }, error => {
    if (error.status === 409 || error.status === 422) clearImportPlan();
  });
});

section("calendar-import-sample").addEventListener("click", () => {
  const start = new Date();
  start.setUTCDate(start.getUTCDate() + 1);
  start.setUTCHours(11, 0, 0, 0);
  const stamp = value => value.toISOString().replace(/[-:]/g, "").replace(/\.\d+Z$/, "Z");
  const contents = ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//SlotGuard//Example//EN", "BEGIN:VEVENT",
    `UID:${start.getTime()}@slotguard`, `DTSTAMP:${stamp(new Date())}`, `DTSTART:${stamp(start)}`,
    `DTEND:${stamp(new Date(start.getTime() + 3600000))}`, "SUMMARY:Пример занятого интервала", "END:VEVENT", "END:VCALENDAR", ""];
  const url = URL.createObjectURL(new Blob([contents.join("\r\n")], { type: "text/calendar" }));
  const link = element("a");
  Object.assign(link, { href: url, download: "slotguard-import.ics" });
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
});
