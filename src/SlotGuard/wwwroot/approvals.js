let approvalQueue = [];

function approvalMessage(message, error = false) {
  const node = section("approval-message");
  node.textContent = message;
  node.classList.toggle("error", error);
  node.hidden = !message;
}

function resetApprovals() {
  approvalQueue = [];
  section("approval-list").replaceChildren();
  section("approval-count").hidden = true;
  approvalMessage("");
}

function renderApprovalControls() {
  for (const control of section("approval-list").querySelectorAll("button, textarea"))
    control.disabled = state.busy;
}

function renderApprovals(requests) {
  const list = section("approval-list");
  if (list.childElementCount && JSON.stringify(requests) === JSON.stringify(approvalQueue)) return;

  const focused = document.activeElement;
  const focusedId = focused?.closest("[data-approval-id]")?.dataset.approvalId;
  const drafts = new Map([...list.querySelectorAll("[data-approval-id]")].map(card =>
    [card.dataset.approvalId, card.querySelector("textarea").value]));
  const cards = requests.map(request => approvalCard(request, drafts.get(request.id) ?? ""));
  if (cards.length) list.replaceChildren(...cards);
  else list.replaceChildren(element("p", "panel approval-empty", "Заявок на согласование пока нет."));

  approvalQueue = requests;
  const count = section("approval-count");
  count.textContent = requests.length;
  count.hidden = requests.length === 0;
  renderApprovalControls();
  if (focusedId) {
    const target = cards.find(card => card.dataset.approvalId === focusedId);
    const replacement = [...(target?.querySelectorAll("textarea, button") ?? [])]
      .find(control => control.name === focused.name && control.value === focused.value);
    replacement?.focus({ preventScroll: true });
  }
}

function approvalCard(request, draft) {
  const card = element("article", "panel approval-card");
  card.dataset.approvalId = request.id;
  const summary = element("div", "approval-summary");
  const header = element("div", "available-heading");
  const icon = symbol("event_available");
  icon.classList.add("available-icon");
  const identity = element("div", "available-copy");
  identity.append(element("h3", "", request.resourceName), element("p", "", request.location));
  header.append(icon, identity);
  const date = element("time", "", appointmentTime(request.startsAt));
  date.dateTime = request.startsAt;
  summary.append(header, date, element("p", "", request.requester));

  const form = element("form", "workspace-form approval-form");
  const label = element("label", "", "Причина решения");
  const reason = element("textarea", "");
  Object.assign(reason, { name: "reason", required: true, maxLength: 240, rows: 2, value: draft,
    placeholder: "Участник увидит этот текст в «Моих записях»" });
  label.append(reason);
  const actions = element("div", "decision-actions");
  for (const [status, text, style] of [
    ["approved", "Одобрить", "page-action"], ["rejected", "Отклонить", "outline-action"]
  ]) {
    const control = element("button", style, text);
    Object.assign(control, { type: "submit", name: "decision", value: status });
    actions.append(control);
  }
  form.append(label, actions);
  form.addEventListener("submit", event => {
    event.preventDefault();
    const status = event.submitter?.value;
    if (!status) return;
    run(async () => {
      approvalMessage("");
      try {
        await api(`/api/approvals/${request.id}/decision`, jsonRequest("POST", { status, reason: reason.value.trim() }));
        approvalMessage(status === "approved" ? "Заявка одобрена." : "Заявка отклонена.");
        await loadWorkspace();
      } catch (error) {
        approvalMessage(error.message, true);
        if (error.status === 409 || error.status === 404) await loadWorkspace();
      }
    });
  });
  card.append(summary, form);
  return card;
}
