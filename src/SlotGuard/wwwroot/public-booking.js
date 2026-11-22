"use strict";

const get = id => document.getElementById(id);
const publicPath = /^\/(book|embed)\/([a-f\d-]{32,36})\/?$/i.exec(location.pathname);
const embedded = publicPath?.[1].toLowerCase() === "embed";
const link = publicPath?.[2].toLowerCase();
const parentOrigin = embedded ? new URLSearchParams(location.search).get("origin") : null;
const emailProof = embedded ? null : new URLSearchParams(location.hash.slice(1)).get("verify");
if (emailProof) history.replaceState(null, "", location.pathname + location.search);
const resourceUrl = `/api/${embedded ? "embed" : "public"}/resources/${link}`;
const storageScope = embedded ? `embed:${link}:${parentOrigin ?? ""}` : link;
const receiptKey = `slotguard:receipt:${storageScope}`;
const seriesReceiptKey = `slotguard:series:${storageScope}`;
const sessionKey = `slotguard:session:${storageScope}`;
let embedSession;
if (embedded) {
  document.body.classList.add("embed-mode");
  get("public-embed-footer").hidden = false;
  get("public-embed-full").href = `/book/${link}`;
  get("public-email-optional").hidden = true;
  get("public-email").required = true;
  try { embedSession = sessionStorage.getItem(sessionKey); } catch { /* Use an in-memory session. */ }
}
let displayPreference = savedTimeZone();
let displayZone = displayPreference || browserTimeZone;
const dateText = new Intl.DateTimeFormat("ru-RU", { weekday: "long", day: "numeric", month: "long", timeZone: "UTC" });
const monthText = new Intl.DateTimeFormat("ru-RU", { month: "long", year: "numeric", timeZone: "UTC" });
const bookingMinutes = new Intl.NumberFormat("ru-RU", { style: "unit", unit: "minute", unitDisplay: "long" });
const compactBooking = matchMedia("(max-width: 600px)");
const categories = {
  room: ["Помещение", "meeting_room"], studio: ["Студия", "event_available"],
  equipment: ["Оборудование", "build"], other: ["Ресурс", "event_available"]
};
const receiptLabels = {
  awaiting_payment: ["schedule", "Слот зарезервирован", ""],
  payment_expired: ["schedule", "Резерв истёк", "Слот снова доступен для бронирования."],
  payment_cancelled: ["×", "Резерв отменён", ""],
  confirmed: ["check_circle", "Бронь подтверждена", ""],
  approved: ["check_circle", "Заявка одобрена", ""],
  pending: ["schedule", "Заявка отправлена", "Решение появится здесь после проверки администратором."],
  rejected: ["×", "Заявка отклонена", ""],
  expired: ["schedule", "Срок заявки истёк", ""],
  cancelled: ["×", "Бронь отменена", ""],
  rescheduled: ["schedule", "Бронь перенесена", ""]
};
const state = { resource: null, days: new Map(), month: "", day: "", selection: null, receipt: null, busy: false };
let paymentTimer;
let submittingPublicBooking = false;
const publicQuestions = bookingFields(get("public-booking-questions"));
let seriesReceipts = [];
let requestedReceipt;
try { seriesReceipts = JSON.parse(sessionStorage.getItem(seriesReceiptKey)) ?? []; } catch { /* Storage can be unavailable. */ }

function utcDay(date) {
  return new Date(`${date}T00:00:00Z`);
}

function dayOf(slot) {
  return zoneFormats(displayZone).day.format(new Date(slot));
}

function button(text, className, action) {
  const control = document.createElement("button");
  control.type = "button";
  control.className = className;
  control.textContent = text;
  control.addEventListener("click", action);
  return control;
}

function fetchPublic(suffix = "", options) {
  const request = { cache: "no-store", ...options };
  if (embedded) {
    const headers = new Headers(request.headers);
    if (embedSession) headers.set("X-SlotGuard-Embed-Session", embedSession);
    if (parentOrigin) headers.set("X-SlotGuard-Embed-Origin", parentOrigin);
    Object.assign(request, { headers, credentials: "omit" });
  }
  return fetch(resourceUrl + suffix, request);
}

async function prepareSession() {
  const session = await api("/session", { method: "POST" });
  if (embedded && session?.session) {
    embedSession = session.session;
    try { sessionStorage.setItem(sessionKey, embedSession); } catch { /* Keep the token in memory. */ }
  }
}

async function api(suffix = "", options) {
  const response = await fetchPublic(suffix, options);
  if (response.status === 204) return;
  if (response.ok) return response.json();
  const problem = await response.json().catch(() => ({}));
  const message = problem.title ?? (response.status === 429
    ? "Слишком много попыток. Повторите через минуту." : "Не удалось выполнить запрос.");
  throw Object.assign(new Error(message), { status: response.status, alternatives: problem.alternatives,
    bookingForm: problem.bookingForm, questionErrors: problem.questionErrors, emailDomain: problem.emailDomain,
    appearance: problem.appearance });
}

function remember(receiptId) {
  requestedReceipt = receiptId;
  try {
    if (receiptId) sessionStorage.setItem(receiptKey, receiptId);
    else sessionStorage.removeItem(receiptKey);
  } catch {
    // The current receipt is still visible in browsers that disable storage.
  }
}

function rememberedReceipt() {
  if (requestedReceipt !== undefined) return requestedReceipt;
  try { return sessionStorage.getItem(receiptKey); }
  catch { return null; }
}

function unavailable(retry, detail) {
  get("public-layout").hidden = true;
  get("public-state").hidden = false;
  get("public-state-title").textContent = retry ? "Не удалось загрузить страницу" : "Ссылка недоступна";
  const text = get("public-state-text");
  text.textContent = detail || (retry ? "Проверьте подключение и повторите попытку." : "Уточните актуальную ссылку у администратора.");
  text.hidden = false;
  get("public-verification").hidden = true;
  get("public-retry").hidden = !retry;
}

function verificationMessage(text, error = false) {
  const notice = get("public-verification-message");
  notice.textContent = text;
  notice.hidden = !text;
  notice.classList.toggle("error", error);
}

function showEmailVerification(domain) {
  unavailable(false, `Эта приватная ссылка доступна адресам из домена ${domain}. Отправим письмо для подтверждения.`);
  get("public-state-title").textContent = "Подтвердите email";
  const form = get("public-verification");
  form.elements.email.placeholder = `name@${domain}`;
  form.hidden = false;
}

function showAccessError(error) {
  if (error.emailDomain) {
    renderPublicBrand(error.appearance);
    showEmailVerification(error.emailDomain);
  }
  else if (error.status === 410) unavailable(false, error.message);
  else unavailable(error.status !== 404);
}

function addSlot(slot) {
  const day = dayOf(slot);
  const values = state.days.get(day) ?? new Set();
  values.add(slot);
  state.days.set(day, values);
}

function removeSlot(slot) {
  state.days.get(dayOf(slot))?.delete(slot);
}

function renderPublicBrand(brand) {
  const name = embedded ? "public-embed-brand" : "public-brand-name";
  const logo = embedded ? "public-embed-logo" : "public-brand-logo";
  applyBookingBrand(document.body, get(name), get(logo), get("public-brand-banner"),
    embedded && brand ? { ...brand, bannerUrl: null } : brand);
  return get(name).textContent;
}

function renderResource(resource) {
  const limit = get("public-private-limit");
  limit.hidden = !(resource.remainingUses > 0);
  limit.textContent = limit.hidden ? "" : `Осталось бронирований по ссылке — ${resource.remainingUses}.`;
  const [type, icon] = categories[resource.type] ?? categories.other;
  get("public-resource-type").textContent = type;
  get("public-resource-icon").textContent = icon;
  get("public-resource-name").textContent = resource.name;
  get("public-location").textContent = resource.location;
  get("public-resource-zone").textContent = `Время ресурса · ${timeZoneLabel(resource.resourceTimeZone)}`;
  const policy = get("public-booking-policy");
  policy.textContent = bookingPolicySummary(resource.policy);
  policy.hidden = !policy.textContent;
  get("public-approval").hidden = !resource.requiresApproval;
  const price = get("public-price");
  price.hidden = resource.priceMinor == null;
  price.textContent = price.hidden ? "" : `${bookingPrice(resource.priceMinor)} за бронь · демо-оплата`;
  const capacity = get("public-capacity");
  capacity.hidden = resource.capacity === null;
  capacity.textContent = `Вместимость · ${resource.capacity} чел.`;
  const equipment = get("public-equipment");
  equipment.hidden = !resource.equipment.length;
  equipment.replaceChildren(...resource.equipment.map(text => {
    const item = document.createElement("li");
    item.textContent = text;
    return item;
  }));
  if (resource.photoUrl) get("public-resource-photo").src = resource.photoUrl;
  document.title = `${resource.name} · ${renderPublicBrand(resource.appearance)}`;
}

function renderCalendar(focus) {
  const first = utcDay(state.month + "-01");
  get("public-month").textContent = monthText.format(first);
  const offset = (first.getUTCDay() + 6) % 7;
  const days = Array.from({ length: offset }, () => document.createElement("span"));
  for (let index = 1; index <= 31; index++) {
    const date = new Date(first);
    date.setUTCDate(index);
    const day = date.toISOString().slice(0, 10);
    if (!day.startsWith(state.month)) break;
    const control = button(date.getUTCDate(), "public-day", event => pickDay(day, event.detail > 0));
    const available = Boolean(state.days.get(day)?.size);
    control.dataset.date = day;
    control.dataset.available = String(available);
    control.setAttribute("aria-label", dateText.format(date));
    control.setAttribute("aria-pressed", String(state.day === day));
    if (day === state.resource.today) control.setAttribute("aria-current", "date");
    days.push(control);
  }
  const calendar = get("public-days");
  calendar.replaceChildren(...days);
  if (focus) calendar.querySelector('[aria-pressed="true"]:enabled')?.focus({ preventScroll: true });
}

function renderTimes() {
  const { durationOptions, durationMinutes } = state.resource;
  fillDurations(get("public-duration"), durationOptions, durationMinutes);
  get("public-duration-control").hidden = durationOptions.length < 2;
  get("public-fixed-duration").hidden = durationOptions.length > 1;
  get("public-duration-label").textContent = bookingMinutes.format(durationMinutes);
  get("public-slot-date").textContent = dateText.format(utcDay(state.day));
  const slots = [...(state.days.get(state.day) ?? [])].sort();
  const labels = slots.map(slot => zoneFormats(displayZone).time.format(new Date(slot)));
  const times = get("public-times");
  times.classList.toggle("flexible-times", durationOptions.length > 1);
  times.replaceChildren(...slots.map((slot, index) => {
    const label = labels[index];
    const duplicate = labels.indexOf(label) !== labels.lastIndexOf(label);
    const control = button(duplicate ? timeWithOffset(slot, displayZone) : label, "public-time", event => {
      state.selection = slot;
      showMessage("");
      for (const time of times.children)
        time.setAttribute("aria-pressed", String(time === control));
      syncControls();
      if (event.detail > 0 && compactBooking.matches && !embedded) focusBookingStep("public-checkout-title", "start");
    });
    control.setAttribute("aria-pressed", String(state.selection === slot));
    return control;
  }));
  get("public-empty").textContent = state.resource.bookingMessage ?? "На этот день свободных слотов нет.";
  get("public-empty").hidden = slots.length > 0;
  syncControls();
}

function focusBookingStep(id, block = "nearest") {
  const target = get(id);
  target.focus({ preventScroll: true });
  if (!embedded) target.scrollIntoView({ block, behavior: reducedMotion.matches ? "auto" : "smooth" });
}

function syncControls() {
  get("public-previous").disabled = state.busy || state.month <= state.resource.today.slice(0, 7);
  get("public-next").disabled = state.busy || state.month >= state.resource.lastDay.slice(0, 7);
  for (const day of get("public-days").querySelectorAll("button"))
    day.disabled = state.busy || day.dataset.available === "false";
  for (const time of get("public-times").children) time.disabled = state.busy;
  for (const option of get("public-alternative-list").children) option.disabled = state.busy;
  get("public-time-zone").disabled = state.busy;
  get("public-duration").disabled = state.busy;
  get("public-reschedule").disabled = state.busy;
  get("public-cancel-booking").disabled = state.busy;
  get("public-pay").disabled = state.busy;
  const submit = get("public-submit");
  submit.disabled = state.busy || !state.selection;
  submit.setAttribute("aria-busy", String(submittingPublicBooking));
  submit.textContent = submittingPublicBooking ? "Сохраняем…" : state.resource.priceMinor != null ? "Зарезервировать для оплаты"
    : state.resource.requiresApproval ? "Отправить заявку" : "Забронировать";
  get("public-checkout").hidden = !state.selection;
  get("public-email").disabled = state.busy;
  publicQuestions.visible(Boolean(state.selection));
  publicQuestions.disable(state.busy);
  get("public-series").hidden = !state.selection || state.resource.priceMinor != null || (state.resource.remainingUses ?? 8) < 2;
  get("public-series").disabled = state.busy;
  get("public-new-booking").hidden = state.resource.previousBookingId != null || state.resource.remainingUses === 0;
  const selected = get("public-selection");
  if (state.selection) {
    const end = new Date(Date.parse(state.selection) + state.resource.durationMinutes * 60000);
    selected.textContent = `${dateText.format(utcDay(state.day))} · ${slotTimeRange(state.selection, displayZone, end)}`;
  } else selected.textContent = "";
}

function showMessage(text, alternatives = []) {
  const message = get("public-message");
  const changed = message.textContent !== text;
  message.textContent = text;
  message.hidden = !text;
  const panel = get("public-alternatives");
  panel.hidden = !alternatives.length;
  get("public-alternative-list").replaceChildren(...alternatives.map(slot => {
    const label = `${dateText.format(utcDay(dayOf(slot)))} · ${timeWithOffset(slot, displayZone)}`;
    return button(label, "", () => {
      addSlot(slot);
      state.day = dayOf(slot);
      state.month = state.day.slice(0, 7);
      state.selection = slot;
      showMessage("");
      renderCalendar();
      renderTimes();
      focusBookingStep("public-checkout-title");
    });
  }));
  if (alternatives.length) reveal(panel, 3);
  else if (text && changed) reveal(message, 3);
}

function pickDay(day, scroll) {
  state.day = day;
  state.selection = null;
  showMessage("");
  renderCalendar(true);
  renderTimes();
  if (!state.receipt) reveal(get("public-times"), 3);
  if (scroll && !state.receipt && compactBooking.matches && !embedded) focusBookingStep("public-time-title", "start");
}

function moveMonth(delta) {
  const date = utcDay(state.month + "-01");
  date.setUTCMonth(date.getUTCMonth() + delta);
  state.month = date.toISOString().slice(0, 7);
  const firstAvailable = [...state.days.keys()].sort().find(day => day.startsWith(state.month) && state.days.get(day).size);
  state.day = firstAvailable ?? state.month + "-01";
  state.selection = null;
  showMessage("");
  renderCalendar();
  renderTimes();
}

function renderReceipt(receipt) {
  clearTimeout(paymentTimer);
  const previous = state.receipt;
  state.receipt = receipt;
  get("public-time-title").textContent = receipt ? "Детали бронирования" : "Выберите время";
  get("public-time-form").hidden = Boolean(receipt);
  const receiptPanel = get("public-receipt");
  receiptPanel.hidden = !receipt;
  if (!receipt) return;
  renderSeriesReceipts(receipt.id);
  const [icon, title, description] = receiptLabels[receipt.status];
  const headingIcon = get("public-receipt-icon");
  headingIcon.textContent = icon;
  headingIcon.classList.toggle("symbol", icon !== "×");
  get("public-receipt-title").textContent = title;
  get("public-receipt-time").textContent =
    `${dateText.format(utcDay(dayOf(receipt.startsAt)))} · ${slotTimeRange(receipt.startsAt, displayZone, receipt.endsAt)}`;
  const message = get("public-receipt-message");
  message.textContent = receipt.reason || description;
  message.hidden = !message.textContent;
  get("public-check-status").hidden = receipt.status !== "pending";
  const calendar = get("public-calendar-download");
  const confirmed = ["confirmed", "approved"].includes(receipt.status);
  receiptPanel.dataset.tone = confirmed ? "success"
    : ["pending", "awaiting_payment"].includes(receipt.status) ? "waiting" : "";
  if (previous?.id !== receipt.id || previous?.status !== receipt.status || previous?.startsAt !== receipt.startsAt)
    reveal(receiptPanel, 4);
  calendar.hidden = !confirmed;
  calendar.href = `${resourceUrl}/bookings/${encodeURIComponent(receipt.id)}/calendar.ics`;
  const completion = get("public-completion");
  const brand = state.resource?.appearance;
  const next = confirmed && brand?.completionLabel ? bookingBrandUrl(brand.completionUrl) : null;
  completion.hidden = !next;
  if (next) {
    completion.href = next;
    completion.textContent = brand.completionLabel;
  } else completion.removeAttribute("href");
  get("public-reschedule").hidden = !receipt.canReschedule;
  get("public-cancel-booking").hidden = !receipt.changes?.canCancel;
  renderPublicPayment(receipt);
  const deadlines = get("public-booking-deadlines");
  deadlines.textContent = bookingDeadlines(receipt.changes, receipt.startsAt, displayZone);
  deadlines.hidden = !deadlines.textContent;
}

function renderPublicPayment(receipt) {
  const summary = bookingPaymentLabel(receipt.payment);
  const awaiting = receipt.status === "awaiting_payment";
  get("public-payment").hidden = !summary;
  get("public-payment-amount").textContent = summary;
  get("public-pay").hidden = !awaiting;
  const note = get("public-payment-note");
  note.hidden = !awaiting;
  if (awaiting) {
    const expires = receipt.payment.expiresAt;
    note.textContent = `Резерв до ${timeWithOffset(expires, displayZone)}. Демо-касса работает без списания денег.`;
    paymentTimer = setTimeout(() => checkReceipt(receipt.id, true), Math.max(2000, Date.parse(expires) - Date.now() + 250));
  }
}

function renderSeriesReceipts(activeId) {
  const list = get("public-series-receipts");
  list.hidden = !seriesReceipts.some(item => item.id === activeId);
  if (list.hidden) return;
  const title = document.createElement("h4");
  title.textContent = "Записи в серии";
  list.replaceChildren(title, ...seriesReceipts.map(item => {
    const choice = button(appointmentLabel(item.startsAt), "outline-action", async () => {
      remember(item.id);
      await checkReceipt(item.id, true);
    });
    choice.setAttribute("aria-pressed", String(item.id === activeId));
    return choice;
  }));
}

function appointmentLabel(start) {
  return `${zoneFormats(displayZone).shortDate.format(new Date(start))} · ${timeWithOffset(start, displayZone)}`;
}

async function checkReceipt(id, reportError = false) {
  const check = get("public-check-status");
  check.disabled = true;
  try {
    let receipt = await api(`/bookings/${encodeURIComponent(id)}`);
    const seen = new Set([receipt.id]);
    while (receipt.rescheduledToId && !seen.has(receipt.rescheduledToId)) {
      receipt = await api(`/bookings/${encodeURIComponent(receipt.rescheduledToId)}`);
      seen.add(receipt.id);
    }
    if (receipt.id !== id) replaceSeriesReceipt(id, receipt);
    if (rememberedReceipt() === id) {
      remember(receipt.id);
      renderReceipt(receipt);
    }
  } catch (error) {
    if (error.status === 404) {
      if (rememberedReceipt() === id) remember(null);
      if (state.receipt?.id === id) {
        get("public-receipt-message").textContent = "Статус больше недоступен по этой ссылке.";
        get("public-receipt-message").hidden = false;
      }
    } else if (reportError && state.receipt?.id === id) {
      get("public-receipt-message").textContent = "Не удалось обновить статус. Повторите попытку.";
      get("public-receipt-message").hidden = false;
    }
  } finally {
    check.disabled = false;
  }
}

function replaceSeriesReceipt(id, receipt) {
  seriesReceipts = seriesReceipts.map(item => item.id === id ? { id: receipt.id, startsAt: receipt.startsAt } : item);
  saveSeriesReceipts();
}

function saveSeriesReceipts() {
  try { sessionStorage.setItem(seriesReceiptKey, JSON.stringify(seriesReceipts)); } catch { /* The list remains in this tab. */ }
}

function applyAvailability(resource, preserveSelection = false, preferredDay) {
  publicQuestions.setForm(resource.bookingForm);
  const email = get("public-email");
  if (resource.verifiedEmail) email.value = resource.verifiedEmail;
  else if (email.readOnly) email.value = "";
  email.readOnly = Boolean(resource.verifiedEmail);
  state.resource = resource;
  state.days.clear();
  for (const slot of resource.slots) addSlot(slot);
  if (!preserveSelection || !resource.slots.includes(state.selection)) state.selection = null;
  state.day = state.selection ? dayOf(state.selection) : preferredDay && preferredDay >= resource.today && preferredDay <= resource.lastDay
    ? preferredDay : state.days.keys().next().value ?? resource.today;
  state.month = state.day.slice(0, 7);
  renderResource(resource);
  renderCalendar();
  renderTimes();
}

async function loadAvailability(zone, duration = state.resource?.durationMinutes ?? 60) {
  const query = new URLSearchParams({ timeZone: zone, durationMinutes: duration });
  try { return await api(`?${query}`); }
  catch (error) {
    if (error.status !== 400 || duration === 60) throw error;
    query.set("durationMinutes", "60");
    return api(`?${query}`);
  }
}

async function load() {
  if (!link) return unavailable(false);
  get("public-retry").disabled = true;
  try {
    const resource = await loadAvailability(displayZone);
    applyAvailability(resource);
    get("public-state").hidden = true;
    get("public-layout").hidden = false;
    const id = rememberedReceipt() ?? resource.previousBookingId;
    if (id) {
      remember(id);
      await checkReceipt(id);
    }
  } catch (error) {
    showAccessError(error);
  } finally {
    get("public-retry").disabled = false;
  }
}

const displaySelector = get("public-time-zone");
fillTimeZones(displaySelector, displayPreference, true);
displaySelector.addEventListener("change", async () => {
  const preference = displaySelector.value;
  const zone = preference || browserTimeZone;
  state.busy = true;
  syncControls();
  try {
    if (zone !== displayZone) {
      const resource = await loadAvailability(zone);
      displayZone = zone;
      applyAvailability(resource, true);
      renderReceipt(state.receipt);
      showMessage("");
    }
    displayPreference = preference;
    saveTimeZone(preference);
  } catch (error) {
    if (error.status === 404 || error.status === 410 || error.emailDomain) showAccessError(error);
    else showMessage(error.message);
    displaySelector.value = displayPreference;
  } finally {
    state.busy = false;
    syncControls();
  }
});

get("public-duration").addEventListener("change", async event => {
  const duration = Number(event.currentTarget.value);
  state.busy = true;
  syncControls();
  try {
    applyAvailability(await loadAvailability(displayZone, duration), false, state.day);
    showMessage("");
  } catch (error) {
    if (error.status === 404 || error.status === 410 || error.emailDomain) showAccessError(error);
    else showMessage(error.message);
  } finally {
    state.busy = false;
    renderTimes();
  }
});

async function submit() {
  if (!state.selection || state.busy) return;
  const email = get("public-email");
  if (!email.reportValidity()) return;
  const bookingForm = publicQuestions.read();
  if (bookingForm === undefined) return;
  const start = state.selection;
  submittingPublicBooking = true;
  state.busy = true;
  syncControls();
  showMessage("");
  try {
    // Establish the cookie first so a retry after a lost booking response keeps the same owner.
    await prepareSession();
    const receipt = await api("/bookings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ startsAt: start, durationMinutes: state.resource.durationMinutes,
        email: email.value.trim() || null, bookingForm, expectedPriceMinor: state.resource.priceMinor ?? null })
    });
    removeSlot(start);
    state.selection = null;
    publicQuestions.clear();
    remember(receipt.id);
    renderReceipt(receipt);
    await reloadOpenTimes();
    focusBookingStep("public-receipt-title");
  } catch (error) {
    if (error.status === 410 || error.emailDomain) await load();
    else if (error.status === 404) unavailable(false);
    else if (error.status === 400) {
      removeSlot(start);
      state.selection = null;
      await load();
      showMessage(error.message);
    }
    else if (error.status === 409) {
      removeSlot(start);
      state.selection = null;
      await reloadOpenTimes();
      showMessage(error.message, error.alternatives ?? []);
      focusBookingStep(get("public-message").hidden ? "public-time-title" : "public-message");
    } else if (error.bookingForm) {
      updatePublicQuestions(error.bookingForm);
      publicQuestions.disable(false);
      publicQuestions.showErrors(error.questionErrors);
      showMessage(error.message);
    } else if (error.status === 422) {
      await reloadOpenTimes();
      showMessage(error.message);
    } else {
      showMessage(error instanceof TypeError ? "Соединение прервалось. Повторите попытку." : error.message);
    }
  } finally {
    submittingPublicBooking = false;
    state.busy = false;
    renderCalendar();
    renderTimes();
  }
}

function updatePublicQuestions(form) {
  state.resource.bookingForm = form;
  publicQuestions.setForm(form);
}

async function reloadOpenTimes() {
  try { applyAvailability(await loadAvailability(displayZone), false, state.day); }
  catch { /* The next refresh will fetch current availability. */ }
}

get("public-retry").addEventListener("click", load);
get("public-verification").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const submit = form.querySelector("button");
  if (submit.disabled) return;
  submit.disabled = true;
  verificationMessage("");
  try {
    await api("/verify", emailRequest({ email: form.elements.email.value.trim() }));
    verificationMessage("Письмо отправлено. Откройте ссылку из письма в течение 15 минут.");
  } catch (error) {
    if (error.status === 410 || error.status === 404) showAccessError(error);
    else verificationMessage(error.message, true);
  } finally {
    submit.disabled = false;
  }
});
get("public-previous").addEventListener("click", () => moveMonth(-1));
get("public-next").addEventListener("click", () => moveMonth(1));
get("public-submit").addEventListener("click", submit);
get("public-series").addEventListener("click", () => {
  const email = get("public-email");
  if (state.busy || !state.selection || !email.reportValidity()) return;
  openWeeklySeries({
    startsAt: state.selection,
    durationMinutes: state.resource.durationMinutes,
    maxCount: state.resource.remainingUses ?? 8,
    email: email.value.trim() || null,
    bookingForm: state.resource.bookingForm,
    answers: publicQuestions.read(false)?.answers,
    onQuestionsChanged: updatePublicQuestions,
    prepare: prepareSession,
    send: (suffix, options) => fetchPublic("/series" + suffix, options),
    onBooked: async result => {
      seriesReceipts = result.bookings.map(item => ({ id: item.id, startsAt: item.startsAt }));
      saveSeriesReceipts();
      state.selection = null;
      publicQuestions.clear();
      remember(seriesReceipts[0].id);
      await checkReceipt(seriesReceipts[0].id, true);
      await reloadOpenTimes();
      renderTimes();
    }
  });
});
get("public-new-booking").addEventListener("click", () => {
  remember(null);
  renderReceipt(null);
  get("public-times").querySelector("button")?.focus();
});
if (embedded) get("public-calendar-download").addEventListener("click", async event => {
  event.preventDefault();
  if (!state.receipt) return;
  try {
    const result = await fetchPublic(`/bookings/${encodeURIComponent(state.receipt.id)}/calendar.ics`);
    if (!result.ok) throw new Error("Календарь недоступен.");
    const url = URL.createObjectURL(await result.blob());
    const download = document.createElement("a");
    download.href = url;
    download.download = "slotguard.ics";
    download.click();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
  } catch {
    const message = get("public-receipt-message");
    message.textContent = "Не удалось скачать файл. Его можно открыть из письма.";
    message.hidden = false;
  }
});
get("public-check-status").addEventListener("click", () => checkReceipt(state.receipt.id, true));
get("public-pay").addEventListener("click", async () => {
  const receipt = state.receipt;
  if (state.busy || receipt?.status !== "awaiting_payment") return;
  state.busy = true;
  syncControls();
  try {
    renderReceipt(await api(`/bookings/${encodeURIComponent(receipt.id)}/pay`, { method: "POST" }));
  } catch (error) {
    await checkReceipt(receipt.id);
    const message = get("public-receipt-message");
    message.textContent = error.message;
    message.hidden = false;
  } finally {
    state.busy = false;
    syncControls();
  }
});
get("public-cancel-booking").addEventListener("click", async () => {
  const receipt = state.receipt;
  if (state.busy || !receipt?.changes?.canCancel || !window.confirm("Отменить эту бронь?")) return;
  state.busy = true;
  syncControls();
  try {
    await api(`/bookings/${encodeURIComponent(receipt.id)}`, { method: "DELETE" });
    await checkReceipt(receipt.id);
    await reloadOpenTimes();
  } catch (error) {
    if (error.status === 422) await checkReceipt(receipt.id);
    const message = get("public-receipt-message");
    message.textContent = error.message;
    message.hidden = false;
  } finally {
    state.busy = false;
    syncControls();
  }
});
get("public-reschedule").addEventListener("click", () => {
  const booking = state.receipt;
  if (state.busy || !booking?.canReschedule) return;
  const address = `/bookings/${encodeURIComponent(booking.id)}/reschedule`;
  openBookingReschedule({
    booking: { ...booking, resourceName: state.resource.name }, zone: displayZone,
    load: () => api(address),
    save: startsAt => api(address, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ startsAt })
    }),
    onMoved: async receipt => {
      remember(receipt.id);
      replaceSeriesReceipt(booking.id, receipt);
      renderReceipt(receipt);
      const note = get("public-receipt-message");
      note.textContent = receipt.status === "pending" ? "Новое время отправлено на согласование." : "Бронь перенесена.";
      note.hidden = false;
      get("public-time-title").focus({ preventScroll: true });
      await reloadOpenTimes();
    }
  });
});
const photo = get("public-resource-photo");
photo.addEventListener("load", () => { photo.hidden = false; });
photo.addEventListener("error", () => { photo.hidden = true; });

function emailRequest(data) {
  return { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(data) };
}

async function startPublicBooking() {
  if (emailProof && link) {
    try {
      await api("/verify/complete", emailRequest({ token: emailProof }));
    } catch (error) {
      await load();
      if (!get("public-verification").hidden) verificationMessage(error.message, true);
      return;
    }
  }
  await load();
}

startPublicBooking();
