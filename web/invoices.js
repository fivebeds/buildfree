// Invoices and payments: the billing panel on a job, the invoice editor with
// payments, and the printable invoice. Loaded by index.html after
// estimates.js, and uses helpers from both (h, api, refresh, money, cents, num).
// The API works out each invoice's state and balance; this page shows them.

const INV_STATUS = ["Draft", "Sent", "Void"];
const INV_STATE = {
  draft: { label: "Draft", color: "var(--b-done)" },
  sent: { label: "Sent", color: "var(--b-plan)" },
  partial: { label: "Partly paid", color: "var(--b-hold)" },
  paid: { label: "Paid", color: "var(--b-active)" },
  overdue: { label: "Overdue", color: "var(--err)" },
  void: { label: "Void", color: "var(--b-done)" }
};
const PAY_METHOD = ["Check", "Card", "Cash", "Zelle", "Bank transfer", "Other"];
const addDays = (dateStr, n) => { const d = new Date(dateStr + "T00:00:00"); d.setDate(d.getDate() + n); return d.toLocaleDateString("en-CA"); };
const longDate = (d) => d ? new Date(d + "T00:00:00").toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" }) : "";

/* ---------- Panel on the job page ---------- */
function invoicesPanel(p) {
  const b = billing || {};
  return h("div", { class: "panel" },
    h("div", { class: "todorow" }, h("h2", {}, "Invoices and payments"),
      h("button", { class: "link", type: "button", onclick: () => { estimateOpen = null; invoiceOpen = "new"; render(); } }, "New invoice")),
    h("div", { class: "stack", style: "gap:6px" },
      h("div", { class: "trow" }, h("span", {}, "Contract"), h("span", { class: "mono" }, b.hasContract ? money(b.contract) : "No approved estimate")),
      h("div", { class: "trow" }, h("span", {}, "Invoiced"), h("span", { class: "mono" }, money(b.invoiced))),
      h("div", { class: "trow" }, h("span", {}, "Paid"), h("span", { class: "mono" }, money(b.paid))),
      h("div", { class: "trow big" }, h("span", {}, "Balance due"), h("span", { class: "mono" }, money(b.balanceDue))),
      b.hasContract ? h("div", { class: "trow sub" }, h("span", {}, "Left to bill"), h("span", { class: "mono" }, money(b.leftToBill))) : null,
      b.overdueCount ? h("div", { class: "trow warn" }, h("span", {}, b.overdueCount + " overdue invoice" + (b.overdueCount === 1 ? "" : "s")), h("span")) : null),
    invoices.length
      ? invoices.map(i => {
          const s = INV_STATE[i.state] || INV_STATE.draft;
          return h("button", { class: "estrow", type: "button", onclick: () => { estimateOpen = null; invoiceOpen = i.id; render(); } },
            h("span", {}, h("b", {}, "#" + i.number + " " + i.title),
              h("span", { class: "badge sub", style: "--c:" + s.color }, s.label)),
            h("span", { class: "mono" }, i.state === "partial" || i.state === "overdue" ? money(i.balance) + " due" : money(i.total)));
        })
      : h("p", { class: "sub", style: "margin:0" }, "No invoices yet. Bill a deposit or progress payment against the contract."));
}

/* ---------- Editor ---------- */
function invoiceEditor(p) {
  const saved = invoiceOpen === "new" ? null : invoices.find(i => i.id === invoiceOpen);
  if (!saved && invoiceOpen !== "new") { invoiceOpen = null; return jobDetail(p); }
  const b = billing || {};
  const last = invoices[0];
  const today = todayStr();
  const d = saved
    ? { title: saved.title, issueDate: saved.issueDate, dueDate: saved.dueDate || "", notes: saved.notes || "",
        lines: saved.lines.map(l => ({ description: l.description, amount: l.amount })) }
    : { title: invoices.length ? "Progress payment " + invoices.length : "Deposit", issueDate: today, dueDate: addDays(today, 14),
        notes: last ? last.notes || "" : "", lines: [] };
  const hasPayments = !!saved && saved.payments.length > 0;
  const locked = hasPayments || (!!saved && saved.status === 2);
  let dirty = false;
  const err = h("p", { class: "err", role: "alert" });
  const lock = (el) => { if (locked) el.disabled = true; return el; };
  // What's left to bill, not counting this invoice if it's already been sent.
  const leftToBill = cents((b.leftToBill || 0) + (saved && saved.status === 1 ? saved.total : 0));

  const totalEl = h("span", { class: "mono" });
  const drawTotal = () => { totalEl.textContent = money(cents(d.lines.reduce((s, l) => s + num(l.amount), 0))); };
  const linesEl = h("div", { class: "lines" });
  const drawLines = () => {
    linesEl.replaceChildren(
      ...d.lines.map((l, n) => h("div", { class: "invline" },
        lock(h("input", { value: l.description, maxlength: 500, placeholder: "What this bills for", "aria-label": "Description, line " + (n + 1), oninput: (e) => { l.description = e.target.value; dirty = true; } })),
        lock(h("input", { type: "number", step: "0.01", value: l.amount, placeholder: "0.00", "aria-label": "Amount, line " + (n + 1), class: "mono", oninput: (e) => { l.amount = e.target.value; dirty = true; drawTotal(); } })),
        locked ? h("span") : h("button", { class: "x", type: "button", title: "Remove line", "aria-label": "Remove line " + (n + 1), onclick: () => { d.lines.splice(n, 1); dirty = true; drawLines(); } }, "×"))),
      d.lines.length ? "" : h("p", { class: "sub", style: "margin:0" }, locked ? "No lines." : "Add a line, or use the quick buttons below."));
    drawTotal();
  };
  const addLine = (description, amount) => {
    d.lines.push({ description, amount }); dirty = true; drawLines();
    const inputs = linesEl.querySelectorAll(".invline input:first-child"); if (inputs.length) inputs[inputs.length - 1].focus();
  };

  const pct = h("input", { type: "number", min: 0, max: 100, step: "any", value: 30, style: "width:80px", "aria-label": "Percent of contract" });
  const quick = locked ? null : h("div", { class: "quick" },
    h("button", { class: "btn ghost", type: "button", onclick: () => addLine("", "") }, "+ Add line"),
    b.hasContract ? [
      h("span", { class: "inline", style: "align-items:center;gap:6px" }, pct, h("button", { class: "btn ghost", type: "button", onclick: () => {
        const n = num(pct.value);
        addLine(n + "% of contract (" + money(b.contract) + ")", cents(b.contract * n / 100));
      } }, "% of contract")),
      leftToBill > 0 ? h("button", { class: "btn ghost", type: "button", onclick: () => addLine("Remaining contract balance", leftToBill) }, "Remaining balance (" + money(leftToBill) + ")") : null
    ] : h("span", { class: "sub" }, "Approve an estimate to bill a percent of the contract."));

  const save = async () => {
    err.textContent = "";
    const body = { title: d.title, issueDate: d.issueDate || null, dueDate: d.dueDate || null, notes: d.notes || null,
      lines: d.lines.map(l => ({ description: l.description, amount: num(l.amount) })) };
    const out = saved ? await api("/api/invoices/" + saved.id, "PUT", body) : await api("/api/projects/" + p.id + "/invoices", "POST", body);
    dirty = false; invoiceOpen = out.id;
    return out;
  };
  const run = (fn) => async (e) => {
    const btn = e && e.currentTarget; if (btn) btn.disabled = true;
    try { await fn(); } catch (ex) { err.textContent = ex.message; } finally { if (btn) btn.disabled = false; }
  };
  const back = () => {
    if (dirty && !confirm("Leave without saving your changes?")) return;
    invoiceOpen = null; render();
  };

  const status = saved && h("select", { "aria-label": "Invoice status", style: "width:auto", onchange: run(async () => {
    if (dirty && !locked) await save();
    await api("/api/invoices/" + saved.id + "/status", "PUT", { status: +status.value }); await refresh();
  }) }, INV_STATUS.map((s, i) => h("option", { value: i, selected: saved.status === i ? "" : false }, s)));

  // Payments received on this invoice.
  let payPanel = null;
  if (saved) {
    const s = INV_STATE[saved.state] || INV_STATE.draft;
    const payErr = h("p", { class: "err", role: "alert" });
    const canPay = saved.status !== 2 && saved.balance > 0;
    const payForm = canPay && h("form", { class: "stack", onsubmit: async (e) => {
      e.preventDefault(); payErr.textContent = "";
      const f = Object.fromEntries(new FormData(payForm));
      const btn = $("button[type=submit]", payForm); btn.disabled = true;
      try {
        await api("/api/invoices/" + saved.id + "/payments", "POST", { amount: num(f.amount), date: f.date || null, method: +f.method, reference: f.reference || null });
        await refresh();
      } catch (ex) { payErr.textContent = ex.message; btn.disabled = false; }
    } },
      h("div", { class: "inline" },
        h("label", { style: "flex:1" }, "Amount", h("input", { name: "amount", type: "number", step: "0.01", min: 0.01, max: saved.balance, value: saved.balance, required: true, class: "mono" })),
        h("label", { style: "flex:1" }, "Date", h("input", { name: "date", type: "date", value: today }))),
      h("div", { class: "inline" },
        h("label", { style: "flex:1" }, "Method", h("select", { name: "method" }, PAY_METHOD.map((m, i) => h("option", { value: i }, m)))),
        h("label", { style: "flex:1" }, "Reference", h("input", { name: "reference", maxlength: 100, placeholder: "Check #, last 4" }))),
      h("button", { class: "btn", type: "submit" }, "Record payment"),
      payErr);
    payPanel = h("div", { class: "panel totals" },
      h("div", { class: "trow" }, h("h2", {}, "Payments"), h("span", { class: "badge sub", style: "--c:" + s.color }, s.label)),
      h("div", { class: "trow" }, h("span", {}, "Invoice total"), h("span", { class: "mono" }, money(saved.total))),
      saved.payments.map(pay => h("div", { class: "trow sub" },
        h("span", {}, fmtDate(pay.date) + " · " + PAY_METHOD[pay.method] + (pay.reference ? " · " + pay.reference : "")),
        h("span", { class: "tools" }, h("span", { class: "mono" }, "−" + money(pay.amount)),
          h("button", { class: "x", type: "button", title: "Delete payment", "aria-label": "Delete payment of " + money(pay.amount), onclick: () =>
            confirmDelete("Delete this " + money(pay.amount) + " payment? Only do this if it was entered by mistake.", "/api/payments/" + pay.id) }, "×")))),
      h("div", { class: "trow big" }, h("span", {}, "Balance due"), h("span", { class: "mono" }, money(saved.balance))),
      payForm || null,
      saved.status === 0 && canPay ? h("p", { class: "sub", style: "margin:0;font-size:13px" }, "Recording a payment marks this invoice sent.") : null);
  }

  drawLines();
  return h("div", {},
    h("button", { class: "link", type: "button", onclick: back, style: "margin-bottom:12px" }, "← Back to " + p.name),
    h("div", { class: "head" }, h("h1", {}, saved ? "Invoice #" + saved.number : "New invoice"),
      h("div", { class: "headtools" }, status,
        saved && h("button", { class: "btn ghost", type: "button", onclick: run(async () => {
          if (dirty && !locked) { await save(); await refresh(); }
          printInvoice(invoices.find(i => i.id === invoiceOpen), p);
        }) }, "Print"),
        saved && !hasPayments && h("button", { class: "btn ghost", type: "button", onclick: () => confirmDelete("Delete invoice #" + saved.number + "? This can’t be undone.", "/api/invoices/" + saved.id, () => { invoiceOpen = null; }) }, "Delete"))),
    hasPayments ? h("p", { class: "sub" }, "This invoice has payments, so its lines are locked. Delete a payment first if it was a mistake.")
      : locked ? h("p", { class: "sub" }, "This invoice is void. Set it back to draft to change it.") : null,
    h("div", { class: "estgrid" },
      h("div", { class: "panel" },
        h("label", {}, "Title", lock(h("input", { value: d.title, maxlength: 200, oninput: (e) => { d.title = e.target.value; dirty = true; } }))),
        h("div", { class: "inline" },
          h("label", { style: "flex:1" }, "Invoice date", lock(h("input", { type: "date", value: d.issueDate, oninput: (e) => { d.issueDate = e.target.value; dirty = true; } }))),
          h("label", { style: "flex:1" }, "Due date", lock(h("input", { type: "date", value: d.dueDate, oninput: (e) => { d.dueDate = e.target.value; dirty = true; } })))),
        linesEl,
        quick,
        h("div", { class: "trow big" }, h("span", {}, "Invoice total"), totalEl),
        h("label", {}, "Notes for the customer", lock(Object.assign(h("textarea", { rows: 3, maxlength: 10000, placeholder: "How to pay (check payable to, Zelle phone or email), thank-you note", oninput: (e) => { d.notes = e.target.value; dirty = true; } }), { value: d.notes }))),
        err,
        locked ? null : h("div", { class: "actions" },
          h("button", { class: "btn ghost", type: "button", onclick: back }, "Cancel"),
          h("button", { class: "btn", type: "button", onclick: run(async () => { await save(); await refresh(); }) }, saved ? "Save changes" : "Create invoice"))),
      payPanel || h("div", { class: "panel totals" },
        h("h2", {}, "Contract"),
        h("div", { class: "trow" }, h("span", {}, "Contract total"), h("span", { class: "mono" }, b.hasContract ? money(b.contract) : "—")),
        h("div", { class: "trow" }, h("span", {}, "Already invoiced"), h("span", { class: "mono" }, money(b.invoiced))),
        h("div", { class: "trow big" }, h("span", {}, "Left to bill"), h("span", { class: "mono" }, money(leftToBill))),
        h("p", { class: "sub", style: "margin:0;font-size:13px" }, "Payments can be recorded once the invoice is saved."))));
}

/* ---------- Printable invoice ---------- */
function printInvoice(inv, p) {
  if (!inv) return;
  const client = clients.find(c => c.id === p.clientId);
  const page = h("div", { class: "doc" },
    h("div", { class: "doc-head" },
      h("div", {}, h("div", { class: "doc-company" }, (me && me.company) || ""), h("div", { class: "sub" }, me ? me.name + " · " + me.email : "")),
      h("div", { style: "text-align:right" }, h("div", { class: "doc-title" }, "Invoice #" + inv.number),
        h("div", { class: "sub" }, "Date: " + longDate(inv.issueDate)),
        inv.dueDate ? h("div", { class: "sub" }, "Due: " + longDate(inv.dueDate)) : null)),
    h("div", { class: "doc-parties" },
      h("div", {}, h("div", { class: "doc-label" }, "Bill to"),
        client ? [h("div", {}, client.name), client.email && h("div", {}, client.email), client.phone && h("div", {}, client.phone)] : h("div", { class: "sub" }, "—")),
      h("div", {}, h("div", { class: "doc-label" }, "Job"), h("div", {}, p.name), p.address && h("div", {}, p.address))),
    h("h2", { class: "doc-sub" }, inv.title),
    h("table", {},
      h("thead", {}, h("tr", {}, h("th", {}, "Description"), h("th", { class: "num" }, "Amount"))),
      h("tbody", {}, inv.lines.map(l => h("tr", {}, h("td", {}, l.description), h("td", { class: "num" }, money(l.amount))))),
      h("tfoot", {},
        h("tr", {}, h("td", { class: "num" }, "Invoice total"), h("td", { class: "num" }, money(inv.total))),
        inv.payments.map(pay => h("tr", {}, h("td", { class: "num" }, "Payment received " + longDate(pay.date) + " (" + PAY_METHOD[pay.method] + ")"), h("td", { class: "num" }, "−" + money(pay.amount)))),
        h("tr", { class: "doc-total" }, h("td", { class: "num" }, inv.balance > 0 ? "Balance due" : "Paid in full"), h("td", { class: "num" }, money(inv.balance))))),
    inv.notes ? h("div", { class: "doc-notes" }, h("div", { class: "doc-label" }, "Notes"), h("p", {}, inv.notes)) : null);
  document.getElementById("print").replaceChildren(page);
  window.print();
}
