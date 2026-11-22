let calendarConnection = null;
let liveOrganization = null;
let liveReady = false;
let liveRefresh = 0;
let structureRefresh = 0;

function renderLiveStatus() {
  const badge = section("live-status");
  badge.hidden = !state.organizationId;
  badge.dataset.connected = String(liveReady);
  badge.textContent = liveReady ? "Обновления в реальном времени" : "Проверка каждые 5 с";
}

function refreshFromSignal() {
  if (liveRefresh) return;
  liveRefresh = setTimeout(async () => {
    liveRefresh = 0;
    if (!state.organizationId) return;
    if (state.busy || polling) return refreshFromSignal();
    await pollWaitlist(true);
    if (!state.busy) refreshCalendarMonth();
    if (!state.busy && currentScreen() === "availability") await refreshAvailability();
    if (!state.busy && currentScreen() === "wizard") await refreshWizard();
    if (!state.busy && ["overview", "mine", "approvals", "sessions", "integrations"].includes(currentScreen())) await loadWorkspace();
  }, 120);
}

async function refreshStructureAccess() {
  if (structureRefresh) return;
  structureRefresh = setTimeout(async () => {
    structureRefresh = 0;
    if (!state.organizationId) return;
    if (state.busy || polling) return refreshStructureAccess();
    const organization = state.organizationId;
    const session = state.session;
    try {
      await refreshReviewerAccess();
      const resources = await api("/api/resources", { cache: "no-store" }, organization);
      if (organization !== state.organizationId || session !== state.session) return;
      if (state.busy) return refreshStructureAccess();
      if (!resources.some(resource => resource.id === state.resourceId)) {
        const resource = resources[0];
        const calendar = resource ? await fetchCalendar(resource.id, undefined, organization, viewerZone, 60) : null;
        if (organization !== state.organizationId || session !== state.session) return;
        if (state.busy) return refreshStructureAccess();
        Object.assign(state, { calendar, resourceId: resource?.id ?? null, selected: null, selectedDay: null, race: null });
        if (calendar) reconcileCalendarSelection(false);
      }
      state.resources = resources;
      render();
      if (currentScreen() === "structure") await loadWorkspace();
      refreshFromSignal();
    } catch {
      refreshFromSignal();
    }
  }, 120);
}

function disconnectCalendar() {
  const previous = calendarConnection;
  calendarConnection = null;
  liveOrganization = null;
  liveReady = false;
  renderLiveStatus();
  if (previous) previous.stop().catch(() => {});
}

function connectCalendar(organization) {
  if (organization === liveOrganization && calendarConnection) return;
  disconnectCalendar();
  if (!organization || !window.signalR) return;
  liveOrganization = organization;
  const query = new URLSearchParams({ organization });
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`/hubs/calendar?${query}`)
    .withAutomaticReconnect([0, 1500, 5000, 10000])
    .build();
  calendarConnection = connection;

  connection.on("CalendarReady", () => {
    if (calendarConnection !== connection) return;
    liveReady = true;
    renderLiveStatus();
    if (isAdmin()) refreshFromSignal();
    else refreshStructureAccess();
  });
  connection.on("CalendarChanged", () => {
    if (calendarConnection === connection) refreshFromSignal();
  });
  connection.on("RoutingChanged", async () => {
    if (calendarConnection !== connection) return;
    await refreshReviewerAccess();
    refreshFromSignal();
  });
  connection.on("StructureChanged", () => {
    if (calendarConnection === connection) refreshStructureAccess();
  });
  connection.onreconnecting(() => {
    if (calendarConnection !== connection) return;
    liveReady = false;
    renderLiveStatus();
  });
  connection.onclose(() => {
    if (calendarConnection === connection) disconnectCalendar();
  });
  connection.start().catch(() => {
    if (calendarConnection === connection) disconnectCalendar();
  });
}

setInterval(() => {
  if (!state.organizationId || state.busy || liveReady) return;
  connectCalendar(state.organizationId);
  if (isAdmin()) refreshFromSignal();
  else refreshStructureAccess();
}, 5000);
