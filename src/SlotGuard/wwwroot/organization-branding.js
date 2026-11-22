const orgBrandingAddress = "/api/workspace/branding";
const orgBrandingInputs = ["name", "logoUrl", "bannerUrl", "palette", "completionLabel", "completionUrl",
  "senderName", "replyTo", "smtpProfile"];

function brandingInput() {
  const form = section("branding-form");
  return Object.fromEntries(orgBrandingInputs.map(name => [name, form.elements[name].value.trim()]));
}

function previewOrganizationBranding() {
  const input = brandingInput();
  applyBookingBrand(section("branding-preview"), section("branding-preview-name"),
    section("branding-preview-logo"), section("branding-preview-banner"), input);
  const completion = section("branding-preview-completion");
  completion.hidden = !input.completionLabel || !bookingBrandUrl(input.completionUrl);
  completion.textContent = completion.hidden ? "" : `После подтверждения · ${input.completionLabel}`;
}

function renderOrganizationBranding({ settings, profiles }) {
  const smtp = section("branding-smtp");
  smtp.replaceChildren(new Option("По умолчанию", ""));
  for (const profile of profiles) smtp.add(new Option(profile.name, profile.id));
  if (settings.smtpProfile && !profiles.some(profile => profile.id === settings.smtpProfile))
    smtp.add(new Option("Профиль недоступен", settings.smtpProfile));
  const form = section("branding-form");
  for (const name of orgBrandingInputs) form.elements[name].value = settings[name] ?? "";
  section("branding-fields").disabled = false;
  previewOrganizationBranding();
}

function resetOrganizationBranding() {
  section("branding-form").reset();
  section("branding-fields").disabled = true;
  section("branding-message").hidden = true;
  previewOrganizationBranding();
}

section("branding-form").addEventListener("input", previewOrganizationBranding);
section("branding-form").addEventListener("submit", async event => {
  event.preventDefault();
  const organization = state.organizationId;
  const payload = brandingInput();
  const fields = section("branding-fields");
  const message = section("branding-message");
  fields.disabled = true;
  message.hidden = true;
  try {
    await api(orgBrandingAddress, jsonRequest("PUT", payload), organization);
    if (state.organizationId !== organization) return;
    message.textContent = "Настройки сохранены.";
    message.classList.remove("error");
    message.hidden = false;
  } catch (error) {
    if (state.organizationId === organization) formProblem(message, error);
  } finally {
    if (state.organizationId === organization) fields.disabled = false;
  }
});
