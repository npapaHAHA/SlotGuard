const securityAddress = "/api/account/security";
const securityDialog = section("account-security-dialog");
const securityForm = section("account-security-form");
const securityFields = section("account-security-fields");
const securitySubmit = section("account-security-submit");
const securityMessage = section("account-security-message");
let securityPhase, setupToken, securityPending = false;

function securityNotice(text, isError = false) {
  securityMessage.textContent = text;
  securityMessage.classList.toggle("error", isError);
  securityMessage.hidden = !text;
}

function securityField(label, name, options) {
  const field = element("label", "", label);
  field.append(Object.assign(element("input"), { name, required: true, ...options }));
  return field;
}

function securityPassword() {
  return securityField("Текущий пароль", "password", { type: "password", minLength: 10, maxLength: 128,
    autocomplete: "current-password" });
}

function securityCode() {
  return securityField("Код", "code", { maxLength: 40, autocomplete: "one-time-code" });
}

function securityView(phase, label, ...children) {
  securityPhase = phase;
  securityFields.replaceChildren(...children);
  securitySubmit.textContent = label ?? "";
  securitySubmit.hidden = !label;
  securityNotice("");
}

function showSecurityStatus(status) {
  if (!status.enabled) {
    securityView("setup", "Получить ключ", element("p", "security-caption",
      "Подключите приложение-аутентификатор. При каждом входе потребуется новый код."), securityPassword());
    return;
  }
  const actions = element("div", "security-actions");
  actions.append(button("outline-action", "Обновить коды восстановления", () => showSecurityChange("recovery-codes")),
    button("outline-action", "Отключить 2FA", () => showSecurityChange("disable")));
  securityView(null, null, element("strong", "security-on", "2FA включена"),
    element("p", "security-caption", `Действующих кодов восстановления: ${status.recoveryCount}.`), actions);
}

function showSecurityChange(phase) {
  const changingCodes = phase === "recovery-codes";
  const proof = element("label", "", "Способ подтверждения");
  const select = Object.assign(element("select"), { name: "proof" });
  select.add(new Option("Код из приложения", "app"));
  select.add(new Option("Код восстановления", "recovery"));
  proof.append(select);
  securityView(phase, changingCodes ? "Выпустить новые коды" : "Отключить 2FA",
    element("p", "security-caption", changingCodes ? "Новый набор заменит все действующие коды."
      : "После отключения для входа будет достаточно пароля."),
    securityPassword(), proof, securityCode());
}

function copySecurityValue(text, label) {
  return button("outline-action", label, async () => {
    try {
      await navigator.clipboard.writeText(text);
      securityNotice("Скопировано.");
    } catch {
      securityNotice("Выделите значение и скопируйте его вручную.");
    }
  });
}

function showSecuritySetup(data) {
  setupToken = data.token;
  const key = element("code", "security-key", data.key);
  const actions = element("div", "security-actions");
  actions.append(copySecurityValue(data.key, "Скопировать ключ"));
  if (data.uri?.startsWith("otpauth://totp/SlotGuard:")) {
    const app = element("a", "outline-action", "Открыть приложение");
    app.href = data.uri;
    actions.append(app);
  }
  securityView("enable", "Включить 2FA",
    element("p", "security-caption", "Добавьте ключ в приложение как код по времени, затем введите шестизначный код."),
    key, actions, securityField("Код из приложения", "code", { maxLength: 8, inputMode: "numeric", autocomplete: "one-time-code" }));
}

function showRecoveryCodes(codes) {
  const list = element("ul", "security-recovery-codes");
  for (const code of codes) {
    const item = element("li");
    item.append(element("code", "", code));
    list.append(item);
  }
  securityView(null, null, element("strong", "security-on", "2FA включена"),
    element("p", "security-caption", "Сохрани коды в безопасном месте. Каждый действует один раз и показывается только сейчас."),
    list, copySecurityValue(codes.join("\n"), "Скопировать коды"));
}

async function withSecurityRequest(action) {
  if (securityPending) return;
  securityPending = true;
  securitySubmit.disabled = true;
  section("account-security-close").disabled = true;
  securityNotice("");
  try { await action(); }
  catch (error) { securityNotice(error.message, true); }
  finally {
    securityPending = false;
    securitySubmit.disabled = false;
    section("account-security-close").disabled = false;
  }
}

section("account-security-button").addEventListener("click", () => {
  securityView(null, null, element("p", "security-caption", "Загрузка настроек…"));
  securityDialog.showModal();
  withSecurityRequest(async () => showSecurityStatus(await api(securityAddress)));
});
section("account-security-close").addEventListener("click", () => securityDialog.close());
securityDialog.addEventListener("cancel", event => { if (securityPending) event.preventDefault(); });
securityDialog.addEventListener("close", () => {
  setupToken = null;
  securityView(null, null);
});
securityForm.addEventListener("submit", event => {
  event.preventDefault();
  if (!securityPhase) return;
  const phase = securityPhase;
  const input = Object.fromEntries(new FormData(securityForm));
  if (phase === "enable") input.token = setupToken;
  if (input.proof) input.recovery = input.proof === "recovery";
  withSecurityRequest(async () => {
    const result = await api(`${securityAddress}/${phase}`, jsonRequest("POST", input));
    if (phase === "setup") return showSecuritySetup(result);
    setupToken = null;
    if (result.recoveryCodes.length) showRecoveryCodes(result.recoveryCodes);
    else showSecurityStatus(result);
  });
});
