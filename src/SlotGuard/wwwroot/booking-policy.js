function noticeLength(minutes) {
  if (minutes % 1440 === 0) return `${minutes / 1440} дн.`;
  if (minutes % 60 === 0) return `${minutes / 60} ч.`;
  return `${minutes} мин.`;
}

function bookingPolicySummary(policy) {
  if (!policy) return "";
  const { minimumNoticeMinutes, bookingWindowDays, maxActiveBookings, cancellationNoticeMinutes, rescheduleNoticeMinutes } = policy;
  return [
    minimumNoticeMinutes && `Запись минимум за ${noticeLength(minimumNoticeMinutes)}`,
    bookingWindowDays && `На ${bookingWindowDays} дн. вперёд`,
    maxActiveBookings && `До ${maxActiveBookings} ${maxActiveBookings % 10 === 1 && maxActiveBookings % 100 !== 11 ? "активной брони" : "активных броней"}`,
    cancellationNoticeMinutes && `Отмена за ${noticeLength(cancellationNoticeMinutes)}`,
    rescheduleNoticeMinutes && `Перенос за ${noticeLength(rescheduleNoticeMinutes)}`
  ].filter(Boolean).join(" · ");
}

function bookingDeadlines(changes, startsAt, zone) {
  if (!changes) return "";
  const start = Date.parse(startsAt);
  const format = value => `${zoneFormats(zone).shortDate.format(new Date(value))}, ${timeWithOffset(value, zone)}`;
  const cancel = Date.parse(changes.cancelUntil) < start;
  const move = Date.parse(changes.rescheduleUntil) < start;
  if (cancel && move && changes.cancelUntil === changes.rescheduleUntil)
    return `Отмена и перенос до ${format(changes.cancelUntil)}`;
  return [cancel && `Отмена до ${format(changes.cancelUntil)}`, move && `Перенос до ${format(changes.rescheduleUntil)}`]
    .filter(Boolean).join(" · ");
}
