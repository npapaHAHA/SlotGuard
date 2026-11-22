(() => {
  const get = id => document.getElementById(id);
  const weekdays = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];
  const hours = [9, 11, 13, 15, 17];
  const lines = ["·xx·r", "x··m·", "·r·x·", "xx··m", "··x·r", "x·m·x", "r··x·"];
  const statusByMark = { "·": "available", x: "booked", r: "held", m: "maintenance" };
  const labels = { available: "Свободно", booked: "Занято", held: "Резерв", maintenance: "Обслуживание", mine: "Ваша бронь" };
  const reasons = {
    booked: "На это время уже есть запись.",
    held: "Время временно закреплено за участником очереди.",
    maintenance: "Организатор закрыл время на обслуживание."
  };
  const firstDay = new Date();
  firstDay.setHours(12, 0, 0, 0);
  firstDay.setDate(firstDay.getDate() + 7 - (firstDay.getDay() + 6) % 7);
  const dates = weekdays.map((_, index) => {
    const date = new Date(firstDay);
    date.setDate(date.getDate() + index);
    return date;
  });
  const short = new Intl.DateTimeFormat("ru-RU", { weekday: "short", day: "numeric", month: "short" });
  const long = new Intl.DateTimeFormat("ru-RU", { weekday: "long", day: "numeric", month: "long" });
  const week = new Intl.DateTimeFormat("ru-RU", { day: "numeric", month: "long" });
  const clock = hour => `${String(hour).padStart(2, "0")}:00`;
  const slotTime = slot => `${clock(slot.hour)}–${clock(slot.hour + 1)}`;
  const slots = lines.flatMap((line, day) => [...line].map((mark, row) => ({
    day, row, hour: hours[row], initialStatus: statusByMark[mark], status: statusByMark[mark]
  })));
  const initial = 6;
  let selected = initial;
  let mapTab = initial;
  let outcome = null;

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (tag === "button") node.type = "button";
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function choose(index, navigate = false) {
    selected = mapTab = index;
    outcome = null;
    render();
    if (navigate) {
      get("demo-time").focus({ preventScroll: true });
      get("demo-time").scrollIntoView({ block: "nearest", behavior: reducedMotion.matches ? "auto" : "smooth" });
    }
  }

  get("demo-time").tabIndex = -1;
  get("demo-week-range").textContent = week.formatRange(dates[0], dates.at(-1));
  const days = dates.map((date, index) => {
    const button = element("button", "landing-day");
    button.setAttribute("aria-label", long.format(date));
    button.append(element("span", "", weekdays[index]), element("strong", "", date.getDate()));
    button.addEventListener("click", () => {
      if (slots[selected].day === index) return;
      const first = slots.findIndex(slot => slot.day === index && slot.status === "available");
      choose(first >= 0 ? first : index * hours.length);
    });
    return button;
  });
  get("demo-days").append(...days);
  const times = hours.map((hour, row) => {
    const button = element("button", "landing-time", clock(hour));
    button.addEventListener("click", () => choose(slots[selected].day * hours.length + row));
    return button;
  });
  get("demo-times").append(...times);

  const plane = get("demo-plane");
  for (const [index, label] of weekdays.entries()) {
    const heading = element("span", "week-scene-day", label);
    heading.style.gridColumn = index + 1;
    heading.setAttribute("aria-hidden", "true");
    plane.append(heading);
  }
  const tileButtons = slots.map((slot, index) => {
    const tile = element("button", "week-voxel");
    tile.style.gridColumn = slot.day + 1;
    tile.style.gridRow = slot.row + 2;
    tile.addEventListener("click", () => choose(index));
    tile.addEventListener("keydown", event => {
      const move = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key];
      if (!move) return;
      event.preventDefault();
      const day = Math.min(weekdays.length - 1, Math.max(0, slot.day + move[0]));
      const row = Math.min(hours.length - 1, Math.max(0, slot.row + move[1]));
      tileButtons[mapTab].tabIndex = -1;
      mapTab = day * hours.length + row;
      const next = tileButtons[mapTab];
      next.tabIndex = 0;
      next.focus();
    });
    return tile;
  });
  plane.append(...tileButtons);
  bindWeekMapTilt(get("demo-stage"), get("demo-map-flat"));

  function alternatives(slot) {
    const position = item => item.day * 24 + item.hour;
    return slots.map((item, index) => ({ item, index }))
      .filter(({ item }) => item.status === "available")
      .sort((left, right) => Math.abs(position(left.item) - position(slot)) - Math.abs(position(right.item) - position(slot)))
      .slice(0, 3);
  }

  function renderFeedback(slot) {
    const panel = get("demo-feedback");
    const taken = Boolean(reasons[slot.status]);
    panel.hidden = slot.status === "available";
    if (panel.hidden) return;
    panel.dataset.tone = taken ? "changed" : "success";
    get("demo-feedback-title").textContent = outcome === "race" ? "Одна заявка прошла"
      : taken ? labels[slot.status] : "Бронь подтверждена";
    get("demo-feedback-text").textContent = outcome === "race"
      ? "Первый участник получил бронь. Второму SlotGuard предлагает свободное время."
      : taken ? reasons[slot.status] : `Зал «Север» · ${short.format(dates[slot.day])}, ${slotTime(slot)}.`;
    const options = taken ? alternatives(slot) : [];
    get("demo-alternatives").hidden = !options.length;
    get("demo-alternative-list").replaceChildren(...options.map(({ item, index }) => {
      const hint = `${short.format(dates[item.day])} · ${clock(item.hour)}`;
      const control = element("button", "outline-action", hint);
      control.addEventListener("click", () => choose(index, true));
      return control;
    }));
  }

  function render() {
    const slot = slots[selected];
    const activeDay = slot.day;
    for (const [index, control] of days.entries()) control.setAttribute("aria-pressed", String(index === activeDay));
    for (const [row, control] of times.entries()) {
      const index = activeDay * hours.length + row;
      const item = slots[index];
      control.dataset.status = item.status;
      control.setAttribute("aria-pressed", String(selected === index));
      control.setAttribute("aria-label", `${clock(item.hour)} · ${labels[item.status]}`);
    }
    let free = 0;
    for (const [index, control] of tileButtons.entries()) {
      const item = slots[index];
      if (item.status === "available") free++;
      control.className = `week-voxel ${item.status}`;
      control.tabIndex = mapTab === index ? 0 : -1;
      control.setAttribute("aria-pressed", String(selected === index));
      const label = `${long.format(dates[item.day])}, ${slotTime(item)} · ${labels[item.status]}`;
      control.setAttribute("aria-label", label);
      control.title = label;
    }
    get("demo-map-count").textContent = `Свободно ${free} из ${slots.length}`;
    get("demo-date").textContent = long.format(dates[activeDay]);
    get("demo-time").textContent = slotTime(slot);
    get("demo-status").dataset.status = slot.status;
    get("demo-status").textContent = labels[slot.status];
    get("demo-book").disabled = get("demo-race").disabled = slot.status !== "available";
    get("demo-reset").disabled = selected === initial && slots.every(item => item.status === item.initialStatus);
    renderFeedback(slot);
  }

  function book(race) {
    const slot = slots[selected];
    if (slot.status !== "available") return;
    slot.status = race ? "booked" : "mine";
    outcome = race ? "race" : null;
    render();
    reveal(get("demo-feedback"));
    get("demo-feedback-title").focus({ preventScroll: true });
    get("demo-feedback").scrollIntoView({ block: "nearest", behavior: reducedMotion.matches ? "auto" : "smooth" });
  }

  get("demo-book").addEventListener("click", () => book(false));
  get("demo-race").addEventListener("click", () => book(true));
  get("demo-reset").addEventListener("click", () => {
    for (const slot of slots) slot.status = slot.initialStatus;
    choose(initial);
    get("demo-book").focus({ preventScroll: true });
  });
  render();
})();
