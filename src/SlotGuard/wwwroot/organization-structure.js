const structureAddress = "/api/structure";
let structureEdit = null;

function organizationRoleLabel(role) {
  return { admin: "Администратор", reviewer: "Согласующий", member: "Участник" }[role] ?? "Участник";
}

function resetOrganizationStructure() {
  const content = section("structure-content");
  content.replaceChildren();
  content.setAttribute("aria-busy", "true");
  section("structure-status").hidden = true;
  section("structure-dialog").close();
}

function structureCard(title, icon, action) {
  const card = element("section", "panel structure-card");
  const heading = element("header", "structure-card-head");
  const label = element("h2");
  label.append(symbol(icon), element("span", "", title));
  heading.append(label);
  if (action) heading.append(action);
  const list = element("ul", "structure-list");
  card.append(heading, list);
  return { card, list };
}

function structureButton(text, callback, className = "outline-action") {
  const button = element("button", className, text);
  button.type = "button";
  button.addEventListener("click", callback);
  return button;
}

function structureRow(name, details, action) {
  const row = element("li", "structure-row");
  const info = element("div", "structure-row-copy");
  info.append(element("strong", "", name));
  if (details) info.append(element("span", "", details));
  row.append(info, action);
  return row;
}

function structureOptions(data, field, initial, options, disabled = false) {
  const label = element("label", "", data);
  const select = element("select");
  select.name = field;
  select.disabled = disabled;
  for (const [id, title] of options) {
    const option = element("option", "", title);
    option.value = id;
    select.append(option);
  }
  select.value = initial ?? "";
  label.append(select);
  return label;
}

function structureName(value) {
  const label = element("label", "", "Название");
  const input = element("input");
  input.name = "name";
  input.value = value ?? "";
  input.required = true;
  input.maxLength = 100;
  label.append(input);
  return label;
}

function editStructure(title, path, fields, payload) {
  structureEdit = { path, payload, organization: state.organizationId };
  section("structure-editor-title").textContent = title;
  section("structure-fields").replaceChildren(...fields);
  section("structure-error").hidden = true;
  section("structure-dialog").showModal();
}

function renderOrganizationStructure({ units, groups, resources, members }) {
  const content = section("structure-content");
  const branches = units.filter(unit => !unit.branchId);
  const departments = unit => units.filter(item => item.branchId === unit.id);
  const byUnit = new Map(units.map(unit => [unit.id, unit]));
  const byMember = new Map(members.map(member => [member.id, member]));
  const byGroup = new Map(groups.map(group => [group.id, group]));
  const unitName = id => {
    const unit = byUnit.get(id);
    return unit?.branchId ? `${byUnit.get(unit.branchId)?.name} / ${unit.name}` : unit?.name;
  };
  const unitOptions = branches.flatMap(unit => [[unit.id, unit.name],
    ...departments(unit).map(department => [department.id, unitName(department.id)])]);

  function editUnit(unit, branchId) {
    const name = branchId ? "Отдел" : "Филиал";
    editStructure(unit ? `Переименовать ${name.toLowerCase()}` : `Новый ${name.toLowerCase()}`,
      `units/${unit?.id ?? crypto.randomUUID()}`, [structureName(unit?.name)],
      form => ({ name: form.elements.name.value, branchId }));
  }

  function editGroup(group) {
    const reviewers = members.filter(member => member.role === "reviewer").map(member => [member.id, member.name]);
    editStructure(group ? "Настройки группы" : "Новая группа", `groups/${group?.id ?? crypto.randomUUID()}`,
      [
        structureName(group?.name),
        structureOptions("Кому доступна группа", "unitId", group?.unitId, [["", "Всей организации"], ...unitOptions]),
        element("p", "structure-note", "Ограничение по подразделению закрывает ссылки и виджет. Филиал включает свои отделы."),
        structureOptions("Согласующий", "reviewerId", group?.reviewerId, [["", "Только администраторы"], ...reviewers]),
        element("p", "structure-note", "Группа определяет, кто принимает решение. Включите согласование в настройках нужных ресурсов.")
      ], form => ({ name: form.elements.name.value, unitId: form.elements.unitId.value || null,
        reviewerId: form.elements.reviewerId.value || null }));
  }

  function editMember(member) {
    const admin = member.role === "admin";
    const roles = admin ? [["admin", "Администратор"]] : [["member", "Участник"], ["reviewer", "Согласующий"]];
    editStructure(member.name, `members/${member.id}`, [
      structureOptions("Подразделение", "unitId", member.unitId, [["", "Без назначения"], ...unitOptions]),
      structureOptions("Роль", "role", member.role, roles, admin)
    ], form => ({ unitId: form.elements.unitId.value || null, role: form.elements.role.value }));
  }

  function editResource(resource) {
    editStructure(resource.name, `resources/${encodeURIComponent(resource.id)}`, [
      structureOptions("Группа ресурсов", "groupId", resource.groupId,
        [["", "Без группы"], ...groups.map(group => [group.id, group.name])])
    ], form => ({ groupId: form.elements.groupId.value || null }));
  }

  const hierarchy = structureCard("Филиалы и отделы", "grid_view", structureButton("Добавить филиал", () => editUnit(null, null)));
  for (const branch of branches) {
    const branchRow = structureRow(branch.name, "Филиал", structureButton("Переименовать", () => editUnit(branch, null)));
    const children = element("ul", "structure-departments");
    for (const department of departments(branch))
      children.append(structureRow(department.name, null, structureButton("Переименовать", () => editUnit(department, branch.id))));
    const add = element("li");
    add.append(structureButton("Добавить отдел", () => editUnit(null, branch.id), "text-link"));
    children.append(add);
    branchRow.append(children);
    hierarchy.list.append(branchRow);
  }
  if (!branches.length)
    hierarchy.list.append(element("li", "structure-empty", "Создайте филиал, чтобы добавить в него отделы."));

  const access = structureCard("Группы ресурсов", "settings", structureButton("Добавить группу", () => editGroup(null)));
  for (const group of groups) {
    const groupScope = unitName(group.unitId) ?? "Вся организация";
    const reviewer = byMember.get(group.reviewerId)?.name ?? "Администраторы";
    access.list.append(structureRow(group.name, `${groupScope} · ${reviewer}`,
      structureButton("Настроить", () => editGroup(group))));
  }
  if (!groups.length)
    access.list.append(element("li", "structure-empty", "Объедините ресурсы с одинаковыми правилами доступа."));

  const roster = structureCard("Участники и роли", "group");
  for (const member of members) {
    const details = [organizationRoleLabel(member.role), unitName(member.unitId), member.email].filter(Boolean).join(" · ");
    roster.list.append(structureRow(member.name, details, structureButton("Изменить", () => editMember(member))));
  }
  const catalog = structureCard("Распределение ресурсов", "meeting_room");
  for (const resource of resources) {
    const details = [byGroup.get(resource.groupId)?.name ?? "Без группы", resource.isArchived && "В архиве"]
      .filter(Boolean).join(" · ");
    catalog.list.append(structureRow(resource.name, details, structureButton("Выбрать группу", () => editResource(resource))));
  }
  if (!resources.length) catalog.list.append(element("li", "structure-empty", "Добавьте ресурсы в разделе «Управление»."));

  content.replaceChildren(hierarchy.card, access.card, roster.card, catalog.card);
  content.setAttribute("aria-busy", "false");
}

section("structure-cancel").addEventListener("click", () => section("structure-dialog").close());
section("structure-dialog").addEventListener("close", () => { structureEdit = null; });
section("structure-form").addEventListener("submit", async event => {
  event.preventDefault();
  const edit = structureEdit;
  if (!edit) return;
  const form = event.currentTarget;
  const save = form.querySelector('[type="submit"]');
  const errorMessage = section("structure-error");
  save.disabled = true;
  errorMessage.hidden = true;
  try {
    await api(`${structureAddress}/${edit.path}`, jsonRequest("PUT", edit.payload(form)), edit.organization);
    if (structureEdit !== edit) return;
    section("structure-dialog").close();
    const status = section("structure-status");
    status.textContent = "Изменения сохранены.";
    status.hidden = false;
    await loadWorkspace();
  } catch (error) {
    if (structureEdit === edit) {
      errorMessage.textContent = error.message;
      errorMessage.hidden = false;
    }
  } finally {
    save.disabled = false;
  }
});
