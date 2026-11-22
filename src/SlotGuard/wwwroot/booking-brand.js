"use strict";

const bookingPalettes = ["forest", "ocean", "plum", "graphite"];
const slotGuardMark = "/design/slotguard-mark.svg";

function bookingBrandUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === "https:" && !url.username && !url.password ? url.href : null;
  } catch {
    return null;
  }
}

function bookingBrandImage(image, value, fallback = null) {
  const source = bookingBrandUrl(value) ?? fallback;
  if (image.dataset.brandSource === (source ?? "")) return;
  image.dataset.brandSource = source ?? "";
  image.onload = () => { image.hidden = false; };
  image.onerror = () => {
    if (fallback && image.getAttribute("src") !== fallback) image.src = fallback;
    else image.hidden = true;
  };
  if (source) {
    if (!fallback) image.hidden = true;
    image.src = source;
  } else {
    image.removeAttribute("src");
    image.hidden = true;
  }
}

function applyBookingBrand(surface, name, logo, banner, brand) {
  surface.dataset.palette = bookingPalettes.includes(brand?.palette) ? brand.palette : "forest";
  name.textContent = brand?.name?.trim() || "SlotGuard";
  bookingBrandImage(logo, brand?.logoUrl, slotGuardMark);
  bookingBrandImage(banner, brand?.bannerUrl);
}
