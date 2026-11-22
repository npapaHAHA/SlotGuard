function bookingFields(container, initial, initialAnswers = {}) {
  let current;
  let fields = new Map();
  function setForm(form) {
    if (current?.version === form?.version && current) return;
    const previous = fields;
    current = form;
    fields = new Map();
    container.replaceChildren();
    container.hidden = !form?.questions.length;
    if (container.hidden) return;
    const heading = document.createElement("strong");
    heading.textContent = "Вопросы к брони";
    container.append(heading);
    for (const question of form.questions) {
      const label = document.createElement("label");
      const title = document.createElement("span");
      title.textContent = question.label;
      const note = document.createElement("small");
      note.textContent = question.required ? "Обязательно" : "Необязательно";
      const input = document.createElement("textarea");
      Object.assign(input, { required: question.required, rows: 2, maxLength: 1000 });
      input.setAttribute("aria-label", question.label);
      const earlier = previous.get(question.id);
      input.value = earlier ? (earlier.label === question.label ? earlier.input.value : "")
        : initialAnswers[question.id] ?? "";
      input.addEventListener("input", () => input.setCustomValidity(""));
      label.append(title, note, input);
      fields.set(question.id, { label: question.label, input });
      container.append(label);
    }
    initialAnswers = {};
  }

  function read(validate = true) {
    if (!fields.size) return null;
    const answers = {};
    for (const [id, { input }] of fields) {
      const value = input.value.trim();
      if (input.required && !value && validate) input.setCustomValidity("Ответьте на обязательный вопрос.");
      if (validate && !input.reportValidity()) return undefined;
      if (value) answers[id] = value;
    }
    return { version: current.version, answers };
  }

  function showErrors(errors) {
    for (const [id, message] of Object.entries(errors ?? {}))
      fields.get(id)?.input.setCustomValidity(message);
    for (const { input } of fields.values())
      if (!input.reportValidity()) break;
  }

  setForm(initial);
  return {
    setForm, read, showErrors,
    visible(value) { container.hidden = !value || !fields.size; },
    disable(value) { for (const { input } of fields.values()) input.disabled = value; },
    clear() {
      for (const { input } of fields.values()) {
        input.value = "";
        input.setCustomValidity("");
      }
    }
  };
}

function renderBookingAnswers(answers) {
  const list = document.createElement("dl");
  list.className = "booking-answers";
  for (const answer of answers) {
    const row = document.createElement("div");
    const question = document.createElement("dt");
    question.textContent = answer.label;
    const response = document.createElement("dd");
    response.textContent = answer.value;
    row.append(question, response);
    list.append(row);
  }
  return list;
}

function questionDialog(title, context) {
  const dialog = document.createElement("dialog");
  dialog.className = "panel login-dialog booking-question-dialog";
  dialog.setAttribute("aria-label", title);
  dialog.innerHTML = `
    <header><h2></h2><p></p></header>
    <form class="workspace-form">
      <div data-fields></div><p class="form-message error" role="status" data-message hidden></p>
      <footer class="booking-question-actions">
        <button type="button" class="outline-action" data-close>Закрыть</button>
        <button type="submit" class="page-action" data-save>Сохранить</button>
      </footer>
    </form>`;
  dialog.querySelector("h2").textContent = title;
  dialog.querySelector("header p").textContent = context ?? "";
  dialog.querySelector("[data-close]").addEventListener("click", () => dialog.close());
  dialog.addEventListener("close", () => dialog.remove(), { once: true });
  return dialog;
}

function openBookingQuestionDialog({ form, context, label, submit, onBooked, onError }) {
  const dialog = questionDialog("Вопросы к брони", context);
  const wrapper = dialog.querySelector("[data-fields]");
  wrapper.className = "booking-question-fields";
  const fields = bookingFields(wrapper, form);
  const status = dialog.querySelector("[data-message]");
  const action = dialog.querySelector("[data-save]");
  const close = dialog.querySelector("[data-close]");
  let busy = false;
  action.textContent = label;
  dialog.addEventListener("cancel", event => { if (busy) event.preventDefault(); });
  dialog.querySelector("form").addEventListener("submit", async event => {
    event.preventDefault();
    if (busy) return;
    const payload = fields.read();
    if (payload === undefined) return;
    busy = action.disabled = close.disabled = true;
    fields.disable(true);
    status.hidden = true;
    let result;
    try {
      result = await submit(payload);
    } catch (error) {
      if (error.bookingForm) {
        fields.setForm(error.bookingForm);
        fields.disable(false);
        fields.showErrors(error.questionErrors);
        status.textContent = error.message;
        status.hidden = false;
      } else if (error instanceof TypeError || error.status >= 500) {
        status.textContent = "Не удалось связаться с сервером. Повторите попытку.";
        status.hidden = false;
      } else {
        dialog.close();
        await onError(error);
      }
      return;
    } finally {
      busy = action.disabled = close.disabled = false;
      fields.disable(false);
    }
    dialog.close();
    await onBooked(result);
  });
  document.body.append(dialog);
  dialog.showModal();
}

function openBookingFormEditor(resource, model, save, onSaved) {
  const dialog = questionDialog("Вопросы к брони", resource.name);
  const form = dialog.querySelector("form");
  const fields = dialog.querySelector("[data-fields]");
  const message = dialog.querySelector("[data-message]");
  const action = dialog.querySelector("[data-save]");
  const close = dialog.querySelector("[data-close]");
  fields.className = "question-editor-fields";
  const empty = document.createElement("p");
  empty.className = "question-editor-empty";
  empty.textContent = "Добавьте вопрос, который увидят при бронировании этого ресурса.";
  const add = document.createElement("button");
  add.type = "button";
  add.className = "outline-action";
  add.textContent = "+ Добавить вопрос";
  fields.after(add);
  fields.before(empty);
  let busy = false;

  function sync() {
    empty.hidden = fields.children.length > 0;
    add.disabled = busy || fields.children.length >= 8;
  }

  function addRow(question = {}) {
    const row = document.createElement("fieldset");
    const input = document.createElement("input");
    Object.assign(input, { required: true, maxLength: 160, placeholder: "Например, цель бронирования", value: question.label ?? "" });
    input.setAttribute("aria-label", "Текст вопроса");
    const meta = document.createElement("div");
    meta.className = "question-editor-controls";
    const label = document.createElement("label");
    label.className = "checkbox-label";
    const required = document.createElement("input");
    Object.assign(required, { type: "checkbox", checked: question.required ?? false });
    label.append(required, "Обязательный");
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "outline-action";
    remove.textContent = "Удалить";
    remove.addEventListener("click", () => { row.remove(); sync(); add.focus(); });
    meta.append(label, remove);
    row.dataset.questionId = question.id ?? "";
    row.append(input, meta);
    fields.append(row);
    sync();
    return input;
  }

  for (const question of model.questions) addRow(question);
  sync();
  add.addEventListener("click", () => { if (!add.disabled) addRow().focus(); });
  dialog.addEventListener("cancel", event => { if (busy) event.preventDefault(); });
  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (busy) return;
    const questions = [...fields.children].map(row => ({
      id: row.dataset.questionId || null,
      label: row.querySelector("input").value.trim(),
      required: row.querySelector("input[type=checkbox]").checked
    }));
    busy = action.disabled = close.disabled = true;
    for (const field of fields.children) field.disabled = true;
    sync();
    message.hidden = true;
    let result;
    try {
      result = await save({ version: model.version, questions });
    } catch (error) {
      message.textContent = error.message;
      message.hidden = false;
      return;
    } finally {
      busy = action.disabled = close.disabled = false;
      for (const field of fields.children) field.disabled = false;
      sync();
    }
    dialog.close();
    await onSaved(result);
  });
  document.body.append(dialog);
  dialog.showModal();
}
