const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
const entryAnimations = new WeakMap();

function reveal(node, distance = 4) {
  if (reducedMotion.matches || !node?.animate) return;
  entryAnimations.get(node)?.cancel();
  const animation = node.animate([
    { opacity: .75, transform: `translateY(${distance}px)` },
    { opacity: 1, transform: "translateY(0)" }
  ], { duration: 180, easing: "cubic-bezier(.2,.75,.25,1)" });
  entryAnimations.set(node, animation);
}
