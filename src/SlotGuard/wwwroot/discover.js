"use strict";
const discoverForm = document.getElementById("discover-search");
const discoverStatus = document.getElementById("discover-status");
const discoverResults = document.getElementById("discover-results");
let discoverRequest;
function node(tag, text, style = "") {
  const item = document.createElement(tag);
  item.textContent = text ?? "";
  item.className = style;
  return item;
}
function discoverCard(item, event) {
  const result = node("article", "", "panel p3-directory-card");
  if (!event && item.publicPhotoUrl) {
    const photo = node("img");
    Object.assign(photo, { alt: "", src: item.publicPhotoUrl, loading: "lazy", referrerPolicy: "no-referrer" });
    photo.addEventListener("error", () => photo.remove());
    result.append(photo);
  }
  result.append(node("p", [item.city, item.organization].filter(Boolean).join(" · "), "p3-muted"),
    node("h2", item.name), node("p", item.location, "p3-muted"));
  if (event) {
    const zone = savedTimeZone() || browserTimeZone;
    const date = new Date(item.startUtc);
    result.append(node("p", `${zoneFormats(zone).longDate.format(date)} · ${timeWithOffset(date, zone)}`, "p3-muted"));
  } else if (item.priceMinor != null) result.append(node("p", `${bookingPrice(item.priceMinor)} за бронь`, "p3-muted"));
  if (item.featured) result.append(node("p", "Рекомендовано организатором", "p3-muted"));
  const link = node("a", event ? "Открыть мероприятие" : "Выбрать время", "outline-action");
  link.href = `/${event ? "events" : "book"}/${encodeURIComponent(item.id)}`;
  result.append(link);
  return result;
}
async function searchDiscover() {
  discoverRequest?.abort();
  const controller = new AbortController();
  discoverRequest = controller;
  discoverStatus.textContent = "Загрузка…";
  try {
    const query = new URLSearchParams();
    for (const [name, value] of new FormData(discoverForm)) if (value.trim()) query.set(name, value.trim());
    const response = await fetch(`/api/discover?${query}`, { signal: controller.signal });
    if (!response.ok) throw new Error("Не удалось загрузить каталог.");
    const { venues, events } = await response.json();
    if (controller.signal.aborted) return;
    discoverResults.replaceChildren(...venues.map(item => discoverCard(item, false)), ...events.map(item => discoverCard(item, true)));
    discoverStatus.textContent = venues.length + events.length ? "" : "По этим фильтрам пока ничего нет.";
  } catch (error) {
    if (!controller.signal.aborted) discoverStatus.textContent = error.message;
  }
}
discoverForm.addEventListener("submit", event => { event.preventDefault(); searchDiscover(); });
searchDiscover();
