// Time clock and timesheets. Loaded after the main script in index.html and
// uses its helpers (h, api, refresh, dialog, team, projects, me, ...).
//
// Everyone clocks themselves in and out. The Time page shows a week of time:
// your own, or (for the owner and office) everyone's, with totals, overtime,
// approving, fixing mistakes and a download for payroll.

const tcDur = (m) => m == null ? "" : m < 60 ? m + " min" : Math.floor(m / 60) + " h" + (m % 60 ? " " + (m % 60) + " min" : "");
const tcClock = (iso) => new Date(iso).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
const tcSince = (iso) => Math.max(0, Math.round((Date.now() - new Date(iso)) / 60000));
// Jobs you can clock in to: active ones first, then planning and on hold.
const tcOpenJobs = () => projects.filter(p => p.status !== 3).sort((a, b) => (a.status !== 1) - (b.status !== 1) || (a.number ?? 0) - (b.number ?? 0));
// Hours for the totals table: "16.5", "24", or "–" for none.
const tcHrs = (m) => m ? String(Math.round(m / 60 * 100) / 100) : "–";
// "7:02 AM" plus the date when it isn't today.
const tcWhen = (iso) => new Date(iso).toDateString() === new Date().toDateString() ? tcClock(iso)
  : new Date(iso).toLocaleDateString(undefined, { weekday: "short", month: "short", day: "numeric" }) + ", " + tcClock(iso);

// Keeps a "2 h 13 min" count going while it's on the page.
function tcTicker(iso) {
  const el = h("span", {}, tcDur(tcSince(iso)));
  const timer = setInterval(() => { if (!el.isConnected) clearInterval(timer); else el.textContent = tcDur(tcSince(iso)); }, 30000);
  return el;
}

/* ---------- The clock card ---------- */
// On the Today page and the Time page (pick a job), or a job's page (that job).
function clockCard(job) {
  const err = h("p", { class: "err", role: "alert" });
  const act = (fn) => async (e) => {
    const btn = e.currentTarget; btn.disabled = true; err.textContent = "";
    try { await fn(); await refresh(); }
    catch (ex) { err.textContent = ex.message; btn.disabled = false; }
  };
  const open = clockEntry;
  if (open) {
    const here = job && open.projectId === job.id;
    const elsewhere = job && !here;
    const onJob = projects.find(p => p.id === open.projectId);
    return h("div", { class: "panel clock on" },
      h("div", { class: "clockhead" },
        h("span", { class: "dot", "aria-hidden": "true" }),
        h("div", {},
          h("b", {}, here ? "You’re on the clock here" : "You’re on the clock"),
          h("div", { class: "sub" },
            !here && [onJob ? h("button", { class: "link", type: "button", onclick: () => selectJob(onJob.id) }, open.jobName) : open.jobName, " · "],
            "since " + tcWhen(open.clockIn) + " · ", tcTicker(open.clockIn)))),
      h("div", { class: "quick" },
        h("button", { class: "btn", type: "button", onclick: () => clockOutDialog(open) }, "Clock out"),
        elsewhere
          ? h("button", { class: "btn ghost", type: "button", onclick: act(() => api("/api/time/switch", "POST", { projectId: job.id })) }, "Switch to this job")
          : !job && tcOpenJobs().length > 1 && h("button", { class: "btn ghost", type: "button", onclick: () => switchDialog(open) }, "Switch job")),
      err);
  }
  if (job) {
    return h("div", { class: "panel clock" },
      h("div", { class: "panelhead" }, h("h2", {}, "Time clock"),
        h("button", { class: "btn sm", type: "button", onclick: act(() => api("/api/time/clock-in", "POST", { projectId: job.id })) }, "Clock in here")),
      err);
  }
  const jobs = tcOpenJobs();
  if (!jobs.length) return h("div", { class: "panel clock" }, h("h2", {}, "Time clock"), h("p", { class: "sub", style: "margin:0" }, "Once there’s an open job, you can clock in to it here."));
  const pick = h("select", { "aria-label": "Job to clock in to" }, jobs.map(p => h("option", { value: p.id, selected: p.id === selected ? "" : false }, jobLabel(p))));
  return h("div", { class: "panel clock" },
    h("h2", {}, "Time clock"),
    h("div", { class: "inline wrap-sm" }, pick,
      h("button", { class: "btn", type: "button", onclick: act(() => api("/api/time/clock-in", "POST", { projectId: pick.value })) }, "Clock in")),
    err);
}

function clockOutDialog(open) {
  const worked = tcSince(open.clockIn);
  dialog("Clock out", [
    h("p", { class: "sub", style: "margin:0" }, open.jobName + " · since " + tcWhen(open.clockIn) + " · " + tcDur(worked)),
    h("label", {}, "Unpaid break (minutes)", h("input", { name: "breakMinutes", type: "number", min: 0, max: worked, step: 1, value: worked >= 360 ? 30 : 0, inputmode: "numeric" })),
    h("label", {}, "Notes", h("textarea", { name: "notes", rows: 2, maxlength: 1000, placeholder: "Optional" }))
  ], "Clock out", (f) => api("/api/time/clock-out", "POST", { breakMinutes: f.breakMinutes ? +f.breakMinutes : 0, notes: f.notes || null }));
}

function switchDialog(open) {
  const jobs = tcOpenJobs().filter(p => p.id !== open.projectId);
  dialog("Switch job", [
    h("p", { class: "sub", style: "margin:0" }, "Clocks you out of " + open.jobName + " and into the job you pick, starting now."),
    h("label", {}, "Job", h("select", { name: "projectId" }, jobs.map(p => h("option", { value: p.id }, jobLabel(p))))),
    h("label", {}, "Unpaid break on " + open.jobName + " (minutes)", h("input", { name: "breakMinutes", type: "number", min: 0, step: 1, value: 0, inputmode: "numeric" }))
  ], "Switch", (f) => api("/api/time/switch", "POST", { projectId: f.projectId, breakMinutes: f.breakMinutes ? +f.breakMinutes : 0 }));
}

/* ---------- The Time page ---------- */
let tcWeek = null, tcWho = "";
function timeView() {
  tcWeek = tcWeek || mondayOf(todayStr());
  const office = canManage();
  const title = h("p", { class: "sub", style: "margin:0" });
  const body = h("div", { class: "stack", style: "gap:20px" }, h("p", { class: "sub" }, "Loading…"));
  const load = async () => {
    const from = tcWeek, to = plusDays(from, 6);
    title.textContent = (from === mondayOf(todayStr()) ? "This week · " : "") + shortDate(from) + " – " + shortDate(to);
    const q = new URLSearchParams({ from, to });
    if (office && tcWho) q.set("userId", tcWho);
    let data, now = [];
    try { [data, now] = await Promise.all([api("/api/time?" + q), office ? api("/api/time/now") : []]); }
    catch (ex) { body.replaceChildren(h("p", { class: "err" }, ex.message)); return; }
    body.replaceChildren(...[
      office && now.length ? onTheClockPanel(now) : null,
      data.people.length ? totalsPanel(data, load) : null,
      ...peoplePanels(data, load),
      office && data.jobs.length > 1 ? jobTotalsPanel(data.jobs) : null,
      !data.entries.length ? h("p", { class: "sub" }, office ? (tcWho ? "No time for this person this week." : "No time this week.") : "You have no time this week.") : null
    ].filter(Boolean));
  };
  const go = (n) => { tcWeek = n === 0 ? mondayOf(todayStr()) : plusDays(tcWeek, 7 * n); load(); };
  const download = async (btn) => {
    btn.disabled = true;
    try {
      const q = new URLSearchParams({ from: tcWeek, to: plusDays(tcWeek, 6) });
      if (tcWho) q.set("userId", tcWho);
      const res = await fetch(API + "/api/time/export?" + q, { headers: { Authorization: "Bearer " + token } });
      if (!res.ok) throw new Error(((await res.json().catch(() => null)) || {}).error || "The download didn’t work (" + res.status + ").");
      const url = URL.createObjectURL(await res.blob());
      const a = h("a", { href: url, download: "timesheet-" + tcWeek + ".csv" });
      document.body.append(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10000);
    } catch (ex) { alert(ex.message); }
    btn.disabled = false;
  };
  load();
  return h("div", {},
    h("div", { class: "head" },
      h("div", {}, h("h1", {}, "Time"), title),
      h("div", { class: "headtools" },
        h("div", { class: "weeknav" },
          h("button", { class: "btn ghost", type: "button", "aria-label": "Previous week", onclick: () => go(-1) }, "‹"),
          h("button", { class: "btn ghost", type: "button", onclick: () => go(0) }, "This week"),
          h("button", { class: "btn ghost", type: "button", "aria-label": "Next week", onclick: () => go(1) }, "›")),
        office && team.members.length > 1 && h("select", { "aria-label": "Show time for", style: "width:auto", onchange: (e) => { tcWho = e.target.value; load(); } },
          h("option", { value: "" }, "Everyone"), team.members.map(m => h("option", { value: m.id, selected: m.id === tcWho ? "" : false }, m.name))),
        office && h("button", { class: "btn ghost", type: "button", onclick: (e) => download(e.currentTarget) }, "Download"),
        office && h("button", { class: "btn", type: "button", onclick: () => timeEntryDialog(null) }, "Add time"))),
    h("div", { class: "stack", style: "gap:20px" }, clockCard(), body));
}

function onTheClockPanel(now) {
  return h("div", { class: "panel" },
    h("h2", {}, "On the clock now (" + now.length + ")"),
    now.map(e => h("div", { class: "tcnow" }, avatar(e.userName),
      h("div", {}, h("b", {}, e.userName), h("div", { class: "sub" }, e.jobName + " · since " + tcWhen(e.clockIn))),
      h("span", { class: "mono" }, tcTicker(e.clockIn)))));
}

// Each person's week: total, regular, overtime, double time, and approving.
function totalsPanel(data, reload) {
  const office = canManage();
  const ot = data.people.some(p => p.overtime || p.doubleTime);
  return h("div", { class: "panel" },
    h("h2", {}, (office ? "Totals" : "Your totals") + " (hours)"),
    h("div", { class: "tctable" },
      h("div", { class: "tcrow th" }, h("span", {}, "Person"), h("span", {}, "Total"), h("span", {}, "Regular"), h("span", {}, "Overtime"), h("span", {}, "Double"), h("span", { class: "hide-sm" }, "Approved"), h("span", {})),
      data.people.map(p => {
        const mine = data.entries.filter(e => e.userId === p.userId && e.clockOut);
        const toApprove = mine.filter(e => !e.approved);
        return h("div", { class: "tcrow" },
          h("b", {}, p.name, p.onTheClock ? h("span", { class: "dot", title: "On the clock", "aria-label": "On the clock" }) : null),
          h("span", { class: "mono", title: tcDur(p.minutes) }, tcHrs(p.minutes)),
          h("span", { class: "mono" }, tcHrs(p.regular)),
          h("span", { class: "mono" + (p.overtime ? " warnc" : "") }, tcHrs(p.overtime)),
          h("span", { class: "mono" + (p.doubleTime ? " warnc" : "") }, tcHrs(p.doubleTime)),
          h("span", { class: "mono hide-sm" }, p.approved === p.minutes && p.minutes ? "All" : tcHrs(p.approved)),
          office ? h("div", { class: "tools" },
            toApprove.length ? h("button", { class: "btn sm", type: "button", onclick: tcApprove(toApprove, true, reload) }, "Approve")
              : mine.length ? h("button", { class: "link", type: "button", onclick: tcApprove(mine, false, reload) }, "Unapprove") : null) : h("span"));
      })),
    ot && h("p", { class: "sub", style: "margin:0;font-size:13px" }, "Overtime follows California’s rules: over 8 hours a day or 40 a week is overtime, over 12 hours a day is double time. Have payroll check special cases, like a 7th day in a row."));
}

function tcApprove(list, approved, reload) {
  return async (e) => {
    const btn = e.currentTarget; btn.disabled = true;
    try { await api("/api/time/approve", "POST", { entryIds: list.map(x => x.id), approved }); await reload(); }
    catch (ex) { alert(ex.message); btn.disabled = false; }
  };
}

// One panel per person, with their shifts day by day.
function peoplePanels(data, reload) {
  const office = canManage();
  const byPerson = new Map();
  for (const e of data.entries) { if (!byPerson.has(e.userId)) byPerson.set(e.userId, []); byPerson.get(e.userId).push(e); }
  return [...byPerson.values()].map(list => {
    const days = [...new Set(list.map(e => e.day))];
    return h("div", { class: "panel" },
      h("h2", {}, office ? list[0].userName : "Your shifts"),
      days.map(d => {
        const shifts = list.filter(e => e.day === d);
        const total = shifts.reduce((s, e) => s + (e.minutes || 0), 0);
        return h("div", { class: "tcday" },
          h("div", { class: "dayhead" }, h("b", {}, fmtDate(d)), h("span", { class: "sub" }, tcDur(total)), total > 480 && h("span", { class: "tag warnc" }, "Over 8 h")),
          shifts.map(e => {
            const job = projects.find(p => p.id === e.projectId);
            const long = (e.clockOut ? new Date(e.clockOut) : new Date()) - new Date(e.clockIn) > 12 * 3600000;
            return h("div", { class: "tcshift" },
              h("div", {},
                h("div", {}, job ? h("button", { class: "link", type: "button", onclick: () => selectJob(job.id) }, e.jobName) : e.jobName),
                h("div", { class: "sub" },
                  tcClock(e.clockIn) + " – " + (e.clockOut ? tcClock(e.clockOut) : "now"),
                  e.breakMinutes ? " · " + e.breakMinutes + " min break" : "",
                  " · ", e.clockOut ? h("b", {}, tcDur(e.minutes) || "0 min") : h("span", { class: "pill" }, "On the clock"),
                  e.approved ? h("span", { class: "pill ok", title: "Approved by " + e.approvedBy }, "Approved") : null,
                  long ? h("span", { class: "late" }, e.clockOut ? " · Over 12 h, check it" : " · Still on the clock after 12 h. Did they forget to clock out?") : null,
                  e.editedBy ? h("span", { title: "Added or changed by " + e.editedBy }, " · edited") : null),
                e.notes ? h("div", { class: "sub", style: "white-space:pre-wrap" }, e.notes) : null),
              office && !e.approved ? h("div", { class: "tools" },
                h("button", { class: "link", type: "button", onclick: () => timeEntryDialog(e) }, "Edit"),
                h("button", { class: "x", type: "button", title: "Delete", "aria-label": "Delete this time", onclick: () =>
                  confirmDelete("Delete " + e.userName + "’s time on " + fmtDate(e.day) + " (" + (tcDur(e.minutes) || "on the clock") + ")?", "/api/time/" + e.id) }, "×")) : h("span"));
          }));
      }));
  });
}

function jobTotalsPanel(jobs) {
  return h("div", { class: "panel" },
    h("h2", {}, "Hours by job"),
    jobs.slice().sort((a, b) => b.minutes - a.minutes).map(j =>
      h("div", { class: "trow" }, h("span", {}, j.jobName), h("span", { class: "mono" }, tcDur(j.minutes)))));
}

// Adding or fixing time by hand (owner and office). Times are entered in this
// device's time zone; an end before the start means the shift ran past midnight.
function timeEntryDialog(entry) {
  const v = entry || {};
  const local = (iso) => {
    if (!iso) return { date: "", time: "" };
    const d = new Date(iso);
    return { date: d.toLocaleDateString("en-CA"), time: d.toTimeString().slice(0, 5) };
  };
  const start = entry ? local(v.clockIn) : { date: todayStr(), time: "07:00" };
  const end = entry ? local(v.clockOut) : { date: "", time: "15:30" };
  const people = team.members.slice();
  if (entry && !people.some(m => m.id === v.userId)) people.push({ id: v.userId, name: v.userName + " (no longer on the team)" });
  const jobs = projects.slice().sort((a, b) => (a.number ?? 0) - (b.number ?? 0));
  const notes = h("textarea", { name: "notes", rows: 2, maxlength: 1000, placeholder: "Optional" });
  notes.value = v.notes || "";
  const stillOpen = entry && !entry.clockOut;
  dialog(entry ? "Edit time" : "Add time", [
    h("label", {}, "Who", h("select", { name: "userId", required: true }, people.map(m => h("option", { value: m.id, selected: m.id === v.userId ? "" : false }, m.name)))),
    h("label", {}, "Job", h("select", { name: "projectId" },
      entry && !v.projectId ? h("option", { value: "" }, v.jobName + " (deleted job)") : null,
      jobs.map(p => h("option", { value: p.id, selected: p.id === v.projectId ? "" : false }, jobLabel(p))))),
    h("label", {}, "Day", h("input", { name: "date", type: "date", required: true, value: start.date })),
    h("div", { class: "inline" },
      h("label", { style: "flex:1" }, "Clock in", h("input", { name: "in", type: "time", required: true, value: start.time })),
      h("label", { style: "flex:1" }, "Clock out", h("input", { name: "out", type: "time", required: stillOpen ? false : true, value: end.time }))),
    stillOpen && h("p", { class: "sub", style: "margin:-8px 0 0;font-size:13px" }, "Leave clock out blank to keep them on the clock."),
    h("label", {}, "Unpaid break (minutes)", h("input", { name: "breakMinutes", type: "number", min: 0, step: 1, value: v.breakMinutes || 0, inputmode: "numeric" })),
    h("label", {}, "Notes", notes)
  ], entry ? "Save changes" : "Add time", async (f) => {
    const clockIn = new Date(f.date + "T" + f.in);
    let clockOut = null;
    if (f.out) {
      clockOut = new Date(f.date + "T" + f.out);
      if (clockOut <= clockIn) clockOut.setDate(clockOut.getDate() + 1); // past midnight
    }
    if (isNaN(clockIn)) throw new Error("Enter the day and the clock-in time.");
    const body = { userId: f.userId, projectId: f.projectId || null, clockIn: clockIn.toISOString(), clockOut: clockOut && clockOut.toISOString(),
      breakMinutes: f.breakMinutes ? +f.breakMinutes : 0, notes: f.notes || null };
    // dialog() reloads the app afterwards, which redraws this page.
    return entry ? api("/api/time/" + entry.id, "PUT", body) : api("/api/time", "POST", body);
  });
}
