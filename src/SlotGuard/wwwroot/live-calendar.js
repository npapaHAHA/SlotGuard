let calendarConnection = null;
let liveOrganization = null;
let liveReady = false;
let liveRefresh = 0;

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
    if (!state.busy && currentScreen() === "availability") await refreshAvailability();
    if (!state.busy && ["overview", "mine"].includes(currentScreen())) await loadWorkspace();
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
    refreshFromSignal();
  });
  connection.on("CalendarChanged", () => {
    if (calendarConnection === connection) refreshFromSignal();
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
  refreshFromSignal();
}, 5000);
