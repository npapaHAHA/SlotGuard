const accountModes = {
  login: ["Войти в SlotGuard", "Откройте своё расписание и команду.", "Войти"],
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
  if (["register", "login", "forgot", "resend"].includes(mode)) addField("Email", "email", "email", "email", 254);
  if (["register", "login", "reset"].includes(mode)) {
    addField("Пароль", "password", "password", mode === "login" ? "current-password" : "new-password", 128, 10);
    if (mode !== "login") fields.append(element("small", "", "От 10 символов, включая заглавную и строчную буквы, цифру и спецсимвол."));
  }
  const actions = mode === "login"
    ? [["Создать аккаунт", "register"], ["Забыли пароль?", "forgot"], ["Отправить подтверждение снова", "resend"]]
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
    if (mode === "login") {
      const session = await api("/api/account/login", jsonRequest("POST", data));
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
    row(item.displayName, item.email, item.role === "admin" ? "Администратор" : "Участник")));
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
    const data = Object.fromEntries(new FormData(form));
    const message = section("team-message");
    run(async () => {
      message.hidden = true;
      try {
        await api("/api/team/invitations", jsonRequest("POST", data));
        form.reset();
        message.textContent = "Письмо отправлено.";
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
