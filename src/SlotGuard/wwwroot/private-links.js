function editPrivateLinks(resource) {
  const organization = state.organizationId;
  const path = `/api/workspace/resources/${encodeURIComponent(resource.id)}/private-links`;
  return run(async () => {
    const existing = await api(path, undefined, organization);
    openPrivateLinkEditor(resource, existing, {
      create: payload => api(path, jsonRequest("POST", payload), organization),
      revoke: id => api(`${path}/${id}`, { method: "DELETE" }, organization),
      reload: () => api(path, undefined, organization)
    });
  });
}

function openPrivateLinkEditor(resource, initial, actions) {
  const dialog = document.createElement("dialog");
  dialog.className = "panel login-dialog private-links-dialog";
  dialog.setAttribute("aria-labelledby", "private-links-title");
  dialog.innerHTML = `
    <header class="private-links-heading">
      <div><h2 id="private-links-title">Приватные ссылки</h2><p data-resource></p></div>
      <button type="button" class="icon-button today-button" data-close aria-label="Закрыть">×</button>
    </header>
    <form class="workspace-form private-link-form">
      <div class="form-pair">
        <label>Действует до<input type="datetime-local" name="expires" required></label>
        <label>Лимит бронирований<input type="number" name="maxUses" min="1" max="1000" value="1" required></label>
      </div>
      <label>Домен email <small>необязательно</small>
        <input type="text" name="emailDomain" maxlength="253" placeholder="example.com" autocapitalize="none" spellcheck="false">
      </label>
      <p class="private-link-hint">При указанном домене гость подтверждает рабочий email. Срок указан по времени устройства.</p>
      <button type="submit" class="page-action">Создать ссылку</button>
    </form>
    <p data-message class="form-message" role="status" hidden></p>
    <div class="private-link-list" data-links></div>`;
  dialog.querySelector("[data-resource]").textContent = resource.name;
  const form = dialog.querySelector("form");
  const list = dialog.querySelector("[data-links]");
  const message = dialog.querySelector("[data-message]");
  const close = dialog.querySelector("[data-close]");
  let working = false;

  const now = new Date();
  const expires = new Date(now.valueOf() + 7 * 86400000);
  const local = date => new Date(date.valueOf() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
  form.elements.expires.min = local(now);
  form.elements.expires.value = local(expires);

  function notice(text, error = false) {
    message.textContent = text;
    message.hidden = !text;
    message.classList.toggle("error", error);
  }

  function busy(value) {
    working = value;
    close.disabled = value;
    for (const control of form.elements) control.disabled = value;
    for (const control of list.querySelectorAll("button")) control.disabled = value;
  }

  const labels = { active: "Активна", revoked: "Отозвана", expired: "Истекла", used: "Лимит исчерпан" };
  const formatExpiry = new Intl.DateTimeFormat("ru-RU", { dateStyle: "medium", timeStyle: "short" });
  function render(links) {
    if (!links.length) {
      list.replaceChildren(element("p", "private-link-hint", "Приватных ссылок пока нет."));
      return;
    }
    list.replaceChildren(...links.map(link => {
      const row = element("article", "private-link-row");
      const title = element("div", "private-link-row-title");
      title.append(element("strong", "", link.emailDomain || "Доступ по ссылке"),
        element("span", `pill ${link.status === "active" ? "available" : "past"}`, labels[link.status]));
      const counter = element("p", "private-link-hint",
        `${link.usedCount} из ${link.maxUses} использовано · до ${formatExpiry.format(new Date(link.expiresAt))}`);
      row.append(title, counter);
      if (link.status === "active") {
        const url = new URL(`/book/${link.id}`, location.origin).href;
        const field = element("input", "private-link-url");
        Object.assign(field, { type: "text", value: url, readOnly: true });
        field.setAttribute("aria-label", "Адрес приватной ссылки");
        const controls = element("div", "private-link-actions");
        const open = element("a", "outline-action", "Открыть");
        Object.assign(open, { href: url, target: "_blank", rel: "noopener noreferrer" });
        const copy = button("outline-action", "Скопировать", async () => {
          try {
            await navigator.clipboard.writeText(url);
            notice("Приватная ссылка скопирована.");
          } catch {
            field.focus();
            field.select();
            notice("Скопируйте выделенный адрес.");
          }
        });
        const revoke = button("outline-action private-link-revoke", "Отозвать", async () => {
          if (working || !window.confirm("Отозвать эту ссылку для новых бронирований?")) return;
          busy(true);
          try {
            await actions.revoke(link.id);
            render(await actions.reload());
            notice("Ссылка отозвана.");
          } catch (error) {
            notice(error.message, true);
          } finally {
            busy(false);
          }
        });
        controls.append(open, copy, revoke);
        row.append(field, controls);
      }
      return row;
    }));
  }

  close.addEventListener("click", () => dialog.close());
  dialog.addEventListener("cancel", event => { if (working) event.preventDefault(); });
  dialog.addEventListener("close", () => dialog.remove(), { once: true });
  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (working) return;
    const fields = form.elements;
    const expiration = new Date(fields.expires.value);
    if (!Number.isFinite(expiration.valueOf())) {
      notice("Укажите дату и время окончания доступа.", true);
      return;
    }
    const payload = {
      expiresAt: expiration.toISOString(), maxUses: Number(fields.maxUses.value), emailDomain: fields.emailDomain.value.trim()
    };
    busy(true);
    notice("");
    try {
      await actions.create(payload);
      render(await actions.reload());
      notice("Приватная ссылка создана.");
    } catch (error) {
      notice(error.message, true);
    } finally {
      busy(false);
    }
  });
  render(initial);
  document.body.append(dialog);
  dialog.showModal();
}
