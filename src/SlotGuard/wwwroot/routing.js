const wizard = { step: 1, request: null, result: null, version: 0 };
const routingSettingsAddress = "/api/workspace/routing";
let routingSettings = null;

function canReviewBookings() {
  return isAdmin() || state.organizations.some(item => item.id === state.organizationId && item.canReviewAssigned);
}

async function refreshReviewerAccess() {
  const session = state.session;
  const organization = state.organizationId;
  if (!session || !organization) return;
  try {
    const organizations = await api("/api/organizations");
    if (session !== state.session || organization !== state.organizationId) return;
    state.organizations = organizations;
    renderOrganization();
    renderWorkspaceScreen();
  } catch {
    // The next connection or sign-in can refresh the permissions.
  }
}

function wizardNotice(text, tone, booked = false) {
  const notice = section("wizard-message");
  notice.querySelector("p").textContent = text;
  notice.querySelector("a").hidden = !booked;
  notice.dataset.tone = tone ?? "";
  notice.hidden = !text;
}

function showWizardStep(step) {
  wizard.step = step;
  section("wizard-form").hidden = step === 3;
  section("wizard-task").hidden = step !== 1;
  section("wizard-timing").hidden = step !== 2;
  section("wizard-results").hidden = step !== 3;
  for (const marker of document.querySelectorAll("[data-wizard-stage]")) {
    if (Number(marker.dataset.wizardStage) === step) marker.setAttribute("aria-current", "step");
    else marker.removeAttribute("aria-current");
  }
  renderRoutingControls();
}

function resetRouting() {
  wizard.version++;
  wizard.request = wizard.result = routingSettings = null;
  const form = section("wizard-form");
  form.reset();
  const tags = [...new Set(state.resources.flatMap(resource => resource.equipment ?? []))]
    .sort((left, right) => left.localeCompare(right, "ru"));
  section("wizard-equipment").replaceChildren(...tags.map(tag => {
    const label = element("label", "chip-check");
    const input = Object.assign(element("input", ""), { type: "checkbox", name: "equipment", value: tag });
    label.append(input, element("span", "", tag));
    return label;
  }));
  section("wizard-equipment-panel").hidden = !tags.length;
  section("wizard-results").replaceChildren();
  const today = zoneFormats(viewerZone).day.format(new Date());
  form.elements.date.value = isoPlusDays(today, 1);
  wizardNotice("");
  section("routing-settings-form").reset();
  routingRuleMessage("");
  showWizardStep(1);
}

function renderRoutingControls() {
  const form = section("wizard-form");
  const equipment = form.elements.purpose.value === "equipment";
  section("wizard-people-label").hidden = equipment;
  for (const control of section("wizard-task").querySelectorAll("input, button"))
    control.disabled = state.busy || wizard.step !== 1 || control.name === "people" && equipment;
  for (const control of section("wizard-timing").querySelectorAll("input, select, button"))
    control.disabled = state.busy || wizard.step !== 2;
  for (const control of section("wizard-results").querySelectorAll("button")) control.disabled = state.busy;

  form.elements.date.min = state.calendar?.today ?? zoneFormats(viewerZone).day.format(new Date());
  form.elements.date.max = state.calendar?.lastDay ?? isoPlusDays(form.elements.date.min, 84);
  section("wizard-zone").textContent = `Часовой пояс · ${timeZoneLabel(viewerZone)}`;

  for (const control of section("routing-settings-form").elements)
    control.disabled = state.busy || !routingSettings;
}

function collectWizardRequest() {
  const { purpose, date, time, durationMinutes, people } = section("wizard-form").elements;
  const equipment = [...section("wizard-equipment").querySelectorAll("input:checked")].map(item => item.value);
  return {
    answers: { purpose: purpose.value, people: purpose.value === "equipment" ? null : people.valueAsNumber, equipment },
    date: date.value, time: time.value, timeZone: viewerZone, durationMinutes: Number(durationMinutes.value)
  };
}

function editWizard(step) {
  wizard.version++;
  wizard.result = wizard.request = null;
  wizardNotice("");
  showWizardStep(step);
  if (step === 2) {
    const date = section("wizard-form").elements.date;
    if (!date.value || date.value < date.min) date.value = isoPlusDays(date.min, 1);
    date.focus();
  } else section("wizard-task").querySelector("input:checked")?.focus();
}

async function findWizardOptions(payload, passive = false) {
  const version = ++wizard.version;
  const organization = state.organizationId;
  const data = await api("/api/routing/suggest", jsonRequest("POST", payload), organization);
  if (version !== wizard.version || organization !== state.organizationId || currentScreen() !== "wizard") return;
  if (passive && JSON.stringify(wizard.result) === JSON.stringify(data)) return;
  wizard.request = payload;
  wizard.result = data;
  renderWizardResults(data);
  showWizardStep(3);
  if (!passive) reveal(section("wizard-results"), 5);
}

async function refreshWizard() {
  if (wizard.step !== 3 || !wizard.result) return;
  try { await findWizardOptions(wizard.request, true); }
  catch { /* A later update or a new search can retry. */ }
}

async function refreshWizardZone() {
  if (!wizard.request || wizard.request.timeZone === viewerZone) return;
  const instant = new Date(wizard.result.requested);
  const formats = zoneFormats(viewerZone);
  const { date, time } = section("wizard-form").elements;
  date.value = formats.day.format(instant);
  time.value = formats.time.format(instant);
  const request = { ...wizard.request, date: date.value, time: time.value, timeZone: viewerZone };
  try { await findWizardOptions(request, true); }
  catch (error) { editWizard(2); wizardNotice(error.message, "error"); }
}

function renderWizardResults(data) {
  const overview = element("section", "panel wizard-result-summary");
  const description = element("div", "");
  description.append(element("h2", "", data.purpose),
    element("p", "", appointmentTime(data.requested, viewerZone, true)));
  const tags = element("ul", "available-tags");
  tags.append(element("li", "", resourceTypeLabels[data.type]));
  if (data.people) tags.append(element("li", "", `Участники · ${data.people}`));
  for (const tag of data.equipment) tags.append(element("li", "", tag));
  description.append(tags);
  if (data.requiresReview) description.append(element("p", "wizard-reviewer",
    `Согласующий · ${data.reviewer ?? "Администратор организации"}`));
  const controls = element("div", "wizard-result-actions");
  controls.append(button("outline-action", "Изменить ответы", () => editWizard(1)),
    button("outline-action", "Изменить время", () => editWizard(2)));
  overview.append(description, controls);

  const groups = [overview];
  for (const [title, slots] of [["Подходящие ресурсы", data.exact], ["Ближайшие свободные варианты", data.alternatives]]) {
    if (!slots.length) continue;
    const group = element("section", "availability-group wizard-match-group");
    const heading = element("div", "availability-group-heading");
    heading.append(element("h2", "", title), element("span", "result-count", slots.length));
    group.append(heading);
    for (const slot of slots) {
      const resource = { ...slot.resource, requiresApproval: slot.resource.requiresApproval || data.requiresReview };
      group.append(availabilityCard({ ...slot, resource }, false, bookWizardOption));
    }
    groups.push(group);
  }
  if (groups.length === 1)
    groups.push(availabilityEmpty("Подходящих ресурсов пока нет", data.bookingMessage ?? "Измените время, оснащение или число участников."));

  const selectedKey = document.activeElement?.dataset.availableKey;
  const container = section("wizard-results");
  container.replaceChildren(...groups);
  if (selectedKey)
    [...container.querySelectorAll("[data-available-key]")].find(control => control.dataset.availableKey === selectedKey)?.focus();
}

function bookWizardOption(slot) {
  const choice = wizard.result;
  const request = wizard.request;
  const organization = state.organizationId;
  const { resource, startsAt } = slot;
  const label = resource.requiresApproval ? "Отправить заявку" : "Забронировать";
  return withBookingQuestions({
    resourceId: resource.id, context: resource.name, label,
    submit: bookingForm => api("/api/routing/book", jsonRequest("POST", {
      resourceId: resource.id, startsAt, durationMinutes: request.durationMinutes,
      answers: request.answers, revision: choice.revision, bookingForm
    }), organization),
    onBooked: async booking => {
      const status = booking.approvalStatus === "pending" ? "Заявка отправлена" : "Бронь подтверждена";
      wizardNotice(`${status} · ${resource.name}, ${appointmentTime(startsAt, viewerZone, true, slot.endsAt)}.`, "success", true);
      await refreshWizard();
    },
    onError: async error => {
      wizardNotice(error.message, "error");
      if ([400, 404, 409, 422].includes(error.status)) await refreshWizard();
    }
  });
}

function routingRuleMessage(message, error = false) {
  const node = section("routing-rule-message");
  node.textContent = message;
  node.hidden = !message;
  node.classList.toggle("error", error);
}

function renderRoutingSettings(data) {
  routingSettings = data;
  const fields = section("routing-settings-form").elements;
  const selected = fields.purpose.value;
  fields.purpose.replaceChildren(...data.rules.map(rule => new Option(rule.name, rule.purpose)));
  if (data.rules.some(rule => rule.purpose === selected)) fields.purpose.value = selected;
  fields.type.replaceChildren(...Object.entries(resourceTypeLabels).map(([value, label]) => new Option(label, value)));
  fields.reviewerId.replaceChildren(new Option("По правилам ресурса", ""), ...data.reviewers.map(person =>
    new Option(person.name, person.id)));
  applyRoutingSettings();
  renderRoutingControls();
}

function applyRoutingSettings() {
  const fields = section("routing-settings-form").elements;
  const rule = routingSettings?.rules.find(item => item.purpose === fields.purpose.value);
  if (!rule) return;
  fields.type.value = rule.type;
  fields.equipment.value = rule.equipment.join(", ");
  fields.reviewerId.value = rule.reviewerId ?? "";
  routingRuleMessage("");
}

function bindRouting() {
  const form = section("wizard-form");
  form.elements.purpose[0].closest("fieldset").addEventListener("change", renderRoutingControls);
  section("wizard-next").addEventListener("click", () => {
    const payload = collectWizardRequest();
    if (payload.answers.purpose !== "equipment" && !form.elements.people.reportValidity()) return;
    if (payload.answers.equipment.length > 12) {
      wizardNotice("Выберите не более 12 позиций оснащения.", "error");
      return;
    }
    editWizard(2);
  });
  section("wizard-back").addEventListener("click", () => editWizard(1));
  form.addEventListener("submit", event => {
    event.preventDefault();
    if (wizard.step !== 2) return;
    run(async () => {
      wizardNotice("");
      try { await findWizardOptions(collectWizardRequest()); }
      catch (error) { wizardNotice(error.message, "error"); }
    });
  });

  const settings = section("routing-settings-form");
  const fields = settings.elements;
  fields.purpose.addEventListener("change", applyRoutingSettings);
  section("try-routing-rule").addEventListener("click", () => {
    form.elements.purpose.value = fields.purpose.value;
    editWizard(1);
    location.hash = "wizard";
  });
  settings.addEventListener("submit", event => {
    event.preventDefault();
    const rule = routingSettings?.rules.find(item => item.purpose === fields.purpose.value);
    if (!rule) return;
    const payload = { type: fields.type.value, reviewerId: fields.reviewerId.value || null, revision: rule.revision,
      equipment: fields.equipment.value.split(",").map(tag => tag.trim()).filter(Boolean) };
    run(async () => {
      routingRuleMessage("");
      try {
        await api(`${routingSettingsAddress}/${encodeURIComponent(rule.purpose)}`, jsonRequest("PUT", payload));
        renderRoutingSettings(await api(routingSettingsAddress));
        routingRuleMessage("Правило сохранено.");
      } catch (error) {
        if (error.status === 409) {
          try { renderRoutingSettings(await api(routingSettingsAddress)); } catch { /* Keep the current form on disconnect. */ }
        }
        routingRuleMessage(error.message, true);
      }
    });
  });
}
