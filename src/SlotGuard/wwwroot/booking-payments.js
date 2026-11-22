const rubles = new Intl.NumberFormat("ru-RU", { style: "currency", currency: "RUB" });
function bookingPrice(amountMinor) {
  return rubles.format(amountMinor / 100);
}

function bookingPaymentLabel(payment) {
  if (!payment) return "";
  const amount = bookingPrice(payment.amountMinor);
  return {
    awaiting_payment: `К оплате ${amount}`,
    paid: `Демо-оплата ${amount}`,
    refunded: `Демо-возврат ${amount}`
  }[payment.status] ?? "";
}
