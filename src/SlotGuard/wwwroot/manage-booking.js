(() => {
  const get = id => document.getElementById(`manage-${id}`);
  const storageKey = "slotguard:booking-management";
  const endpoint = "/api/manage-booking";
  const labels = {
    confirmed: "Бронь подтверждена",
    approved: "Бронь подтверждена",
    pending: "Ожидает согласования",
    rejected: "Заявка отклонена",
    cancelled: "Бронь отменена",
    expired: "Срок заявки истёк"
  };
  let token = "";
  let current = null;
  let busy = false;

  if (location.hash) {
    token = location.hash.slice(1);
    history.replaceState(null, "", location.pathname);
    try { sessionStorage.setItem(storageKey, token); } catch { /* The current tab can still use the link. */ }
  } else {
    try { token = sessionStorage.getItem(storageKey) ?? ""; } catch { /* This browser disables storage. */ }
  }

  async function request(path = "", options = {}) {
    const response = await fetch(endpoint + path, {
      ...options,
      credentials: "omit",
      cache: "no-store",
      headers: { "X-SlotGuard-Booking-Token": token, ...options.headers }
    });
    if (!response.ok) {
      const problem = await response.json().catch(() => null);
      const message = problem?.title ?? (response.status === 429 ? "Слишком много запросов. Попробуйте через минуту." :
        "Не удалось выполнить действие. Обновите статус брони.");
      throw Object.assign(new Error(message), { status: response.status });
    }
    return response;
  }

  function notify(text, error = false) {
    const message = get("message");
    message.textContent = text;
    message.hidden = !text;
    message.classList.toggle("error", error);
  }

  function setBusy(value) {
    busy = value;
    for (const id of ["move", "download", "cancel", "refresh"]) get(id).disabled = value;
  }

  function render(view) {
    current = view;
    const { booking, timeZone } = view;
    get("name").textContent = view.name;
    get("location").textContent = view.location;
    get("status").textContent = labels[booking.status] ?? booking.status;
    get("status").dataset.state = booking.status;
    get("date").textContent = zoneFormats(timeZone).longDate.format(new Date(booking.startsAt));
    get("time").textContent = `${slotTimeRange(booking.startsAt, timeZone, booking.endsAt)} · ${timeZone}`;
    const payment = get("payment");
    payment.textContent = bookingPaymentLabel(booking.payment);
    payment.hidden = !payment.textContent;
    get("reason").textContent = booking.reason ?? "";
    get("reason").hidden = !booking.reason;
    get("deadlines").textContent = bookingDeadlines(booking.changes, booking.startsAt, timeZone);
    get("deadlines").hidden = !get("deadlines").textContent;
    get("move").hidden = !booking.canReschedule;
    get("cancel").hidden = !booking.changes?.canCancel;
    get("download").hidden = !view.canDownload;
    get("refresh").hidden = booking.status !== "pending";
    get("loading").hidden = true;
    get("card").hidden = false;
  }

  function showUnavailable(invalid) {
    current = null;
    get("card").hidden = true;
    get("loading").hidden = false;
    get("loading-title").textContent = invalid ? "Ссылка недоступна" : "Не удалось загрузить бронь";
    get("loading-message").textContent = invalid ? "Откройте личную ссылку из письма с подтверждением." :
      "Проверьте соединение и попробуйте ещё раз.";
    get("loading-message").hidden = false;
    get("retry").hidden = invalid;
  }

  async function load() {
    if (!token) {
      showUnavailable(true);
      return false;
    }
    setBusy(true);
    try {
      render(await (await request()).json());
      return true;
    } catch (error) {
      showUnavailable(error.status === 404);
      return false;
    } finally {
      setBusy(false);
    }
  }

  get("retry").addEventListener("click", load);
  get("refresh").addEventListener("click", () => { if (!busy) load(); });
  get("move").addEventListener("click", () => {
    if (busy || !current?.booking.canReschedule) return;
    const { booking, timeZone, name } = current;
    const url = `/${encodeURIComponent(booking.id)}/reschedule`;
    openBookingReschedule({
      booking: { ...booking, resourceName: name },
      zone: timeZone,
      load: async () => (await request(url)).json(),
      save: async startsAt => (await request(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ startsAt })
      })).json(),
      onMoved: async () => {
        if (await load()) notify(current.booking.status === "pending" ?
          "Новое время отправлено на согласование." : "Бронь перенесена.");
      }
    });
  });
  get("cancel").addEventListener("click", async () => {
    if (busy || !current?.booking.changes?.canCancel || !window.confirm("Отменить эту бронь?")) return;
    setBusy(true);
    try {
      await request(`/${encodeURIComponent(current.booking.id)}`, { method: "DELETE" });
      if (await load()) notify("");
    } catch (error) {
      if ([404, 409, 422].includes(error.status)) await load();
      notify(error.message, true);
    } finally {
      setBusy(false);
    }
  });
  get("download").addEventListener("click", async () => {
    if (busy || !current?.canDownload) return;
    setBusy(true);
    try {
      const file = await (await request("/calendar.ics")).blob();
      const link = document.createElement("a");
      link.href = URL.createObjectURL(file);
      link.download = "slotguard.ics";
      link.click();
      setTimeout(() => URL.revokeObjectURL(link.href), 1000);
    } catch (error) {
      notify(error.message, true);
    } finally {
      setBusy(false);
    }
  });
  load();
})();
