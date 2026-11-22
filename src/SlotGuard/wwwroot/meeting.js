"use strict";
const $ = id => document.getElementById(id);
const token = location.hash.slice(1);
const joinForm = $("meeting-join");
const chatForm = $("meeting-chat");
const notes = $("meeting-notes");
let call, local, peer, ice = [], settings, sendQueue = Promise.resolve(), recorder, recognizer;
const Speech = window.SpeechRecognition || window.webkitSpeechRecognition;
const noteKey = `slotguard.meeting.notes:${token}`;
try { if (token) notes.value = sessionStorage.getItem(noteKey) ?? ""; } catch {}
notes.addEventListener("input", () => {
  try { if (token) sessionStorage.setItem(noteKey, notes.value); } catch {}
});
$("meeting-dictation").hidden = !Speech;
$("meeting-record").hidden = !window.MediaRecorder;

function status(text) { $("meeting-status").textContent = text; }
function saveMeetingFile(blob, filename) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  Object.assign(anchor, { href: url, download: filename });
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
}

function resetPeer() {
  peer?.close();
  peer = null;
  ice = [];
  $("meeting-remote").srcObject = null;
}

function connection() {
  if (peer) return peer;
  const rtc = new RTCPeerConnection(settings?.stun ? { iceServers: [{ urls: settings.stun }] } : {});
  peer = rtc;
  const remote = new MediaStream();
  $("meeting-remote").srcObject = remote;
  for (const track of local.getTracks()) rtc.addTrack(track, local);
  rtc.ontrack = event => remote.addTrack(event.track);
  rtc.onicecandidate = event => {
    if (event.candidate && call?.state === signalR.HubConnectionState.Connected)
      call.invoke("Signal", "ice", JSON.stringify(event.candidate.toJSON())).catch(() => {});
  };
  rtc.onconnectionstatechange = () => {
    if (peer !== rtc) return;
    if (rtc.connectionState === "connected") status("Связь установлена.");
    if (rtc.connectionState === "failed") status("Медиасоединение недоступно. Переподключитесь к комнате.");
  };
  return rtc;
}

function enqueue(work) {
  sendQueue = sendQueue.then(work).catch(() => status("Не удалось установить связь."));
}
async function sendDescription(rtc) {
  const description = rtc.localDescription;
  await call.invoke("Signal", description.type, JSON.stringify(description.toJSON()));
}
async function receive(type, payload) {
  const rtc = connection();
  const value = JSON.parse(payload);
  if (type === "ice") {
    if (!rtc.remoteDescription) ice.push(value);
    else await rtc.addIceCandidate(value);
    return;
  }
  if (type === "answer" && rtc.signalingState !== "have-local-offer") return;
  await rtc.setRemoteDescription(value);
  for (const candidate of ice.splice(0)) await rtc.addIceCandidate(candidate);
  if (type === "offer") {
    await rtc.setLocalDescription(await rtc.createAnswer());
    await sendDescription(rtc);
  }
}

function toggleCall(active) {
  joinForm.hidden = active;
  $("meeting-video").hidden = !active;
  chatForm.querySelector("button").disabled = !active;
}

async function leave(text = "Вы вышли из комнаты.") {
  const hub = call;
  call = null;
  if (recorder?.state === "recording") recorder.stop();
  recognizer?.abort();
  resetPeer();
  local?.getTracks().forEach(track => track.stop());
  local = null;
  $("meeting-local").srcObject = null;
  toggleCall(false);
  status(text);
  if (hub) await hub.stop().catch(() => {});
}

joinForm.addEventListener("submit", async event => {
  event.preventDefault();
  if (call || !token) return;
  const submit = joinForm.querySelector("button");
  const data = new FormData(joinForm);
  submit.disabled = true;
  status("Подключение…");
  try {
    if (!navigator.mediaDevices?.getUserMedia || typeof RTCPeerConnection !== "function")
      throw new Error("Для звонка нужен браузер с WebRTC и HTTPS или localhost.");
    local = await navigator.mediaDevices.getUserMedia({ audio: true, video: !data.has("audioOnly") });
    $("meeting-local").srcObject = local;
    const hub = new signalR.HubConnectionBuilder().withUrl("/hubs/meeting").build();
    call = hub;
    let ready;
    hub.on("Signal", (type, payload) => enqueue(async () => { await ready; if (call === hub) await receive(type, payload); }));
    hub.on("PeerJoined", () => enqueue(async () => {
      await ready;
      if (call !== hub) return;
      resetPeer();
      const rtc = connection();
      await rtc.setLocalDescription(await rtc.createOffer());
      await sendDescription(rtc);
      status("Соединение с собеседником…");
    }));
    hub.on("PeerLeft", () => enqueue(async () => { resetPeer(); status("Собеседник вышел."); }));
    hub.on("Chat", (name, text) => {
      const item = document.createElement("li");
      item.className = "p3-result";
      item.textContent = `${name}: ${text}`;
      const messages = $("meeting-messages");
      messages.append(item);
      if (messages.children.length > 100) messages.firstElementChild.remove();
      item.scrollIntoView({ block: "nearest" });
    });
    hub.onclose(() => { if (call === hub) leave("Соединение закрыто. Подключитесь снова."); });
    await hub.start();
    ready = hub.invoke("Join", token, data.get("displayName"));
    settings = await ready;
    $("meeting-mic").setAttribute("aria-pressed", "true");
    $("meeting-camera").hidden = !local.getVideoTracks().length;
    $("meeting-camera").setAttribute("aria-pressed", "true");
    toggleCall(true);
    status(settings.peer ? "Соединение с собеседником…" : "Ожидаем собеседника.");
  } catch (error) {
    await leave(error.name === "NotAllowedError" ? "Разрешите доступ к микрофону и камере." : error.message);
  } finally { submit.disabled = false; }
});

for (const [control, kind] of [["meeting-mic", "audio"], ["meeting-camera", "video"]]) {
  $(control).addEventListener("click", () => {
    const enabled = $(control).getAttribute("aria-pressed") === "true";
    local?.getTracks().filter(track => track.kind === kind).forEach(track => { track.enabled = !enabled; });
    $(control).setAttribute("aria-pressed", String(!enabled));
  });
}
$("meeting-leave").addEventListener("click", () => leave());
chatForm.addEventListener("submit", async event => {
  event.preventDefault();
  if (!call) return;
  const field = chatForm.elements.text;
  try { await call.invoke("Chat", field.value); field.value = ""; }
  catch { status("Сообщение не отправлено. Повторите через секунду."); }
});
$("meeting-copy").addEventListener("click", async () => {
  try { await navigator.clipboard.writeText(location.href); status("Ссылка скопирована."); }
  catch { status("Скопируйте ссылку из адресной строки."); }
});
$("meeting-save-notes").addEventListener("click", () =>
  saveMeetingFile(new Blob([notes.value], { type: "text/plain;charset=utf-8" }), "slotguard-notes.txt"));

$("meeting-record").addEventListener("click", () => {
  if (recorder?.state === "recording") { recorder.stop(); return; }
  if (!local) return;
  try {
    const chunks = [];
    let total = 0;
    const capture = new MediaRecorder(local);
    recorder = capture;
    capture.ondataavailable = event => {
      if (event.data.size) chunks.push(event.data);
      total += event.data.size;
      if (total > 100 * 1024 * 1024 && capture.state === "recording") capture.stop();
    };
    capture.onstop = () => {
      const file = new Blob(chunks, { type: capture.mimeType });
      if (file.size) saveMeetingFile(file, `slotguard-local.${capture.mimeType.includes("mp4") ? "mp4" : "webm"}`);
      $("meeting-record").textContent = "Записать мою дорожку";
    };
    capture.start(1000);
    $("meeting-record").textContent = "Остановить и скачать";
  } catch { status("Браузер не может записать эту дорожку."); }
});

$("meeting-dictation").addEventListener("click", () => {
  if (recognizer) { recognizer.stop(); return; }
  if (!window.confirm("Распознавание выполняет браузер. Он может использовать свой сетевой сервис. Начать диктовку?")) return;
  const speech = new Speech();
  recognizer = speech;
  speech.lang = "ru-RU";
  speech.continuous = true;
  speech.interimResults = true;
  speech.onresult = event => {
    const caption = $("meeting-caption");
    caption.hidden = false;
    caption.textContent = Array.from(event.results).at(-1)?.[0].transcript ?? "";
    for (let index = event.resultIndex; index < event.results.length; index++) {
      const result = event.results[index];
      if (result.isFinal) notes.value = (notes.value + "\n" + result[0].transcript).trim().slice(0, 40000);
    }
    notes.dispatchEvent(new Event("input"));
  };
  speech.onerror = () => status("Диктовка недоступна в этом браузере.");
  speech.onend = () => { recognizer = null; $("meeting-dictation").textContent = "Диктовать заметки"; };
  try { speech.start(); $("meeting-dictation").textContent = "Остановить диктовку"; }
  catch { recognizer = null; status("Не удалось начать диктовку."); }
});

if (!token) { joinForm.hidden = true; $("meeting-copy").hidden = true; status("Откройте ссылку, которую прислал организатор."); }
