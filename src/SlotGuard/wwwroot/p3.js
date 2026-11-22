const p3Event = section("p3-event");
const p3Assistant = section("p3-assistant");
let p3Sessions = [];
let p3Room = "";
let ticketCamera;

function resetP3() {
  p3Sessions = [];
  p3Room = "";
  stopTicketCamera();
  section("p3-answer").replaceChildren();
  for (const name of ["p3-answer-message", "p3-event-message", "p3-host-message", "p3-room-message"]) section(name).textContent = "";
  for (const name of ["p3-room-link", "p3-copy-room", "p3-event-panel", "p3-host-panel"]) section(name).hidden = true;
}

function editDiscoverListing(resource) {
  const org = state.organizationId;
  const dialog = questionDialog("Карточка в публичном каталоге", resource.name);
  const fields = dialog.querySelector("[data-fields]");
  function field(text, name, type, value) {
    const label = element("label", type === "checkbox" ? "checkbox-label" : "", text);
    const input = Object.assign(element("input"), { type, name, maxLength: 70 });
    if (type === "checkbox") { input.checked = value; label.prepend(input); }
    else { input.value = value; label.append(input); }
    return label;
  }
  fields.replaceChildren(field("Показывать в каталоге", "enabled", "checkbox", !!resource.discoverCity),
    field("Город", "city", "text", resource.discoverCity ?? ""),
    field("Рекомендовать площадку", "featured", "checkbox", resource.discoverFeatured));
  dialog.querySelector("form").addEventListener("submit", event => {
    event.preventDefault();
    const form = event.currentTarget.elements;
    const data = { enabled: form.enabled.checked, city: form.city.value, featured: form.featured.checked };
    run(async () => {
      try {
        await api(`/api/workspace/resources/${encodeURIComponent(resource.id)}/discover`, jsonRequest("PUT", data), org);
        dialog.close();
        if (state.organizationId === org) await loadWorkspace();
      } catch (error) { formProblem(dialog.querySelector("[data-message]"), error); }
    });
  });
  document.body.append(dialog);
  dialog.showModal();
}

async function p3Search(org, text, message) {
  try {
    const response = await api("/api/assistant/search", jsonRequest("POST", { text, timeZone: viewerZone }), org);
    if (org !== state.organizationId) return;
    const { query, results } = response;
    const notice = section("p3-answer-message");
    const found = [...results.exact, ...results.alternatives];
    notice.textContent = message || results.bookingMessage || (found.length
      ? [query.date, query.time, `${query.durationMinutes} мин`, query.capacity && `от ${query.capacity} человек`].filter(Boolean).join(" · ")
      : "Подходящих слотов нет.");
    section("p3-answer").replaceChildren(...found.map((slot, index) => {
      const row = element("li", "p3-result");
      row.append(element("strong", "", slot.resource.name),
        element("p", "p3-muted", `${appointmentTime(slot.startsAt, viewerZone, true, slot.endsAt)} · ${slot.resource.location}`));
      if (index >= results.exact.length) row.append(element("p", "p3-muted", "Ближайший вариант"));
      row.append(button("outline-action", slot.resource.requiresApproval ? "Подтвердить заявку" : "Подтвердить бронь", () => {
        if (org !== state.organizationId || !window.confirm(`Забронировать «${slot.resource.name}» на ${appointmentTime(slot.startsAt)}?`)) return;
        withBookingQuestions({
          resourceId: slot.resource.id, context: slot.resource.name, label: "Подтвердить",
          submit: bookingForm => api("/api/bookings", jsonRequest("POST", { resourceId: slot.resource.id,
            startsAt: slot.startsAt, durationMinutes: query.durationMinutes, bookingForm }), org),
          onBooked: result => p3Search(org, text, result.approvalStatus === "pending" ? "Заявка отправлена." : "Бронь подтверждена."),
          onError: error => p3Search(org, text, error.message)
        });
      }));
      return row;
    }));
  } catch (error) {
    if (state.organizationId === org) {
      section("p3-answer").replaceChildren();
      section("p3-answer-message").textContent = error.message;
    }
  }
}
p3Assistant.addEventListener("submit", event => {
  event.preventDefault();
  const text = p3Assistant.elements.text.value;
  const org = state.organizationId;
  run(() => p3Search(org, text));
});

section("p3-new-room").addEventListener("click", () => run(async () => {
  const { link } = await api("/api/meetings", { method: "POST" });
  p3Room = new URL(link, location.origin).href;
  const entry = section("p3-room-link");
  entry.href = p3Room;
  entry.hidden = false;
  section("p3-copy-room").hidden = false;
  section("p3-room-message").textContent = "Ссылка готова.";
}));
section("p3-copy-room").addEventListener("click", async () => {
  try { await navigator.clipboard.writeText(p3Room); section("p3-room-message").textContent = "Ссылка скопирована."; }
  catch { section("p3-room-message").textContent = p3Room; }
});

function dateField(value) {
  if (!value) return "";
  const instant = new Date(value);
  const format = zoneFormats(browserTimeZone);
  return `${format.day.format(instant)}T${format.time.format(instant)}`;
}
function selectP3Event() {
  const fields = p3Event.elements;
  const session = p3Sessions.find(item => item.id === fields.session.value);
  const published = session?.publication;
  fields.enabled.checked = !!published?.enabled;
  fields.city.value = published?.city ?? "";
  fields.description.value = published?.description ?? "";
  fields.opensAt.value = dateField(published?.opensAt);
  fields.closesAt.value = dateField(published?.closesAt);
  fields.coHostId.value = published?.coHostId ?? "";
  const link = section("p3-public-event");
  link.hidden = !published?.enabled;
  link.href = session ? `/events/${session.id}` : "/discover";
  section("p3-host-panel").hidden = !session;
}
function renderP3Events(model) {
  p3Sessions = model.sessions;
  const fields = p3Event.elements;
  const current = fields.session.value;
  fields.session.replaceChildren(...p3Sessions.map(item => new Option(
    `${item.sessionTitle} · ${appointmentTime(item.startUtc)}`, item.id)));
  if (p3Sessions.some(item => item.id === current)) fields.session.value = current;
  fields.coHostId.replaceChildren(new Option("Без соорганизатора", ""), ...model.members.map(item => new Option(item.name, item.id)));
  section("p3-event-panel").hidden = !model.canPublish && !p3Sessions.length;
  section("p3-events-empty").hidden = !!p3Sessions.length;
  p3Event.hidden = !p3Sessions.length;
  section("p3-event-fields").hidden = !model.canPublish;
  selectP3Event();
}
p3Event.elements.session.addEventListener("change", selectP3Event);
p3Event.addEventListener("submit", event => {
  event.preventDefault();
  const fields = p3Event.elements;
  const data = { enabled: fields.enabled.checked, city: fields.city.value, description: fields.description.value,
    opensAt: fields.opensAt.value ? new Date(fields.opensAt.value).toISOString() : null,
    closesAt: fields.closesAt.value ? new Date(fields.closesAt.value).toISOString() : null, coHostId: fields.coHostId.value || null };
  run(async () => {
    try {
      await api(`/api/events/${fields.session.value}`, jsonRequest("PUT", data));
      await loadWorkspace();
      section("p3-event-message").textContent = "Публикация сохранена.";
    } catch (error) { section("p3-event-message").textContent = error.message; }
  });
});

for (const [id, route, name] of [["p3-ticket", "check-in", "ticket"], ["p3-announcement", "announce", "message"]]) {
  section(id).addEventListener("submit", event => {
    event.preventDefault();
    const form = event.currentTarget;
    const data = { [name]: form.elements[name].value.trim() };
    const session = p3Event.elements.session.value;
    run(async () => {
      const status = section("p3-host-message");
      try {
        const receipt = await api(`/api/events/${session}/${route}`, jsonRequest("POST", data));
        status.textContent = route === "check-in" ? "Приход отмечен." : `Писем в очереди: ${receipt.count}.`;
        form.reset();
      } catch (error) { status.textContent = error.message; }
    });
  });
}

function stopTicketCamera() {
  ticketCamera?.getTracks().forEach(track => track.stop());
  ticketCamera = null;
  const video = section("event-ticket-camera");
  video.srcObject = null;
  video.hidden = true;
  section("p3-scan").textContent = "Сканировать QR";
}
section("p3-scan").hidden = !("BarcodeDetector" in window) || !navigator.mediaDevices?.getUserMedia;
section("p3-scan").addEventListener("click", async () => {
  if (ticketCamera) return stopTicketCamera();
  try {
    const detector = new BarcodeDetector({ formats: ["qr_code"] });
    ticketCamera = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: "environment" } } });
    const camera = ticketCamera;
    const video = section("event-ticket-camera");
    video.srcObject = camera;
    video.hidden = false;
    section("p3-scan").textContent = "Закрыть камеру";
    const started = Date.now();
    while (ticketCamera === camera && currentScreen() === "p3" && Date.now() - started < 30000) {
      if (video.readyState >= 2) {
        const value = (await detector.detect(video)).map(item => item.rawValue)
          .find(code => /^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/i.test(code));
        if (value) { section("p3-ticket").elements.ticket.value = value; break; }
      }
      await new Promise(resolve => setTimeout(resolve, 250));
    }
  } catch { section("p3-host-message").textContent = "Камера недоступна. Введите код билета вручную."; }
  finally { stopTicketCamera(); }
});
