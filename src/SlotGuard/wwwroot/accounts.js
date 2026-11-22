const accountModes = {
  login: ["Войти в SlotGuard", "Откройте своё расписание и команду.", "Войти"],
  factor: ["Подтвердите вход", "Введите новый код из приложения-аутентификатора.", "Подтвердить вход"],
  recovery: ["Вход с кодом восстановления", "Введите один из сохранённых одноразовых кодов.", "Подтвердить вход"],
  register: ["Создать аккаунт", "Пришлём письмо для подтверждения адреса.", "Зарегистрироваться"],
  forgot: ["Восстановить пароль", "Отправим ссылку на подтверждённый email.", "Отправить письмо"],
  resend: ["Подтверждение email", "Отправим письмо повторно.", "Отправить письмо"],
  confirm: ["Подтверждение email", "После подтверждения можно войти в аккаунт.", "Подтвердить email"],
  reset: ["Новый пароль", "Задайте пароль для входа в SlotGuard.", "Сохранить пароль"]
};
const accountLink = new URLSearchParams(location.search);
let inviteToken = sessionStorage.getItem("slotguard.invitation");
if (accountLink.get("action") === "invite" && accountLink.get("token")?.length === 43) {
  inviteToken = accountLink.get("token");
  sessionStorage.setItem("slotguard.invitation", inviteToken);
}
if (accountLink.has("action")) history.replaceState(history.state, "", location.pathname + location.hash);
let accountMode = "login";
let inviteError = "";

function showAccount(mode) {
  accountMode = mode;
  const [title, description, submit] = accountModes[mode];
  section("account-title").textContent = title;
  section("account-description").textContent = description;
  section("account-submit").textContent = submit;
  section("account-message").hidden = true;
  const fields = section("account-fields");
  const email = fields.querySelector('[name="email"]')?.value ?? "";
  fields.replaceChildren();
  const addField = (label, name, type, autocomplete, maxLength, minLength = 0) => {
    const row = element("label", "", label);
    const input = Object.assign(element("input", ""), { name, type, autocomplete, maxLength, minLength, required: true });
    if (name === "email") input.value = email;
    row.append(input);
    fields.append(row);
  };
  if (mode === "register") addField("Имя", "name", "text", "name", 100);
  if (mode === "factor" || mode === "recovery") {
    addField(mode === "factor" ? "Код из приложения" : "Код восстановления", "code", "text", "one-time-code", mode === "factor" ? 8 : 40);
    if (mode === "factor") fields.querySelector("input").inputMode = "numeric";
  }
  if (["register", "login", "forgot", "resend"].includes(mode)) addField("Email", "email", "email", "email", 254);
  if (["register", "login", "reset"].includes(mode)) {
    addField("Пароль", "password", "password", mode === "login" ? "current-password" : "new-password", 128, 10);
    if (mode !== "login") fields.append(element("small", "", "От 10 символов, включая заглавную и строчную буквы, цифру и спецсимвол."));
  }
  const actions = mode === "login"
    ? [["Создать аккаунт", "register"], ["Забыли пароль?", "forgot"], ["Отправить подтверждение снова", "resend"]]
    : mode === "factor" ? [["Использовать код восстановления", "recovery"], ["Вернуться ко входу", "login"]]
    : mode === "recovery" ? [["Код из приложения", "factor"], ["Вернуться ко входу", "login"]]
    : [["Вернуться ко входу", "login"]];
  section("account-links").replaceChildren(...actions.map(([label, next]) =>
    button("account-link", label, () => showAccount(next))));
  if (!section("account-dialog").open) section("account-dialog").showModal();
  (fields.querySelector("input") ?? section("account-submit")).focus();
}

async function submitAccount(data) {
  const mode = accountMode;
  const message = section("account-message");
  message.hidden = true;
  try {
    if (mode === "login" || mode === "factor" || mode === "recovery") {
      const session = mode === "login" ? await api("/api/account/login", jsonRequest("POST", data))
        : await api("/api/account/login/factor", jsonRequest("POST", { code: data.code, recovery: mode === "recovery" }));
      if (session.requiresTwoFactor) {
        showAccount("factor");
        return;
      }
      section("account-dialog").close();
      await enterSession(session);
      return;
    }
    const endpoint = mode === "forgot" || mode === "resend" ? "email" : mode;
    const payload = { ...data };
    if (endpoint === "email") payload.purpose = mode === "forgot" ? "reset" : "confirm";
    if (mode === "confirm" || mode === "reset") {
      payload.token = accountLink.get("token");
      payload.userId = accountLink.get("userId");
    }
    await api(`/api/account/${endpoint}`, jsonRequest("POST", payload));
    showAccount("login");
    message.classList.remove("error");
    message.textContent = mode === "confirm" ? "Адрес подтверждён. Войдите в аккаунт."
      : mode === "reset" ? "Пароль сохранён. Войдите с новым паролем."
      : "Проверьте входящие письма. При необходимости запросите ссылку снова.";
    message.hidden = false;
  } catch (error) {
    formProblem(message, error);
  }
}

function renderAccountControls() {
  const { session, busy } = state;
  section("account-button").hidden = !session || session.isDemo;
  section("account-button").textContent = session?.isAccount ? "Выйти" : "Войти";
  section("account-button").disabled = busy;
  section("p3-new-room").disabled = busy || !session?.isAccount && !session?.isDemo;
  section("account-security-button").hidden = !session?.isAccount;
  section("account-security-button").disabled = busy;
  section("create-organization").hidden = !session?.isAccount || !state.organizations.length;
  section("create-organization").disabled = busy;
  section("organization-form").querySelector('[type="submit"]').disabled = busy;
  section("account-submit").disabled = busy;
  section("team-invite-form").querySelector("button").disabled = busy;
  section("invite-banner").hidden = !inviteToken;
  section("accept-invite").disabled = busy || !session;
  section("accept-invite").textContent = session?.isAccount ? "Принять" : "Войти для принятия";
  section("invite-message").textContent = inviteError || "Ссылка из письма откроет доступ к расписанию команды.";
}

function renderTeam(team) {
  section("team-organization").textContent = state.organizations.find(item => item.id === state.organizationId)?.name ?? "";
  section("team-count").textContent = team.members.length;
  const row = (name, email, label) => {
    const item = element("div", "data-row team-row");
    const identity = element("div", "team-identity");
    const text = element("div", "data-title");
    text.append(element("strong", "", name));
    if (email) text.append(element("span", "", email));
    identity.append(element("span", "team-avatar", name.slice(0, 1).toUpperCase()), text);
    item.append(identity, element("span", "guest", label));
    return item;
  };
  section("team-members").replaceChildren(...team.members.map(item =>
    row(item.displayName, item.email, organizationRoleLabel(item.role))));
  section("team-invitations").replaceChildren(...(team.invitations.length ? team.invitations.map(item =>
    row(item.email, "", `До ${appointmentTime(item.expiresAt)}`))
    : [element("p", "list-empty", "Приглашений без ответа нет.")]));
}

function clearInvitation() {
  sessionStorage.removeItem("slotguard.invitation");
  inviteToken = null;
  inviteError = "";
  renderAccountControls();
}

function bindAccounts() {
  for (const close of document.querySelectorAll("[data-close-dialog]"))
    close.addEventListener("click", () => close.closest("dialog").close());
  section("account-button").addEventListener("click", () => {
    if (!state.session?.isAccount) return showAccount("login");
    run(async () => {
      await enterSession(await api("/api/session/guest", { method: "POST" }));
      location.hash = "overview";
    });
  });
  section("account-form").addEventListener("submit", event => {
    event.preventDefault();
    const data = Object.fromEntries(new FormData(event.currentTarget));
    run(() => submitAccount(data));
  });
  for (const id of ["create-organization", "welcome-create"]) {
    section(id).addEventListener("click", () => {
      section("organization-form").reset();
      section("organization-error").hidden = true;
      section("organization-dialog").showModal();
    });
  }
  section("organization-form").addEventListener("submit", event => {
    event.preventDefault();
    const data = Object.fromEntries(new FormData(event.currentTarget));
    run(async () => {
      try {
        const organization = await api("/api/account/organizations", jsonRequest("POST", data));
        section("organization-dialog").close();
        location.hash = "manage";
        await enterSession(state.session, organization.id);
      } catch (error) {
        formProblem(section("organization-error"), error);
      }
    });
  });
  section("team-invite-form").addEventListener("submit", event => {
    event.preventDefault();
    const form = event.currentTarget;
    const emails = form.elements.emails.value.split("\n").map(email => email.trim()).filter(Boolean);
    const message = section("team-message");
    const results = section("team-invite-results");
    run(async () => {
      message.hidden = true;
      results.replaceChildren();
      try {
        const delivery = await api("/api/team/invitations/batch", jsonRequest("POST", { emails }));
        const labels = { sent: "Отправлено", member: "Уже в команде", pending: "Ожидает ответа", failed: "Повторите отправку" };
        results.replaceChildren(...delivery.map(item => {
          const row = element("li", "", item.email);
          row.append(element("span", item.status === "failed" ? "error" : "", labels[item.status]));
          return row;
        }));
        form.elements.emails.value = delivery.filter(item => item.status === "failed").map(item => item.email).join("\n");
        message.textContent = `Отправлено писем: ${delivery.filter(item => item.status === "sent").length}.`;
        message.classList.remove("error");
        message.hidden = false;
        await loadWorkspace();
      } catch (error) {
        formProblem(message, error);
      }
    });
  });
  section("dismiss-invite").addEventListener("click", clearInvitation);
  section("accept-invite").addEventListener("click", () => {
    if (!state.session?.isAccount) return showAccount("login");
    run(async () => {
      try {
        const { organizationId } = await api("/api/account/invitations/accept", jsonRequest("POST", { token: inviteToken }));
        clearInvitation();
        location.hash = "calendar";
        await enterSession(state.session, organizationId);
      } catch (error) {
        inviteError = error.message;
      }
    });
  });
  const action = accountLink.get("action");
  if (action === "confirm" || action === "reset") showAccount(action);
}
