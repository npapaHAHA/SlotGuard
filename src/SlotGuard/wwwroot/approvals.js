let approvalQueue = [];
let selectedApproval = null;
const approvalDrafts = new Map();

function approvalMessage(message, error = false) {
  const node = section("approval-message");
  node.textContent = message;
  node.classList.toggle("error", error);
  node.hidden = !message;
}

function resetApprovals() {
  approvalQueue = [];
  selectedApproval = null;
  approvalDrafts.clear();
  section("approval-list").replaceChildren();
  section("approval-detail").replaceChildren();
  section("approval-board").hidden = true;
  section("approval-empty").hidden = true;
  approvalMessage("");
}

function renderApprovalControls() {
  for (const control of section("approval-board").querySelectorAll("button, textarea"))
    control.disabled = state.busy;
}

function renderApprovals(requests) {
  const list = section("approval-list");
  const empty = section("approval-empty");
  if ((!empty.hidden || list.childElementCount) && JSON.stringify(requests) === JSON.stringify(approvalQueue)) return;

  const focusedId = document.activeElement?.dataset.approvalId;
  const focusInDetail = section("approval-detail").contains(document.activeElement);
  const previousId = selectedApproval?.id;
  const scroll = list.scrollTop;
  approvalQueue = requests;
  const openIds = new Set(requests.map(request => request.id));
  for (const id of approvalDrafts.keys()) {
    if (!openIds.has(id)) approvalDrafts.delete(id);
  }
  const next = requests.find(request => request.id === selectedApproval?.id) ?? requests[0];
  section("approval-board").hidden = !next;
  empty.hidden = Boolean(next);
  section("approval-count").textContent = requests.length;

  let group;
  let previousDay;
  const groups = [];
  for (const request of requests) {
    const date = new Date(request.startsAt);
    const day = zoneFormats(viewerZone).shortDate.format(date);
    if (day !== previousDay) {
      group = element("div", "approval-day");
      group.append(element("h3", "", day));
      groups.push(group);
      previousDay = day;
    }
    const row = button("approval-row", undefined, () => {
      selectApproval(request);
      section("approval-detail-title").focus();
    });
    row.dataset.approvalId = request.id;
    row.setAttribute("aria-controls", "approval-detail");
    const time = element("time", "", slotTimeRange(date, viewerZone, request.endsAt));
    time.dateTime = request.startsAt;
    const text = element("span", "approval-row-text");
    const label = request.bundle ? request.bundle.map(item => item.resourceName).join(" + ") : request.resourceName;
    text.append(element("strong", "", label), element("span", "", request.requester));
    row.append(time, text);
    group.append(row);
  }
  list.replaceChildren(...groups);
  selectApproval(next);
  list.scrollTop = scroll;
  if (focusedId && !state.busy) queueButton(focusedId)?.focus({ preventScroll: true });
  if (focusInDetail && next && next.id !== previousId) section("approval-detail-title").focus({ preventScroll: true });
}

function queueButton(id) {
  return [...section("approval-list").querySelectorAll(".approval-row")].find(row => row.dataset.approvalId === id);
}

function selectApproval(request) {
  for (const row of section("approval-list").querySelectorAll(".approval-row"))
    row.setAttribute("aria-pressed", String(row.dataset.approvalId === request?.id));
  if (JSON.stringify(selectedApproval) !== JSON.stringify(request)) {
    selectedApproval = request;
    section("approval-detail").replaceChildren(...(request ? [approvalSummary(request), approvalForm(request)] : []));
  }
  renderApprovalControls();
}

function approvalSummary(request) {
  const date = new Date(request.startsAt);
  const header = element("div", "approval-detail-header");
  const appointment = element("div", "approval-appointment");
  const time = element("time", "approval-start", slotTimeRange(date, viewerZone, request.endsAt));
  time.dateTime = request.startsAt;
  appointment.append(time, element("span", "approval-date", zoneFormats(viewerZone).shortDate.format(date)));
  const title = element("h2", "", request.bundle ? "Комплект ресурсов" : request.resourceName);
  title.id = "approval-detail-title";
  title.tabIndex = -1;
  const requester = element("p", "approval-requester", request.requester);
  const returnToList = button("approval-back", "К очереди", () => queueButton(request.id)?.focus());
  header.append(appointment, title);
  if (!request.bundle) header.append(element("p", "approval-location", request.location));
  header.append(requester, returnToList);
  if (request.routing) {
    const route = element("dl", "approval-routing");
    const details = [["Цель", request.routing.purpose], ["Участники", request.routing.people],
      ["Оснащение", request.routing.equipment.join(", ")], ["Согласующий", request.reviewer]];
    for (const [label, value] of details) {
      if (!value) continue;
      const row = element("div", "");
      row.append(element("dt", "", label), element("dd", "", value));
      route.append(row);
    }
    header.append(route);
  }
  for (const resource of request.bundle ?? []) {
    const row = element("section", "selection-resource");
    const label = element("div", "selection-resource-label");
    label.append(symbol("layers"), element("strong", "", resource.resourceName), element("p", "", resource.location));
    row.append(label);
    if (resource.answers.length) row.append(renderBookingAnswers(resource.answers));
    header.append(row);
  }
  if (request.answers?.length) {
    header.append(element("h3", "approval-answers-title", "Ответы гостя"), renderBookingAnswers(request.answers));
  }
  return header;
}

function approvalForm(request) {
  const form = element("form", "workspace-form approval-form");
  const label = element("label", "", "Причина решения");
  const reason = element("textarea", "");
  Object.assign(reason, { name: "reason", required: true, maxLength: 240, rows: 3,
    value: approvalDrafts.get(request.id) ?? "" });
  reason.setAttribute("aria-describedby", "approval-reason-hint");
  label.append(reason);
  const hint = element("div", "approval-form-hint");
  const explanation = element("span", "", "Участник увидит причину в «Моих записях».");
  explanation.id = "approval-reason-hint";
  const counter = element("span", "approval-counter", `${reason.value.length} / ${reason.maxLength}`);
  reason.addEventListener("input", () => {
    approvalDrafts.set(request.id, reason.value);
    counter.textContent = `${reason.value.length} / ${reason.maxLength}`;
  });
  hint.append(explanation, counter);

  const actions = element("div", "decision-actions");
  for (const [status, text, style] of [
    ["rejected", "Отклонить", "outline-action"], ["approved", "Одобрить", "page-action"]
  ]) {
    const control = element("button", style, text);
    Object.assign(control, { type: "submit", name: "decision", value: status });
    actions.append(control);
  }
  form.append(label, hint, actions);
  form.addEventListener("submit", event => {
    event.preventDefault();
    if (!event.submitter) return;
    const status = event.submitter.value;
    run(async () => {
      approvalMessage("");
      try {
        await api(`/api/approvals/${request.id}/decision`, jsonRequest("POST", { status, reason: reason.value.trim() }));
        approvalMessage(status === "approved" ? "Заявка одобрена." : "Заявка отклонена.");
        await loadWorkspace();
        section("approval-message").focus();
      } catch (error) {
        approvalMessage(error.message, true);
        if (error.status === 409 || error.status === 404) await loadWorkspace();
      }
    });
  });
  return form;
}
