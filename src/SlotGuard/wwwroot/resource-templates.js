const templateAddress = "/api/workspace/resource-templates";

function templateByKey(key) {
  return workspace.templates.find(template => template.key === key);
}

function templateScheduleLabel(schedule) {
  const open = schedule.week.filter(day => day.hours.length);
  const first = open[0]?.hours;
  const officeWeek = open.length === 5 && schedule.week.slice(0, 5).every(day => day.hours.length === 1)
    && first.length === 1 && open.every(day => day.hours[0].from === first[0].from && day.hours[0].to === first[0].to);
  const hours = officeWeek ? `Пн–Пт · ${scheduleTime(first[0].from)}–${scheduleTime(first[0].to)}`
    : open.length ? `Рабочих дней в неделю · ${open.length}` : "Все дни закрыты";
  return `${hours} · ${timeZoneLabel(schedule.timeZone)}`;
}

function templateRuleLabel(settings) {
  return settings.requiresApproval ? "По согласованию" : "Мгновенное подтверждение";
}

function templateLocks(settings) {
  return [settings.lockSchedule && "График", settings.lockRules && "Правила бронирования"].filter(Boolean);
}

function renderTemplateControls() {
  section("add-resource-template").disabled = state.busy || !workspace.templates.length;
  for (const control of section("template-cards").querySelectorAll("button")) control.disabled = state.busy;
}

function renderResourceTemplates() {
  const cards = workspace.templates.map(template => {
    const card = element("article", "template-card");
    const { name, settings, revision, linkedCount } = template;
    const title = element("div", "template-card-heading");
    title.append(symbol(settings.requiresApproval ? "build" : "meeting_room"), element("h3", "", name));
    const tags = element("div", "template-tags");
    if (settings.details.capacity) tags.append(element("span", "", `Вместимость · ${settings.details.capacity}`));
    if (revision) tags.append(element("span", "", `Связано · ${linkedCount}`));
    for (const label of templateLocks(settings)) tags.append(element("span", "template-lock-tag", label));
    const create = button("page-action", "Создать ресурс", () => editResource(null, template));
    create.setAttribute("aria-label", `Создать ресурс из шаблона «${name}»`);
    const change = button("outline-action", revision ? "Настроить" : "Скопировать", () => openTemplateEditor(template));
    change.setAttribute("aria-label", `${revision ? "Настроить" : "Скопировать"} шаблон «${name}»`);
    const actions = element("div", "template-actions");
    actions.append(create, change);
    card.append(title, element("p", "template-schedule-info", templateScheduleLabel(settings.schedule)),
      element("p", "template-schedule-info", templateRuleLabel(settings)), tags, actions);
    return card;
  });
  section("template-cards").replaceChildren(...cards);
  renderTemplateControls();
}

function prepareResourceTemplate(item, template) {
  const fields = section("resource-form").elements;
  const chooser = fields.templateKey;
  section("resource-template-field").hidden = Boolean(item);
  const options = workspace.templates.map(model => new Option(model.revision ? `${model.name} · шаблон организации` : model.name, model.key));
  chooser.replaceChildren(new Option("Без шаблона", ""), ...options);
  chooser.value = template?.key ?? "";
  if (item) {
    const source = templateByKey(item.templateId);
    fields.requiresApproval.disabled = source?.settings.lockRules === true;
    const locks = source ? templateLocks(source.settings) : [];
    const note = section("resource-template-hint");
    note.textContent = source ? `Шаблон «${source.name}»${locks.length ? ` · закреплено: ${locks.join(", ").toLocaleLowerCase("ru")}` : ""}` : "";
    note.hidden = !source;
  } else applyResourceTemplate();
}

function applyResourceTemplate() {
  const fields = section("resource-form").elements;
  const template = templateByKey(fields.templateKey.value);
  const settings = template?.settings;
  const note = section("resource-template-hint");
  note.hidden = !settings;
  fields.requiresApproval.disabled = settings?.lockRules === true;
  if (settings) {
    fields.type.value = settings.details.type;
    fields.capacity.value = settings.details.capacity ?? "";
    fields.equipment.value = settings.details.equipment.join(", ");
    fields.requiresApproval.checked = settings.requiresApproval;
    note.textContent = `${templateScheduleLabel(settings.schedule)} · ${templateRuleLabel(settings)}`;
  }
}

function bindResourceTemplates() {
  section("resource-form").elements.templateKey.addEventListener("change", applyResourceTemplate);
  section("add-resource-template").addEventListener("click", () => {
    const base = workspace.templates[0];
    if (base) openTemplateEditor({ name: "", settings: base.settings });
  });
}

function templateFromResource(item) {
  const organization = state.organizationId;
  return run(async () => {
    const { week, booking, policy, timeZone } = await api(scheduleAddress(item.id), undefined, organization);
    if (organization !== state.organizationId) return;
    const details = { type: item.type, capacity: item.capacity, equipment: item.equipment };
    openTemplateEditor({
      name: item.name,
      settings: { details, schedule: { week, booking, policy, timeZone, exceptions: [] }, requiresApproval: item.requiresApproval }
    });
  });
}

function detachResourceTemplate(item, template) {
  if (!window.confirm(`Отвязать «${item.name}» от шаблона «${template.name}»? Текущие настройки сохранятся.`)) return;
  const address = `/api/workspace/resources/${encodeURIComponent(item.id)}/template`;
  return run(async () => {
    await api(address, { method: "DELETE" });
    await loadWorkspace();
    ui.status.textContent = "Ресурс отвязан от шаблона.";
  });
}

function openTemplateEditor(source) {
  const org = state.organizationId;
  const existing = Boolean(source.revision);
  const dialog = questionDialog(existing ? "Настроить шаблон" : "Новый шаблон", "");
  dialog.classList.add("resource-template-editor");
  dialog.querySelector("header p").hidden = true;
  const fields = dialog.querySelector("[data-fields]");
  fields.className = "template-editor-fields";
  fields.innerHTML = `
    <label>Название шаблона<input name="name" required maxlength="100" autocomplete="off"></label>
    <div class="form-pair">
      <label>Тип ресурса<select name="type">
        <option value="other">Другое</option><option value="room">Помещение</option>
        <option value="equipment">Оборудование</option><option value="studio">Студия</option>
      </select></label>
      <label>Вместимость<input name="capacity" type="number" min="1" max="10000"></label>
    </div>
    <label>Оснащение через запятую<input name="equipment" maxlength="520"></label>
    <label class="checkbox-label"><input name="requiresApproval" type="checkbox">Брони требуют согласования</label>
    <div class="template-schedule-preview"><p data-template-schedule></p>
      <button type="button" class="outline-action" data-edit-template-schedule>График и правила</button>
    </div>
    <fieldset class="template-setting-locks">
      <legend>Общие настройки связанных ресурсов</legend>
      <label class="checkbox-label"><input name="lockSchedule" type="checkbox">Закрепить рабочую неделю и часовой пояс</label>
      <label class="checkbox-label"><input name="lockRules" type="checkbox">Закрепить правила и согласование</label>
      <p>Закреплённые настройки обновляются вместе с шаблоном. Исключения по датам задаются в каждом ресурсе.</p>
    </fieldset>`;
  const form = dialog.querySelector("form");
  const input = form.elements;
  const summary = dialog.querySelector("[data-template-schedule]");
  const submit = dialog.querySelector("[data-save]");
  const message = dialog.querySelector("[data-message]");
  const conflicts = element("ul", "template-conflicts");
  message.after(conflicts);
  const endpoint = existing ? `${templateAddress}/${encodeURIComponent(source.key)}` : templateAddress;
  let schedule = source.settings.schedule;
  let busy = false;
  input.name.value = source.name;
  input.type.value = source.settings.details.type;
  input.capacity.value = source.settings.details.capacity ?? "";
  input.equipment.value = source.settings.details.equipment.join(", ");
  input.requiresApproval.checked = source.settings.requiresApproval;
  input.lockSchedule.checked = source.settings.lockSchedule ?? false;
  input.lockRules.checked = source.settings.lockRules ?? false;
  summary.textContent = templateScheduleLabel(schedule);
  submit.textContent = existing ? "Сохранить шаблон" : "Создать шаблон";
  dialog.querySelector("[data-edit-template-schedule]").addEventListener("click", () => openScheduleEditor({
    name: input.name.value.trim() || "Новый шаблон", title: "Настройки шаблона", saveLabel: "Применить настройки",
    load: () => schedule,
    allowExceptions: false,
    save: payload => {
      schedule = payload;
      summary.textContent = templateScheduleLabel(schedule);
    }
  }));

  function busyState(value) {
    busy = value;
    for (const control of form.elements) control.disabled = value;
  }

  function showError(error) {
    message.textContent = error.message;
    message.hidden = false;
    conflicts.replaceChildren(...(error.templateConflicts ?? []).map(conflict => {
      const timings = conflict.starts.slice(0, 3).map(start => appointmentTime(start, conflict.timeZone)).join(", ");
      const rest = conflict.starts.length > 3 ? ` и ещё ${conflict.starts.length - 3}` : "";
      return element("li", "", `${conflict.resourceName} · ${timings}${rest}`);
    }));
  }

  async function submitTemplate(options, success) {
    if (busy || org !== state.organizationId) return;
    message.hidden = true;
    conflicts.replaceChildren();
    busyState(true);
    try {
      await api(endpoint, options, org);
      dialog.close();
      await refreshCalendar();
      await loadWorkspace();
      ui.status.textContent = success;
    } catch (error) {
      if (error.status === 409 && !error.templateConflicts) await loadWorkspace();
      showError(error);
    } finally {
      busyState(false);
    }
  }

  if (existing && !source.linkedCount) {
    const remove = button("text-link template-delete", "Удалить шаблон", () => {
      if (window.confirm(`Удалить шаблон «${source.name}»?`))
        submitTemplate({ method: "DELETE" }, "Шаблон удалён.");
    });
    dialog.querySelector("footer").prepend(remove);
  }
  dialog.addEventListener("cancel", event => { if (busy) event.preventDefault(); });
  form.addEventListener("submit", event => {
    event.preventDefault();
    const payload = {
      name: input.name.value.trim(), revision: source.revision,
      settings: {
        details: { type: input.type.value, capacity: input.capacity.value ? Number(input.capacity.value) : null,
          equipment: input.equipment.value.split(",").map(value => value.trim()).filter(Boolean) },
        schedule, requiresApproval: input.requiresApproval.checked, lockSchedule: input.lockSchedule.checked, lockRules: input.lockRules.checked
      }
    };
    submitTemplate(jsonRequest(existing ? "PUT" : "POST", payload), "Шаблон сохранён.");
  });
  document.body.append(dialog);
  dialog.showModal();
}
