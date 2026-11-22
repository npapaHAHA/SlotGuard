const defaultTimeZone = "Europe/Moscow";
const browserTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone ?? defaultTimeZone;
const timeZonePreference = "slotguard.displayTimeZone";
const zoneFormatters = new Map();

function savedTimeZone() {
  try {
    const value = localStorage.getItem(timeZonePreference);
    if (value) {
      new Intl.DateTimeFormat("ru-RU", { timeZone: value });
      return value;
    }
  } catch { }
  return "";
}

function saveTimeZone(value) {
  try { localStorage.setItem(timeZonePreference, value); } catch { }
}

function timeZoneLabel(zone) {
  const moscow = zone === defaultTimeZone ? "Москва" : null;
  return moscow ?? zone.replaceAll("_", " ").replaceAll("/", " / ");
}

function fillTimeZones(select, value, automatic = false) {
  const known = typeof Intl.supportedValuesOf === "function" ? Intl.supportedValuesOf("timeZone") : [];
  const zones = [...new Set(["UTC", defaultTimeZone, browserTimeZone, "Europe/London", "Europe/Berlin",
    "America/New_York", "America/Los_Angeles", "Asia/Tokyo", "Asia/Kolkata", ...known, value].filter(Boolean))];
  const options = zones.map(zone => new Option(timeZoneLabel(zone), zone));
  if (automatic) options.unshift(new Option(`Авто · ${timeZoneLabel(browserTimeZone).split(" / ").at(-1)}`, ""));
  select.replaceChildren(...options);
  select.value = value;
}

function zoneFormats(zone) {
  if (!zoneFormatters.has(zone)) {
    const format = (locale, options) => new Intl.DateTimeFormat(locale, { ...options, timeZone: zone });
    zoneFormatters.set(zone, {
      day: format("sv-SE", { year: "numeric", month: "2-digit", day: "2-digit" }),
      date: format("ru-RU", { day: "numeric", month: "short", year: "numeric" }),
      longDate: format("ru-RU", { weekday: "long", month: "long", day: "numeric" }),
      shortDate: format("ru-RU", { weekday: "short", month: "long", day: "numeric" }),
      time: format("ru-RU", { hour: "2-digit", minute: "2-digit", hourCycle: "h23" }),
      offset: format("en-GB", { hour: "numeric", timeZoneName: "shortOffset" })
    });
  }
  return zoneFormatters.get(zone);
}

function utcOffset(value, zone) {
  return zoneFormats(zone).offset.formatToParts(new Date(value))
    .find(part => part.type === "timeZoneName").value.replace("GMT", "UTC");
}

function timeWithOffset(value, zone) {
  return `${zoneFormats(zone).time.format(new Date(value))} ${utcOffset(value, zone)}`;
}

function slotTimeRange(value, zone, endsAt) {
  const start = new Date(value);
  const end = new Date(endsAt ?? start.valueOf() + 60 * 60 * 1000);
  return utcOffset(start, zone) === utcOffset(end, zone)
    ? `${zoneFormats(zone).time.format(start)}–${timeWithOffset(end, zone)}`
    : `${timeWithOffset(start, zone)} – ${timeWithOffset(end, zone)}`;
}

function fillDurations(select, choices, duration) {
  if ([...select.options].map(option => Number(option.value)).join() !== choices.join())
    select.replaceChildren(...choices.map(minutes => new Option(`${minutes} минут`, minutes)));
  select.value = duration;
}

function durationOf(start, end) {
  return end ? Math.round((Date.parse(end) - Date.parse(start)) / 60000) : 60;
}
