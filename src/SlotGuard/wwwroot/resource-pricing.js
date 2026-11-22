const pricingDialog = section("pricing-dialog");
const pricingForm = section("pricing-form");
let priceEditor;

function editResourcePrice(resource) {
  priceEditor = { resource, organization: state.organizationId };
  section("pricing-resource").textContent = resource.name;
  pricingForm.elements.amount.value = resource.priceMinor == null ? "" : (resource.priceMinor / 100).toFixed(2);
  section("pricing-error").hidden = true;
  pricingDialog.showModal();
}

section("pricing-cancel").addEventListener("click", () => pricingDialog.close());
pricingForm.addEventListener("submit", async event => {
  event.preventDefault();
  const editor = priceEditor;
  const button = section("pricing-save");
  const value = pricingForm.elements.amount.value;
  const priceMinor = value === "" ? null : Math.round(Number(value) * 100);
  const message = section("pricing-error");
  message.hidden = true;
  button.disabled = true;
  try {
    await api(`/api/workspace/resources/${encodeURIComponent(editor.resource.id)}/price`,
      jsonRequest("PUT", { priceMinor }), editor.organization);
    if (editor !== priceEditor || state.organizationId !== editor.organization) return;
    pricingDialog.close();
    await loadWorkspace();
  } catch (error) {
    if (editor === priceEditor && pricingDialog.open) formProblem(message, error);
  } finally {
    button.disabled = false;
  }
});
