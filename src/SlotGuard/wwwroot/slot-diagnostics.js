function openSlotDiagnostics({ resourceName, duration, timeZone, resourceZone, firstDay, lastDay,
  date, time, startsAt, check, choose }) {
  if (document.querySelector(".slot-diagnostics")) return;
  const dialog = document.createElement("dialog");
  dialog.className = "panel login-dialog slot-diagnostics";
  dialog.setAttribute("aria-labelledby", "slot-diagnostics-title");
  dialog.innerHTML = `
    <div class="slot-diagnostics-heading">
      <div><h2 id="slot-diagnostics-title">Почему недоступно?</h2><p data-context></p></div>
      <button type="button" class="icon-button today-button" data-close aria-label="Закрыть">×</button>
    </div>
    <form class="workspace-form slot-check-form">
      <div class="slot-check-fields">
        <label>Дата<input type="date" name="date" required></label>
        <label>Время<input type="time" name="time" required></label>
      </div>
      <button class="outline-action" type="submit">Проверить</button>
    </form>
    <p class="slot-check-zone" data-zone></p>
    <p class="form-message" role="status" data-status hidden></p>
    <div class="slot-check-reasons" data-reasons aria-live="polite" hidden></div>
    <div class="slot-check-choices" data-choices hidden>
      <h3>Ближайшие свободные слоты</h3>
      <ul data-alternatives></ul>
    </div>
  `;

  const node = name => dialog.querySelector(`[data-${name}]`);
  const form = dialog.querySelector("form");
  const dateField = form.elements.date;
  const timeField = form.elements.time;
  const lifetime = new AbortController();
  let exact = startsAt;
  let working = false;
  let checked = false;
  dateField.min = firstDay;
  dateField.max = lastDay;
  dateField.value = date;
  timeField.value = time;
  node("context").textContent = [resourceName, `${duration} мин.`].filter(Boolean).join(" · ");
  node("zone").textContent = `Ваше время · ${timeZoneLabel(timeZone)}`
    + (resourceZone !== timeZone ? ` · график ресурса в зоне ${timeZoneLabel(resourceZone)}` : "");

  function message(text, isError = false) {
    const status = node("status");
    status.textContent = text;
    status.hidden = !text;
    status.classList.toggle("error", isError);
  }

  function setWorking(value) {
    working = value;
    for (const input of form.elements) input.disabled = value;
    for (const button of dialog.querySelectorAll("[data-start]")) button.disabled = value;
  }

  function choice(start, content, className = "outline-action slot-alternative") {
    const control = document.createElement("button");
    control.type = "button";
    control.className = className;
    control.dataset.start = start;
    control.textContent = content;
    control.addEventListener("click", async () => {
      if (working || !checked) return;
      setWorking(true);
      try {
        if (await choose(start, lifetime.signal)) {
          dialog.close();
        } else if (dialog.open) {
          await refresh(true);
        }
      } catch (error) {
        if (dialog.open) message(error.message, true);
      } finally {
        setWorking(false);
      }
    });
    return control;
  }

  function render(view) {
    const reasons = node("reasons");
    reasons.replaceChildren(...view.reasons.map(reason => {
      const card = document.createElement("div");
      card.className = "slot-diagnosis";
      card.dataset.code = reason.code;
      const content = document.createElement("strong");
      content.textContent = reason.message;
      if (view.reasons.length > 1 && reason.startsAt) {
        const offset = document.createElement("span");
        offset.className = "slot-diagnosis-offset";
        offset.textContent = timeWithOffset(reason.startsAt, timeZone);
        card.append(offset);
      }
      card.append(content);
      if (reason.detail) {
        const detail = document.createElement("p");
        detail.textContent = reason.detail;
        card.append(detail);
      }
      if (reason.code === "available")
        card.append(choice(reason.startsAt, "Выбрать это время", "page-action"));
      return card;
    }));
    if (view.bookingMessage && !view.reasons.some(reason => reason.code === "active-limit")) {
      const note = document.createElement("p");
      note.className = "slot-check-zone";
      note.textContent = view.bookingMessage;
      reasons.append(note);
    }
    reasons.hidden = false;

    const slots = node("alternatives");
    slots.replaceChildren(...view.alternatives.map(slot => {
      const item = document.createElement("li");
      item.append(choice(slot.startsAt, appointmentTime(slot.startsAt, timeZone, true, slot.endsAt)));
      return item;
    }));
    node("choices").hidden = !view.alternatives.length;
    return !view.alternatives.length && !view.bookingMessage && view.reasons.every(reason => reason.code !== "available");
  }

  async function refresh(updated = false) {
    const parameters = exact ? { startsAt: exact } : { date: dateField.value, time: timeField.value };
    setWorking(true);
    checked = false;
    node("reasons").hidden = true;
    node("choices").hidden = true;
    form.setAttribute("aria-busy", "true");
    message("Проверяем доступность…");
    try {
      const view = await check(parameters, lifetime.signal);
      if (!dialog.open) return;
      const empty = render(view);
      checked = true;
      reveal(node("choices").hidden ? node("reasons") : node("choices"), 3);
      message([
        updated && "Выбранный вариант уже недоступен. Список обновлён.",
        empty && "В пределах окна бронирования свободных вариантов нет."
      ].filter(Boolean).join(" "), updated);
    } catch (error) {
      if (dialog.open) message(error.message, true);
    } finally {
      setWorking(false);
      form.setAttribute("aria-busy", "false");
    }
  }

  form.addEventListener("input", () => {
    exact = null;
    checked = false;
    node("reasons").hidden = true;
    node("choices").hidden = true;
    message("");
  });
  form.addEventListener("submit", event => {
    event.preventDefault();
    if (!working) refresh();
  });
  node("close").addEventListener("click", () => dialog.close());
  dialog.addEventListener("close", () => {
    lifetime.abort();
    dialog.remove();
  }, { once: true });
  document.body.append(dialog);
  dialog.showModal();
  refresh();
}
