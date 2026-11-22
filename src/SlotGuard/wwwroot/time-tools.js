"use strict";
const converter = document.getElementById("zone-convert");
const output = document.getElementById("zone-results");
const message = document.getElementById("zone-message");
const controls = converter.elements;
fillTimeZones(controls.from, savedTimeZone() || browserTimeZone);
fillTimeZones(controls.to, "UTC");
const now = new Date();
controls.datetime.value = `${zoneFormats(controls.from.value).day.format(now)}T${zoneFormats(controls.from.value).time.format(now)}`;

function wallTime(date, zone) {
  return `${zoneFormats(zone).day.format(date)}T${zoneFormats(zone).time.format(date)}`;
}
function interpretations(local, zone) {
  const naive = Date.parse(local + ":00Z");
  if (!Number.isFinite(naive)) return [];
  const hour = 3600000;
  const shifts = new Set();
  for (const distance of [-36, -12, 0, 12, 36]) {
    const instant = naive + distance * hour;
    shifts.add(Date.parse(wallTime(new Date(instant), zone) + ":00Z") - instant);
  }
  return [...shifts].map(shift => naive - shift).sort((one, two) => one - two)
    .filter(instant => wallTime(new Date(instant), zone) === local);
}
function convert() {
  const from = controls.from.value, to = controls.to.value, local = controls.datetime.value;
  const times = interpretations(local, from);
  message.textContent = times.length > 1 ? "Из-за перевода часов у исходного времени два соответствия."
    : times.length === 0 ? "Такого местного времени нет. Проверьте дату или выберите соседний час." : "";
  message.hidden = !message.textContent;
  output.replaceChildren(...times.map(value => {
    const item = document.createElement("li");
    item.className = "panel p3-result";
    const date = new Date(value);
    const result = document.createElement("strong");
    result.textContent = `${zoneFormats(to).longDate.format(date)} · ${timeWithOffset(date, to)}`;
    const detail = document.createElement("span");
    detail.className = "p3-muted";
    detail.textContent = `${timeZoneLabel(to)} · исходное смещение ${utcOffset(date, from)}`;
    item.append(result, detail);
    return item;
  }));
}
converter.addEventListener("submit", event => { event.preventDefault(); convert(); });
convert();
