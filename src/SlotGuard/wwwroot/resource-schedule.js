const scheduleDays = ["Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота", "Воскресенье"];
let scheduleEditor = null;

function scheduleMessage(message) {
  const node = section("schedule-message");
  node.textContent = message;
  node.hidden = !message;
}

function closeSchedule() {
  scheduleEditor = null;
  const dialog = section("schedule-dialog");
  if (dialog.open) dialog.close();
}

function editSchedule(item) {
  const address = scheduleAddress(item.id);
  const organization = state.organizationId;
  return openScheduleEditor({
    name: item.name, title: "График работы", saveLabel: "Сохранить график",
    load: () => api(address, undefined, organization),
    save: payload => api(address, jsonRequest("PUT", payload), organization),
    onSaved: async () => {
      await refreshCalendar();
      await loadWorkspace();
      await refreshAvailability();
      ui.status.textContent = "График сохранён.";
    }
  });
}

async function openScheduleEditor({ name, title, saveLabel, load, save, onSaved, allowExceptions = true }) {
  const editor = { view: "week", saving: false, save, onSaved };
  scheduleEditor = editor;
  section("schedule-title").textContent = title;
  section("schedule-resource").textContent = name;
  section("schedule-toolbar").hidden = true;
  section("schedule-save").textContent = saveLabel;
  section("schedule-save").disabled = true;
  section("schedule-body").replaceChildren(element("p", "schedule-empty", "Загружаем график…"));
  section("schedule-body").scrollTop = 0;
  document.querySelector('[data-schedule-view="exceptions"]').hidden = !allowExceptions;
  scheduleMessage("");
  section("schedule-dialog").showModal();
  try {
    const definition = await load();
    if (scheduleEditor !== editor) return;
    editor.timeZone = definition.timeZone;
    editor.booking = { ...definition.booking };
    editor.policy = { ...definition.policy };
    editor.template = definition.template;
    fillTimeZones(section("schedule-time-zone"), editor.timeZone);
    editor.week = definition.week.map(draftScheduleDay);
    editor.exceptions = definition.exceptions.map(exception => ({ date: exception.date, day: draftScheduleDay(exception.hours) }));
    renderResourceSchedule();
    section("schedule-toolbar").hidden = false;
    section("schedule-save").disabled = false;
  } catch (error) {
    if (scheduleEditor !== editor) return;
    section("schedule-body").replaceChildren();
    scheduleMessage(error.message);
  }
}

function scheduleAddress(id) {
  return `/api/workspace/resources/${encodeURIComponent(id)}/schedule`;
}

function scheduleTime(minutes) {
  const digits = value => String(value).padStart(2, "0");
  return `${digits(Math.floor(minutes / 60))}:${digits(minutes % 60)}`;
}

function scheduleMinutes(value) {
  if (value === "24:00") return 1440;
  if (!/^(?:[01]\d|2[0-3]):[0-5]\d$/.test(value)) return NaN;
  const [hour, minute] = value.split(":").map(Number);
  return hour * 60 + minute;
}

function draftScheduleDay(day) {
  const draft = periods => periods.map(({ from, to }) => ({ from: scheduleTime(from), to: scheduleTime(to) }));
  return { hours: draft(day.hours), breaks: draft(day.breaks) };
}

function scheduleRemoveButton(label, action) {
  const control = button("icon-button schedule-icon-button", "×", action);
  control.setAttribute("aria-label", label);
  return control;
}

function renderResourceSchedule() {
  const editor = scheduleEditor;
  const isWeek = editor.view === "week";
  const body = section("schedule-body");
  for (const control of section("schedule-toolbar").querySelectorAll("[data-schedule-view]"))
    control.setAttribute("aria-pressed", String(control.dataset.scheduleView === editor.view));

  if (editor.view === "policy") {
    body.replaceChildren(schedulePolicyFields(editor.policy));
  } else if (editor.view === "booking") {
    body.replaceChildren(scheduleBookingFields(editor.booking));
  } else if (isWeek) {
    const intro = element("p", "schedule-note", "Часы указаны в местном времени ресурса. Без рабочих часов день считается выходным.");
    body.replaceChildren(intro, ...editor.week.map((day, index) => {
      const card = element("details", "schedule-day");
      const heading = element("summary", "schedule-day-title");
      const status = element("span", "schedule-day-status");
      heading.append(element("strong", "", scheduleDays[index]), status);
      const closeDay = button("text-link schedule-close-day", "Сделать выходным", () => {
        day.hours.length = 0;
        day.breaks.length = 0;
        card.querySelector(".schedule-day-fields").replaceWith(scheduleDayFields(day, scheduleDays[index], updateSummary));
        updateSummary();
        heading.focus();
      });
      function updateSummary() {
        describeScheduleDay(status, day);
        closeDay.hidden = day.hours.length === 0 && day.breaks.length === 0;
      }
      updateSummary();
      card.append(heading, scheduleDayFields(day, scheduleDays[index], updateSummary), closeDay);
      return card;
    }));
  } else {
    const top = element("div", "schedule-exceptions-top");
    const add = button("outline-action", "Добавить дату", () => {
      let date = zoneFormats(editor.timeZone).day.format(new Date());
      while (editor.exceptions.some(item => item.date === date)) date = isoPlusDays(date, 1);
      const exception = { date, day: { hours: [], breaks: [] } };
      editor.exceptions.push(exception);
      const card = scheduleExceptionCard(exception, editor.exceptions.length);
      body.querySelector(".schedule-empty")?.remove();
      body.append(card);
      card.querySelector("input").focus();
      add.disabled = editor.exceptions.length >= 366;
    });
    add.disabled = editor.exceptions.length >= 366;
    top.append(element("p", "schedule-note", "На выбранную дату эти часы заменят обычный график. Оставьте рабочие часы пустыми, чтобы закрыть день."), add);
    const cards = editor.exceptions.map((item, index) => scheduleExceptionCard(item, index + 1));
    body.replaceChildren(top, ...(cards.length ? cards : [element("p", "schedule-empty", "Исключений пока нет")]));
  }
  const lock = editor.template;
  section("schedule-time-zone").disabled = lock?.lockSchedule === true;
  const lockedView = isWeek ? lock?.lockSchedule : editor.view === "exceptions" ? false : lock?.lockRules;
  if (lockedView) {
    for (const control of body.querySelectorAll("input, select, button")) control.disabled = true;
    body.prepend(element("p", "template-lock-note", `Настройки закреплены шаблоном «${lock.name}».`));
  }
  body.scrollTop = 0;
}

function scheduleBookingFields(rules) {
  const content = element("section", "schedule-booking-options");
  const toggle = element("label", "checkbox-label");
  const enabled = Object.assign(element("input", ""), { type: "checkbox", checked: rules.flexible });
  toggle.append(enabled, document.createTextNode("Гибкие слоты"));
  const note = element("p", "schedule-note", "Брони на 30, 60 или 90 минут со стартом каждые 15 минут. Буферы занимают время в расписании ресурса.");
  const fields = element("div", "form-pair");
  for (const [key, caption] of [["bufferBeforeMinutes", "До брони, мин"], ["bufferAfterMinutes", "После брони, мин"]]) {
    const label = element("label", "", caption);
    const field = Object.assign(element("input", ""), { type: "number", value: rules[key], min: 0, max: 60, step: 5 });
    field.addEventListener("input", () => { rules[key] = field.valueAsNumber; });
    label.append(field);
    fields.append(label);
  }
  fields.hidden = !rules.flexible;
  enabled.addEventListener("change", () => { rules.flexible = enabled.checked; fields.hidden = !enabled.checked; });
  content.append(toggle, note, fields);
  return content;
}

function schedulePolicyFields(policy) {
  const content = element("section", "schedule-booking-options");
  const fields = [
    ["minimumNoticeMinutes", "Запись заранее, мин", 0, 120960],
    ["bookingWindowDays", "Окно записи, дней", 1, 84, "По умолчанию"],
    ["maxActiveBookings", "Активных броней на человека", 1, 1000, "Без лимита"],
    ["cancellationNoticeMinutes", "Отмена не позднее чем за, мин", 0, 120960],
    ["rescheduleNoticeMinutes", "Перенос не позднее чем за, мин", 0, 120960]
  ];
  const row = element("div", "form-pair");
  for (const [key, caption, min, max, placeholder] of fields) {
    const label = element("label", "", caption);
    const input = Object.assign(element("input", ""), {
      type: "number", min, max, step: 1, value: policy[key] ?? "", placeholder: placeholder ?? "0"
    });
    input.addEventListener("input", () => { policy[key] = placeholder && input.value === "" ? null : input.valueAsNumber; });
    label.append(input);
    row.append(label);
  }
  const note = element("p", "schedule-note", "Ноль разрешает действие вплоть до начала. В лимит входят активные резервы и заявки на согласовании.");
  content.append(row, note);
  return content;
}

function describeScheduleDay(status, day) {
  const count = day.hours.length;
  const time = day.hours[0];
  status.textContent = count === 0 ? "Выходной" : count === 1 ? `${time.from}–${time.to}` : `Интервалы · ${count}`;
  status.classList.toggle("is-closed", count === 0);
}

function scheduleExceptionCard(item, index) {
  const card = element("section", "schedule-exception");
  const top = element("div", "schedule-exception-header");
  const label = element("label", "", "Дата исключения");
  const date = Object.assign(element("input", ""), { type: "date", value: item.date, required: true });
  date.addEventListener("input", () => { item.date = date.value; });
  label.append(date);
  const remove = scheduleRemoveButton(`Удалить исключение ${index}`, () => {
    scheduleEditor.exceptions = scheduleEditor.exceptions.filter(exception => exception !== item);
    renderResourceSchedule();
    section("schedule-body").querySelector("button").focus();
  });
  top.append(label, remove);
  card.append(top, scheduleDayFields(item.day, `Исключение ${index}`));
  return card;
}

function scheduleDayFields(day, label, changed = () => {}) {
  const fields = element("div", "schedule-day-fields");
  fields.append(schedulePeriods(day.hours, "Рабочие часы", "Добавить часы", label, "09:00", "18:00", changed));
  fields.append(schedulePeriods(day.breaks, "Перерывы", "Добавить перерыв", label, "13:00", "14:00", changed));
  return fields;
}

function schedulePeriods(periods, title, action, day, from, to, changed) {
  const group = element("fieldset", "schedule-periods");
  group.append(element("legend", "", title));
  const rows = element("div", "schedule-period-rows");
  const add = button("outline-action schedule-add", action, () => {
    const period = { from, to };
    periods.push(period);
    const row = createRow(period);
    rows.append(row);
    const first = row.querySelector("input");
    first.focus();
    first.select();
    add.disabled = periods.length >= 12;
    changed();
  });

  function createRow(period) {
    const row = element("div", "schedule-period");
    const timeInput = (field, boundary) => {
      const input = Object.assign(element("input", ""), {
        type: "text", value: period[field], placeholder: "ЧЧ:ММ", maxLength: 5, autocomplete: "off"
      });
      input.setAttribute("aria-label", `${day}, ${title.toLowerCase()}, ${boundary}`);
      input.addEventListener("input", () => { period[field] = input.value; changed(); });
      return input;
    };
    const remove = scheduleRemoveButton(`${day}, удалить интервал «${title.toLowerCase()}»`, () => {
      periods.splice(periods.indexOf(period), 1);
      row.remove();
      add.disabled = false;
      add.focus();
      changed();
    });
    row.append(timeInput("from", "начало"), element("span", "", "–"), timeInput("to", "конец"), remove);
    return row;
  }

  add.disabled = periods.length >= 12;
  rows.append(...periods.map(createRow));
  group.append(rows, add);
  return group;
}

function schedulePayload(editor) {
  function dayPayload(day, label) {
    function periods(items) {
      const ordered = items.map(item => ({ from: scheduleMinutes(item.from), to: scheduleMinutes(item.to) }))
        .sort((left, right) => left.from - right.from);
      if (ordered.some(({ from, to }, index) => !Number.isFinite(from) || !Number.isFinite(to) || from >= to
        || index > 0 && ordered[index - 1].to > from))
        throw new Error(`${label}. Проверьте время в формате ЧЧ:ММ и уберите пересечения внутри группы.`);
      return ordered;
    }
    const hours = periods(day.hours);
    const breaks = periods(day.breaks);
    if (breaks.some(pause => !hours.some(work => work.from <= pause.from && pause.to <= work.to)))
      throw new Error(`${label}. Каждый перерыв должен помещаться внутри рабочих часов.`);
    return { hours, breaks };
  }

  const dates = editor.exceptions.map(item => item.date);
  if (dates.some(date => !date) || new Set(dates).size !== dates.length)
    throw new Error("Укажите уникальную дату для каждого исключения.");
  const { flexible, bufferBeforeMinutes, bufferAfterMinutes } = editor.booking;
  if (flexible && [bufferBeforeMinutes, bufferAfterMinutes].some(value => !Number.isInteger(value)
      || value < 0 || value > 60 || value % 5 !== 0)) throw new Error("Задайте буферы от 0 до 60 минут с шагом 5 минут.");
  const { minimumNoticeMinutes, bookingWindowDays, maxActiveBookings, cancellationNoticeMinutes, rescheduleNoticeMinutes } = editor.policy;
  const valid = (value, min, max) => Number.isInteger(value) && value >= min && value <= max;
  if (![minimumNoticeMinutes, cancellationNoticeMinutes, rescheduleNoticeMinutes].every(value => valid(value, 0, 120960))
    || bookingWindowDays !== null && (!valid(bookingWindowDays, 1, 84) || minimumNoticeMinutes > bookingWindowDays * 1440)
    || maxActiveBookings !== null && !valid(maxActiveBookings, 1, 1000))
    throw new Error("Проверьте сроки и лимит. Минимальный срок записи должен помещаться в окно бронирования.");
  return {
    policy: editor.policy,
    booking: { flexible, bufferBeforeMinutes: flexible ? bufferBeforeMinutes : 0, bufferAfterMinutes: flexible ? bufferAfterMinutes : 0 },
    timeZone: editor.timeZone,
    week: editor.week.map((day, index) => dayPayload(day, scheduleDays[index])),
    exceptions: editor.exceptions.map(item => ({ date: item.date, hours: dayPayload(item.day, item.date) }))
  };
}

function bindResourceSchedules() {
  const dialog = section("schedule-dialog");
  const form = section("schedule-form");
  section("schedule-time-zone").addEventListener("change", event => {
    scheduleEditor.timeZone = event.currentTarget.value;
  });
  for (const id of ["schedule-close", "schedule-cancel"]) section(id).addEventListener("click", closeSchedule);
  dialog.addEventListener("cancel", event => {
    event.preventDefault();
    if (!scheduleEditor?.saving) closeSchedule();
  });
  for (const toggle of form.querySelectorAll("[data-schedule-view]")) toggle.addEventListener("click", () => {
    if (scheduleEditor.view === toggle.dataset.scheduleView) return;
    scheduleEditor.view = toggle.dataset.scheduleView;
    renderResourceSchedule();
  });

  form.addEventListener("submit", event => {
    event.preventDefault();
    const editor = scheduleEditor;
    if (!editor?.week || editor.saving || state.busy) return;
    scheduleMessage("");
    let payload;
    try { payload = schedulePayload(editor); }
    catch (error) { scheduleMessage(error.message); return; }

    editor.saving = true;
    form.inert = true;
    run(async () => {
      try {
        await editor.save(payload);
      } catch (error) {
        const conflicts = error.conflicts?.slice(0, 5).map(value => appointmentTime(value, editor.timeZone)).join(", ");
        const remaining = error.conflicts?.length - 5;
        if (scheduleEditor === editor) scheduleMessage(conflicts
          ? `${error.message} ${conflicts}${remaining > 0 ? ` и ещё ${remaining}` : ""}.`
          : error.message);
        return;
      } finally {
        editor.saving = false;
        form.inert = false;
      }
      if (scheduleEditor !== editor) return;
      closeSchedule();
      await editor.onSaved?.();
    });
  });
}
