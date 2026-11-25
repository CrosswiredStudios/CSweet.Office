"use strict";
const el = id => document.getElementById(id);
const ticket = location.hash.slice(1) || sessionStorage.getItem("officeSession");
if (location.hash) { sessionStorage.setItem("officeSession", ticket); history.replaceState(null, "", "/"); }
let latest, updating, acting = false, warning = "";
function theme(value) { document.documentElement.classList.toggle("gold", value === "Gold"); el("theme").value = value; }
theme(localStorage.getItem("officeTheme") || "Aurora");
el("theme").onchange = () => { localStorage.setItem("officeTheme", el("theme").value); theme(el("theme").value); };
async function api(path, body) {
 const opts = { headers: { "X-Office-Session": ticket || "" }, cache: "no-store" };
 if (body !== undefined) { opts.method = "POST"; opts.headers["Content-Type"] = "application/json"; opts.body = JSON.stringify(body); }
 const response = await fetch("/api/" + path, opts);
 if (!response.ok) { const info = await response.json().catch(() => null); throw new Error(info?.error || (response.status === 403 ? "Open C-Sweet Office from this computer’s app launcher." : "Office could not complete that action. Refresh the device status and try again.")); }
 return response.status === 200 ? response.json() : null;
}
function render(s) {
 latest = s;
 el("defaultLimits").textContent = "Default sharing limits · " + s.defaultCpu + " CPU cores · 4 GiB memory · 32 GiB storage";
 if (!el("cpu").value) el("cpu").value = s.defaultCpu;
 const configured = s.services.some(x => x.state !== "Awaiting setup");
 const fresh = !configured && !s.installing;
 el("title").textContent = s.installing ? "Installing Office…" : configured ? "Your Office" : "Welcome to C-Sweet Office";
 el("device").textContent = s.machine;
 el("lead").textContent = s.installing ? "Follow your computer’s administrator prompt. You can leave this page open." : configured ? "Check the services running on this computer." : "Connect this computer to Headquarters so your team can run work here.";
 el("message").textContent = warning || s.message || "";
 el("setup").hidden = !fresh || !!s.verify;
 el("pair").hidden = !fresh || !s.verify || s.approved;
 el("configure").hidden = !fresh || (!!s.verify && !s.approved);
 el("manual").hidden = s.approved;
 el("install").hidden = !s.approved && !el("manual").open;
 el("destination").textContent = s.approved ? "Approved by " + s.headquarters + ". Office will connect to " + s.gateway + "." : "Use the one-use connection code from your Headquarters.";
 if (s.verify) { el("verify").href = s.verify; el("match").textContent = s.matchingCode; if (s.qr && el("qr").dataset.key !== s.matchingCode) { el("qr").src = "data:image/png;base64," + s.qr; el("qr").dataset.key = s.matchingCode; } }
 el("health").hidden = false; el("version").textContent = "Version " + s.version;
 el("services").replaceChildren(...s.services.map(x => { const row = document.createElement("div"), name = document.createElement("dt"), value = document.createElement("dd"); name.textContent = x.name; value.textContent = x.state; row.append(name,value); return row; }));
 if (s.health && configured) {
  const h = s.health;
  const fresh = Date.now() - new Date(h.checkedAt).getTime() < 45000 && s.services.find(x => x.name === "Node")?.state === "Running";
  const values = [["Headquarters gateway", h.gateway],["Connection", fresh ? h.connection : "Last reported: " + h.connection],
    ["Last checked", new Date(h.checkedAt).toLocaleString()],["Sharing limits", h.cpu + " CPU · " + (h.memoryMb / 1024).toFixed(1) + " GB memory · " + Math.round(h.diskMb / 1024) + " GB storage"],
    ["Active assignments at last check", h.activeWork + " of " + h.workLimit],["Office ID", h.officeId]];
  for (const [label,value] of values) { const row=document.createElement("div"), name=document.createElement("dt"), detail=document.createElement("dd"); name.textContent=label; detail.textContent=value; row.append(name,detail); el("services").append(row); }
 }
 const stopped = s.services.filter(x => x.state === "Stopped" || x.state === "Failed");
 const recent = s.health && Date.now() - new Date(s.health.checkedAt).getTime() < 45000;
 el("advice").textContent = !configured ? "Connect this computer first. If setup did not finish, check the administrator prompt and hardware virtualization settings." :
   stopped.length ? stopped.map(x => x.name).join(", ") + " needs attention. Open the system service manager or use Repair in Headquarters if this Office is reachable." :
   !s.health ? "Waiting for the first device report. Check that the gateway is reachable and review this computer’s enrollment and certificate in Headquarters." :
   !recent || s.health.connection !== "Connected" ? "Check this computer’s network or VPN, gateway address and certificate. Headquarters shows the last authenticated connection." :
   "The Office has recently connected. Open its details in Headquarters to check approval, virtualization and availability for work.";
 el("preview").textContent = JSON.stringify(reportData(s), null, 2);
 el("manage").hidden = !s.manage; if (s.manage) el("manage").href = s.manage;
 el("statusHint").textContent = configured ? "Open Offices in Headquarters to check this computer’s approval, authenticated connection and eligibility for work." : "The installer starts these services after you connect this computer.";
}
async function refresh() {
 if (acting || updating) return updating;
 updating = api("status").then(render).catch(e => { el("message").textContent = e.message || "Open C-Sweet Office again to reconnect."; });
 try { await updating; } finally { updating = null; }
}
async function act(path, body) {
 if (acting) return;
 acting = true; warning = "";
 await updating;
 try { await api(path, body); if (path === "install") { el("code").value = ""; el("agree").checked = false; } }
 catch (e) { warning = e.message; }
 finally { acting = false; await refresh(); }
}
el("hosted").onclick = () => act("start", { mode:"hosted" });
el("private").onsubmit = e => { e.preventDefault(); act("start", { mode:"private", address:el("address").value.trim() }); };
el("cancel").onclick = () => act("cancel", {});
el("manual").ontoggle = () => { if (latest) render(latest); };
el("custom").onchange = () => { el("limits").hidden = !el("custom").checked; };
el("install").onsubmit = e => { e.preventDefault(); act("install", { gateway:el("gateway").value.trim(), code:el("code").value.trim(), pin:el("pin").value.trim(), consent:el("agree").checked, allocation:el("custom").checked ? {cpu:Number(el("cpu").value),memoryGiB:Number(el("memory").value),diskGiB:Number(el("disk").value)} : null }); };
function reportData(s) {
 return {computer:s.machine, officeVersion:s.version, services:s.services, health:s.health, message:s.message};
}
el("report").onclick = () => {
 if (!latest) return;
 const report = reportData(latest);
 const url = URL.createObjectURL(new Blob([JSON.stringify(report,null,2)],{type:"application/json"}));
 const a = document.createElement("a"); a.href=url; a.download="office-diagnostics.json"; a.click(); setTimeout(() => URL.revokeObjectURL(url),1000);
};
refresh(); setInterval(refresh, 5000);
