function openBookingReschedule({ booking, zone, load, save, onMoved }) {
  if (document.querySelector(".reschedule-dialog")) return;
  const dialog = document.createElement("dialog");
  dialog.className = "panel login-dialog reschedule-dialog";
  dialog.setAttribute("aria-labelledby", "reschedule-heading");
  dialog.innerHTML = `
    <form class="workspace-form reschedule-form">
      <header><h2 id="reschedule-heading">Перенести бронь</h2><p data-resource></p></header>
      <div class="reschedule-current"><span>Сейчас</span><strong data-current></strong></div>
      <label>Новая дата<select aria-label="Новая дата" data-day disabled></select></label>
      <div class="reschedule-times" role="group" aria-label="Новое время" data-starts></div>
      <p class="reschedule-safety">Исходная бронь действует до успешного переноса.</p>
      <p class="reschedule-approval" data-approval hidden>Новое время будет отправлено на согласование.</p>
      <p class="form-message" role="status" data-message></p>
      <footer class="reschedule-actions">
        <button class="outline-action" type="button" data-cancel>Отмена</button>
        <button class="page-action" type="submit" data-submit disabled>Подтвердить перенос</button>
      </footer>
    </form>`;

  const node = name => dialog.querySelector(`[data-${name}]`);
  const dayField = node("day");
  const times = node("starts");
  const submit = node("submit");
  const message = node("message");
  const cancel = node("cancel");
  const formats = zoneFormats(zone);
  const days = new Map();
  const minutes = durationOf(booking.startsAt, booking.endsAt);
  let selected = null;
  let loading = false;
  let saving = false;

  node("resource").textContent = booking.resourceName;
  node("current").textContent = `${formats.longDate.format(new Date(booking.startsAt))} · ${slotTimeRange(booking.startsAt, zone, booking.endsAt)}`;

  function syncControls() {
    const busy = loading || saving;
    dayField.disabled = busy || !days.size;
    for (const choice of times.querySelectorAll("button")) choice.disabled = busy;
    cancel.disabled = saving;
    submit.disabled = busy || !selected;
  }

  function renderTimes() {
    const options = days.get(dayField.value) ?? [];
    const labels = options.map(start => formats.time.format(new Date(start)));
    times.replaceChildren(...options.map((start, index) => {
      const button = document.createElement("button");
      const offset = utcOffset(start, zone);
      const label = labels[index];
      const repeated = labels.indexOf(label) !== labels.lastIndexOf(label);
      button.type = "button";
      button.className = "outline-action reschedule-time";
      button.textContent = repeated ? `${label} ${offset}` : label;
      button.setAttribute("aria-pressed", String(selected === start));
      button.addEventListener("click", () => {
        selected = start;
        for (const other of times.querySelectorAll("button"))
          other.setAttribute("aria-pressed", String(other === button));
        const end = new Date(Date.parse(start) + minutes * 60000);
        showMessage(`Новое время · ${slotTimeRange(start, zone, end)}`);
        syncControls();
      });
      return button;
    }));
    syncControls();
  }

  function showMessage(text, error = false) {
    message.textContent = text;
    message.classList.toggle("error", error);
    message.hidden = !text;
  }

  async function refresh(preferredDay, notice) {
    loading = true;
    selected = null;
    syncControls();
    if (!notice) showMessage("Ищем свободное время…");
    try {
      const view = await load();
      if (!dialog.open) return;
      node("approval").hidden = !view.requiresApproval;
      days.clear();
      for (const start of view.starts) {
        const date = formats.day.format(new Date(start));
        const list = days.get(date) ?? [];
        list.push(start);
        days.set(date, list);
      }
      dayField.replaceChildren(...[...days].map(([date, starts]) =>
        new Option(formats.longDate.format(new Date(starts[0])), date)));
      if (days.has(preferredDay)) dayField.value = preferredDay;
      renderTimes();
      if (notice) showMessage(notice, true);
      else showMessage(days.size ? "" : "Свободного времени для этой длительности пока нет.");
    } catch (error) {
      if (dialog.open) {
        days.clear();
        dayField.replaceChildren();
        times.replaceChildren();
        showMessage(error.message, true);
      }
    } finally {
      loading = false;
      if (dialog.open) syncControls();
    }
  }

  dayField.addEventListener("change", () => {
    selected = null;
    if (!message.classList.contains("error")) showMessage("");
    renderTimes();
  });
  cancel.addEventListener("click", () => dialog.close());
  dialog.addEventListener("cancel", event => { if (saving) event.preventDefault(); });
  dialog.addEventListener("close", () => dialog.remove(), { once: true });
  dialog.querySelector("form").addEventListener("submit", async event => {
    event.preventDefault();
    if (!selected || saving || loading) return;
    saving = true;
    syncControls();
    let moved;
    try {
      moved = await save(selected);
    } catch (error) {
      showMessage(error.message, true);
      if ([400, 409, 422].includes(error.status))
        await refresh(dayField.value, error.message);
    } finally {
      saving = false;
      syncControls();
    }
    if (!moved) return;
    dialog.close();
    await onMoved(moved);
  });

  document.body.append(dialog);
  dialog.showModal();
  refresh(formats.day.format(new Date(booking.startsAt)));
}
