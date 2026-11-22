function bindWeekMapTilt(stage, toggle) {
  let frame = 0;
  let point;

  function reset() {
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    stage.style.removeProperty("--scene-pitch");
    stage.style.removeProperty("--scene-roll");
  }

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
  reducedMotion.addEventListener("change", reset);
}
