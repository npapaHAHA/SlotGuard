let sceneCalendar = null;
let sceneTimeZone;

function renderWeekScene() {
  const calendar = state.calendar;
  if (sceneCalendar === calendar && sceneTimeZone === viewerZone) return;

  sceneCalendar = calendar;
  sceneTimeZone = viewerZone;
  const free = calendar.days.flatMap(day => day.slots).filter(slot => slot.status === "available").length;
  section("week-scene-available").textContent = `Свободные слоты · ${free}`;
  const formats = zoneFormats(viewerZone);
  const tiles = calendar.days.flatMap((day, dayIndex) => {
    const heading = element("span", "week-scene-day", weekdays[dayIndex]);
    heading.style.gridColumn = dayIndex + 1;
    return [heading, ...day.slots.map((slot, slotIndex) => {
      const tile = element("span", `week-voxel ${slot.status}`);
      tile.dataset.slot = slot.startsAt;
      tile.style.gridColumn = dayIndex + 1;
      tile.style.gridRow = slotIndex + 2;
      const start = new Date(slot.startsAt);
      tile.title = `${formats.longDate.format(start)}, ${formats.time.format(start)} · ${labels[slot.status]}`;
      return tile;
    })];
  });
  const plane = section("week-scene-plane");
  const rows = Math.max(5, ...calendar.days.map(day => day.slots.length));
  plane.style.setProperty("--scene-rows", rows);
  plane.style.setProperty("--scene-row-gap", rows > 32 ? "0px" : rows > 10 ? "1px" : "3px");
  plane.replaceChildren(...tiles);
}

function updateSceneSelection() {
  for (const tile of section("week-scene-plane").children)
    tile.classList.toggle("selected", tile.dataset.slot === state.selected);
}

function bindWeekScene() {
  const stage = section("week-scene-stage");
  bindWeekMapTilt(stage, section("week-scene-toggle"));
  stage.addEventListener("click", event => {
    const tile = event.target.closest(".week-voxel");
    const slot = tile && allSlots().find(item => item.startsAt === tile.dataset.slot);
    if (slot && !state.busy) {
      selectSlot(slot);
      section("details-title").scrollIntoView({ block: "nearest", behavior: reducedMotion.matches ? "auto" : "smooth" });
    }
  });
}
