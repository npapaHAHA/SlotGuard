const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
const entryAnimations = new WeakMap();
let sceneCalendar = null;

function reveal(node, distance = 8) {
  if (reducedMotion.matches || !node.animate) return;
  entryAnimations.get(node)?.cancel();
  const animation = node.animate([
    { opacity: .4, transform: `translateY(${distance}px)` },
    { opacity: 1, transform: "translateY(0)" }
  ], { duration: 230, easing: "cubic-bezier(.2,.75,.25,1)" });
  entryAnimations.set(node, animation);
}

function renderWeekScene() {
  const scene = section("week-scene");
  const calendar = state.calendar;
  scene.hidden = !calendar;
  if (!calendar || sceneCalendar === calendar) return;

  sceneCalendar = calendar;
  const resource = state.resources.find(item => item.id === state.resourceId);
  section("week-scene-resource").textContent = resource?.name ?? "";
  const free = calendar.days.flatMap(day => day.slots).filter(slot => slot.status === "available").length;
  section("week-scene-available").textContent = `${free} свободно`;
  const tiles = calendar.days.flatMap((day, dayIndex) => day.slots.map((slot, slotIndex) => {
    const tile = element("span", `week-voxel ${slot.status}`);
    tile.dataset.slot = slot.startsAt;
    tile.style.gridColumn = dayIndex + 1;
    tile.style.gridRow = slotIndex + 1;
    return tile;
  }));
  section("week-scene-plane").replaceChildren(...tiles);
  updateSceneSelection();
}

function updateSceneSelection() {
  for (const tile of section("week-scene-plane").children)
    tile.classList.toggle("selected", tile.dataset.slot === state.selected);
}

function bindWeekScene() {
  const stage = section("week-scene-stage");
  const toggle = section("week-scene-toggle");
  let frame = 0;
  let point;
  const reset = () => {
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    stage.style.removeProperty("--scene-pitch");
    stage.style.removeProperty("--scene-roll");
  };

  toggle.addEventListener("click", () => {
    const flat = toggle.getAttribute("aria-pressed") !== "true";
    toggle.setAttribute("aria-pressed", String(flat));
    stage.classList.toggle("flat", flat);
    reset();
  });
  stage.addEventListener("pointermove", event => {
    if (event.pointerType === "touch" || reducedMotion.matches || stage.classList.contains("flat")) return;
    point = { x: event.clientX, y: event.clientY };
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      const rect = stage.getBoundingClientRect();
      stage.style.setProperty("--scene-pitch", `${(point.y - rect.top) / rect.height * 12 - 6}deg`);
      stage.style.setProperty("--scene-roll", `${(point.x - rect.left) / rect.width * 14 - 7}deg`);
    });
  });
  stage.addEventListener("pointerleave", reset);
  stage.addEventListener("click", event => {
    const tile = event.target.closest(".week-voxel");
    const slot = tile && allSlots().find(item => item.startsAt === tile.dataset.slot);
    if (slot && !state.busy) {
      selectSlot(slot);
      scrollSelectionIntoView();
    }
  });
  reducedMotion.addEventListener("change", reset);
}
