function openWeeklySeries({ startsAt, durationMinutes, email, bookingForm, answers, onQuestionsChanged, prepare, send, onBooked, maxCount = 8 }) {
  if (document.querySelector(".weekly-dialog")) return;
  const dialog = document.createElement("dialog");
  dialog.className = "panel login-dialog weekly-dialog";
  dialog.setAttribute("aria-labelledby", "weekly-title");
  dialog.innerHTML = `
    <form class="workspace-form weekly-form">
      <header>
        <h2 id="weekly-title">Еженедельная бронь</h2>
        <p data-resource></p>
      </header>
      <div class="weekly-settings">
        <label>Количество записей
          <select data-count>${Array.from({ length: Math.min(8, maxCount) - 1 }, (_, i) => `<option>${i + 2}</option>`).join("")}</select>
        </label>
        <span data-zone></span>
      </div>
      <ol data-dates class="weekly-dates" aria-label="Даты серии"></ol>
      <div class="booking-question-fields" data-questions hidden></div>
      <p data-status class="form-message" role="status"></p>
      <p class="weekly-approval" data-approval hidden>Каждую дату согласует администратор.</p>
      <footer class="weekly-actions">
        <button type="button" data-close class="outline-action">Закрыть</button>
        <button type="button" data-retry class="outline-action" hidden>Проверить снова</button>
        <button type="submit" data-book class="page-action" disabled>Подтвердить серию</button>
      </footer>
    </form>`;
  const get = name => dialog.querySelector(`[data-${name}]`);
  const count = get("count");
  const dates = get("dates");
  const book = get("book");
  const retry = get("retry");
  const fields = bookingFields(get("questions"), bookingForm, answers);
  let seriesId;
  let replacements = {};
  let busy = false;
  let saving = false;
  let saved = false;
  let prepared = false;
  let preview = null;
  count.value = String(Math.min(4, maxCount));

  function message(text, error = false) {
    const status = get("status");
    status.textContent = text;
    status.classList.toggle("error", error);
  }

  function sync() {
    count.disabled = busy;
    get("close").disabled = saving;
    retry.disabled = busy;
    fields.disable(busy);
    for (const control of dates.querySelectorAll("select, button")) control.disabled = busy;
    book.disabled = busy || !preview || Boolean(preview.limitMessage) || preview.occurrences.some(row => row.conflict);
  }

  function render(view) {
    preview = view;
    seriesId = view.seriesId;
    get("resource").textContent = view.resourceName;
    get("zone").textContent = view.timeZone + " · " + durationMinutes + " мин.";
    get("approval").hidden = !view.requiresApproval;
    book.textContent = view.requiresApproval ? "Отправить на согласование" : "Подтвердить серию";
    const formats = zoneFormats(view.timeZone);
    const dateOnly = new Intl.DateTimeFormat("ru-RU", {
      weekday: "short", day: "numeric", month: "long", timeZone: "UTC"
    });
    const dateTime = start => `${formats.longDate.format(new Date(start))} · ${timeWithOffset(start, view.timeZone)}`;
    dates.replaceChildren(...view.occurrences.map(row => {
      const item = document.createElement("li");
      item.className = "weekly-date";
      item.classList.toggle("weekly-date-conflict", Boolean(row.conflict));
      const title = document.createElement("strong");
      title.textContent = row.startsAt ? dateTime(row.startsAt) : dateOnly.format(new Date(row.date + "T00:00:00Z"));
      const status = document.createElement("span");
      status.className = "weekly-date-status";
      status.textContent = row.conflict || (replacements[row.index] ? "Выбрана замена" : "Свободно");
      item.append(title, status);

      if (row.alternatives.length) {
        const select = document.createElement("select");
        select.setAttribute("aria-label", `Замена для записи ${row.index + 1}`);
        select.append(new Option("Выберите другое время", ""), ...row.alternatives.map(start => new Option(dateTime(start), start)));
        select.addEventListener("change", () => {
          if (!select.value) return;
          replacements[row.index] = select.value;
          refresh();
        });
        item.append(select);
      } else if (replacements[row.index]) {
        const reset = document.createElement("button");
        reset.type = "button";
        reset.className = "outline-action weekly-reset";
        reset.textContent = "Вернуть исходное время";
        reset.addEventListener("click", () => {
          delete replacements[row.index];
          refresh();
        });
        item.append(reset);
      }
      return item;
    }));
    sync();
  }

  async function submit(suffix, formAnswers) {
    if (!prepared && prepare) {
      await prepare();
      prepared = true;
    }
    const seenStarts = suffix === "" ? preview.occurrences.map(row => row.startsAt) : undefined;
    const response = await send(suffix, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ startsAt, durationMinutes, count: Number(count.value), replacements, seriesId, email,
        seenStarts, bookingForm: formAnswers })
    });
    const data = await response.json().catch(() => null);
    if (response.ok || response.status === 409 && data?.occurrences) return data;
    const fallback = response.status === 429 ? "Слишком много запросов. Попробуйте через минуту." :
      response.status === 404 ? "Ресурс больше недоступен." : "Не удалось связаться с сервером.";
    throw Object.assign(new Error(data?.title || fallback), { bookingForm: data?.bookingForm, questionErrors: data?.questionErrors });
  }

  async function refresh() {
    if (busy) return;
    busy = true;
    preview = null;
    retry.hidden = true;
    message("Проверяем даты…");
    sync();
    try {
      const view = await submit("/preview");
      render(view);
      message(view.limitMessage || (view.occurrences.some(item => item.conflict) ? "Выберите замену для конфликтующих дат." : ""),
        Boolean(view.limitMessage));
    } catch (error) {
      retry.hidden = false;
      message(error.message, true);
    } finally {
      busy = false;
      sync();
    }
  }

  count.addEventListener("change", () => {
    replacements = {};
    seriesId = undefined;
    refresh();
  });
  retry.addEventListener("click", refresh);
  get("close").addEventListener("click", () => dialog.close());
  dialog.addEventListener("cancel", event => { if (saving) event.preventDefault(); });
  dialog.addEventListener("close", () => dialog.remove(), { once: true });
  dialog.querySelector("form").addEventListener("submit", async event => {
    event.preventDefault();
    if (book.disabled || saved) return;
    const answers = fields.read();
    if (answers === undefined) return;
    busy = true;
    saving = true;
    message("Сохраняем серию…");
    sync();
    let result;
    try {
      result = await submit("", answers);
      if (result.occurrences) {
        render(result);
        message(result.limitMessage || "Доступность изменилась. Проверьте обновлённые даты.", true);
        return;
      }
      saved = true;
    } catch (error) {
      if (error.bookingForm) {
        fields.setForm(error.bookingForm);
        fields.disable(false);
        fields.showErrors(error.questionErrors);
        onQuestionsChanged?.(error.bookingForm);
      }
      message(error.message, true);
    } finally {
      busy = false;
      saving = false;
      sync();
    }
    if (!saved) return;
    dialog.close();
    await onBooked(result);
  });
  document.body.append(dialog);
  dialog.showModal();
  refresh();
}
