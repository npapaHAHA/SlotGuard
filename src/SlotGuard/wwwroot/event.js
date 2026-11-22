"use strict";
const eventId = /^\/events\/([0-9a-f-]{36})\/?$/i.exec(location.pathname)?.[1];
const eventApi = `/api/public/events/${eventId}`;
const field = id => document.getElementById(id);
const form = field("event-form");
const answers = bookingFields(field("event-questions"));
const zone = savedTimeZone() || browserTimeZone;
let busy = false;

function showTicket(seat) {
  const canvas = field("event-qr");
  field("event-ticket-code").textContent = seat.id;
  field("event-ics").href = eventApi + "/calendar.ics";
  if (typeof window.qrcode !== "function") { canvas.hidden = true; return; }
  const qr = window.qrcode(0, "M");
  qr.addData(seat.id);
  qr.make();
  const context = canvas.getContext("2d");
  const count = qr.getModuleCount();
  const size = Math.floor(canvas.width / (count + 8));
  const margin = Math.floor((canvas.width - size * count) / 2);
  context.fillStyle = "#fff";
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.fillStyle = "#152920";
  for (let row = 0; row < count; row++)
    for (let column = 0; column < count; column++)
      if (qr.isDark(row, column)) context.fillRect(margin + column * size, margin + row * size, size, size);
}

function readable(date) {
  return `${zoneFormats(zone).longDate.format(new Date(date))} · ${timeWithOffset(date, zone)}`;
}

function renderEvent(event) {
  document.title = `${event.sessionTitle} · SlotGuard`;
  field("event-name").textContent = event.sessionTitle;
  field("event-date").textContent = readable(event.startUtc);
  field("event-date").hidden = false;
  field("event-description").textContent = event.description;
  field("event-description").hidden = !event.description;
  field("event-location").textContent = [event.city, event.location, event.organization].filter(Boolean).join(" · ");
  field("event-capacity").textContent = `Свободно ${event.remaining} из ${event.seatCapacity} мест`;
  field("event-registration").textContent = [
    event.opensAt && `Открытие · ${readable(event.opensAt)}`, event.closesAt && `Закрытие · ${readable(event.closesAt)}`
  ].filter(Boolean).join(" / ");
  answers.setForm(event.form);
  const active = ["waiting", "pending", "confirmed"].includes(event.mine?.status);
  const ticket = !event.cancelledAt && event.mine?.status === "confirmed";
  field("event-status").textContent = event.cancelledAt ? "Организатор отменил мероприятие."
    : ({ waiting: "Вы в листе ожидания.", pending: "Заявка ожидает решения организатора.", confirmed: "Место подтверждено.",
      rejected: event.mine?.decisionReason || "Заявка отклонена." }[event.mine?.status]
      ?? (event.open ? (!event.remaining ? "Места заняты. Можно встать в очередь." : "") : "Регистрация закрыта."));
  form.hidden = !event.open || active;
  field("event-join").textContent = event.remaining ? "Записаться" : "Встать в очередь";
  field("event-cancel").hidden = !active || !!event.cancelledAt || new Date(event.startUtc) <= new Date();
  field("event-ticket").hidden = !ticket;
  if (ticket) showTicket(event.mine);
  field("event-content").hidden = false;
}

async function eventRequest(path = "", options) {
  const response = await fetch(eventApi + path, { cache: "no-store", ...options });
  if (response.ok) return response.status === 204 ? null : response.json();
  const details = await response.json().catch(() => ({}));
  throw Object.assign(new Error(details.title ?? "Запрос не выполнен."), details);
}

async function loadEvent() {
  try { renderEvent(await eventRequest()); }
  catch { field("event-name").textContent = "Мероприятие недоступно"; }
}

async function changeEvent(action) {
  if (busy) return;
  busy = true;
  try { await action(); await loadEvent(); }
  catch (error) {
    if (error.bookingForm) answers.setForm(error.bookingForm);
    answers.showErrors(error.questionErrors);
    field("event-status").textContent = error.message;
  } finally { busy = false; }
}

form.addEventListener("submit", event => {
  event.preventDefault();
  const bookingForm = answers.read();
  if (bookingForm === undefined) return;
  const data = Object.fromEntries(new FormData(form));
  changeEvent(() => eventRequest("/rsvp", { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ ...data, bookingForm }) }));
});
field("event-refresh").addEventListener("click", loadEvent);
field("event-cancel").addEventListener("click", () => {
  if (window.confirm("Отменить регистрацию?")) changeEvent(() => eventRequest("/rsvp", { method: "DELETE" }));
});
if (eventId) loadEvent();
