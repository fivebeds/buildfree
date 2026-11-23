// Estimates: the panel on a job, the line-item editor and the printable
// customer copy. Loaded by index.html, and uses its helpers (h, api, refresh).
// Line items are entered at cost; the customer price is cost plus the
// estimate's markup. Tax applies to materials lines only. The API does the
// final math; the editor shows the same math live while you type.

const EST_STATUS = [
  { label: "Draft", color: "var(--b-done)" },
  { label: "Sent", color: "var(--b-plan)" },
  { label: "Approved", color: "var(--b-active)" },
  { label: "Declined", color: "var(--err)" }
];
const EST_CATEGORY = ["Labor", "Materials", "Subcontractor", "Equipment", "Other"];
const MATERIALS = 1;
const money = (n) => (n || 0).toLocaleString("en-US", { style: "currency", currency: "USD" });
const cents = (n) => Math.round((n + Number.EPSILON) * 100) / 100;
const num = (v) => { const n = parseFloat(v); return isFinite(n) ? n : 0; };

// Same math as the API (Estimates.cs).
function estimateTotals(d) {
  const lines = d.items.map(i => {
    const unitPrice = cents(num(i.unitCost) * (1 + num(d.markupPercent) / 100));
    return { unitPrice, amount: cents(num(i.quantity) * unitPrice), cost: cents(num(i.quantity) * num(i.unitCost)), category: +i.category };
  });
  const subtotal = cents(lines.reduce((s, l) => s + l.amount, 0));
  const taxable = lines.filter(l => l.category === MATERIALS).reduce((s, l) => s + l.amount, 0);
  const tax = cents(taxable * num(d.taxPercent) / 100);
  const cost = cents(lines.reduce((s, l) => s + l.cost, 0));
  const profit = cents(subtotal - cost);
  return { lines, subtotal, tax, total: cents(subtotal + tax), cost, profit, marginPercent: subtotal > 0 ? Math.round(profit / subtotal * 1000) / 10 : 0 };
}

/* ---------- Panel on the job page ---------- */
function estimatesPanel(p) {
  return h("div", { class: "panel" },
    h("div", { class: "todorow" }, h("h2", {}, "Estimates"),
      h("button", { class: "link", type: "button", onclick: () => { estimateOpen = "new"; render(); } }, "New estimate")),
    estimates.length
      ? estimates.map(e => {
          const s = EST_STATUS[e.status] || EST_STATUS[0];
          return h("button", { class: "estrow", type: "button", onclick: () => { estimateOpen = e.id; render(); } },
            h("span", {}, h("b", {}, "#" + e.number + " " + e.title),
              h("span", { class: "badge sub", style: "--c:" + s.color }, s.label)),
            h("span", { class: "mono" }, money(e.total)));
        })
      : h("p", { class: "sub" }, "No estimates yet. Price out the job with labor, materials and subs."));
}

/* ---------- Editor ---------- */
function estimateEditor(p) {
  const saved = estimateOpen === "new" ? null : estimates.find(e => e.id === estimateOpen);
  if (!saved && estimateOpen !== "new") { estimateOpen = null; return jobDetail(p); }
  const last = estimates[0];
  const d = saved
    ? { title: saved.title, notes: saved.notes || "", markupPercent: saved.markupPercent, taxPercent: saved.taxPercent,
        items: saved.items.map(i => ({ description: i.description, category: i.category, quantity: i.quantity, unit: i.unit || "", unitCost: i.unitCost })) }
    : { title: p.name, notes: last ? last.notes || "" : "", markupPercent: last ? last.markupPercent : 20, taxPercent: last ? last.taxPercent : 0,
        items: [{ description: "", category: 0, quantity: 1, unit: "", unitCost: "" }] };
  const locked = !!saved && saved.status === 2;
  let dirty = false;
  const err = h("p", { class: "err", role: "alert" });
  const lock = (el) => { if (locked) el.disabled = true; return el; };

  // Totals box, refreshed on every change.
  const totalsEl = h("div", { class: "panel totals" });
  const drawTotals = () => {
    const t = estimateTotals(d);
    rowEls.forEach((r, n) => { r.price.textContent = money(t.lines[n].unitPrice); r.amount.textContent = money(t.lines[n].amount); });
    const low = t.subtotal > 0 && t.marginPercent < 15;
    totalsEl.replaceChildren(
      h("h2", {}, "Customer sees"),
      h("div", { class: "trow" }, h("span", {}, "Subtotal"), h("span", { class: "mono" }, money(t.subtotal))),
      h("div", { class: "trow" }, h("span", {}, "Tax on materials (" + num(d.taxPercent) + "%)"), h("span", { class: "mono" }, money(t.tax))),
      h("div", { class: "trow big" }, h("span", {}, "Total"), h("span", { class: "mono" }, money(t.total))),
      h("h2", { style: "margin-top:8px" }, "Only you see"),
      h("div", { class: "trow" }, h("span", {}, "Your cost"), h("span", { class: "mono" }, money(t.cost))),
      h("div", { class: "trow" }, h("span", {}, "Profit"), h("span", { class: "mono" }, money(t.profit))),
      h("div", { class: "trow" + (low ? " warn" : "") }, h("span", {}, "Margin"), h("span", { class: "mono" }, t.marginPercent + "%")),
      low ? h("p", { class: "sub", style: "margin:0;font-size:13px" }, "Margin is under 15%.") : "");
  };
  const changed = () => { dirty = true; drawTotals(); };

  // Line items.
  let rowEls = [];
  const linesEl = h("div", { class: "lines" });
  const drawLines = () => {
    rowEls = d.items.map((it, n) => {
      const input = (key, attrs) => lock(h("input", { ...attrs, value: it[key] ?? "", oninput: (e) => { it[key] = e.target.value; changed(); } }));
      const r = {
        price: h("span", { class: "mono sub cell-price" }),
        amount: h("span", { class: "mono cell-amount" })
      };
      r.el = h("div", { class: "line" },
        input("description", { placeholder: "What’s included", maxlength: 500, "aria-label": "Description, line " + (n + 1), class: "cell-desc" }),
        lock(h("select", { "aria-label": "Category, line " + (n + 1), class: "cell-cat", onchange: (e) => { it.category = +e.target.value; changed(); } },
          EST_CATEGORY.map((c, i) => h("option", { value: i, selected: +it.category === i ? "" : false }, c)))),
        input("quantity", { type: "number", min: 0, step: "any", placeholder: "Qty", "aria-label": "Quantity, line " + (n + 1), class: "cell-qty" }),
        input("unit", { placeholder: "ea", maxlength: 20, "aria-label": "Unit, line " + (n + 1), class: "cell-unit" }),
        input("unitCost", { type: "number", min: 0, step: "0.01", placeholder: "Unit cost", "aria-label": "Unit cost, line " + (n + 1), class: "cell-cost" }),
        r.price, r.amount,
        locked ? h("span") : h("button", { class: "x", type: "button", title: "Remove line", "aria-label": "Remove line " + (n + 1), onclick: () => {
          d.items.splice(n, 1); dirty = true; drawLines();
        } }, "×"));
      return r;
    });
    linesEl.replaceChildren(
      h("div", { class: "line th" }, ["Description", "Category", "Qty", "Unit", "Unit cost", "Price", "Amount", ""].map(t => h("span", {}, t))),
      ...rowEls.map(r => r.el));
    drawTotals();
  };

  const save = async () => {
    err.textContent = "";
    const body = {
      title: d.title, notes: d.notes || null, markupPercent: num(d.markupPercent), taxPercent: num(d.taxPercent),
      items: d.items.map(i => ({ description: i.description, category: +i.category, quantity: num(i.quantity), unit: i.unit || null, unitCost: num(i.unitCost) }))
    };
    const out = saved ? await api("/api/estimates/" + saved.id, "PUT", body) : await api("/api/projects/" + p.id + "/estimates", "POST", body);
    dirty = false; estimateOpen = out.id;
    return out;
  };
  const run = (fn) => async (e) => {
    const btn = e && e.currentTarget; if (btn) btn.disabled = true;
    try { await fn(); } catch (ex) { err.textContent = ex.message; } finally { if (btn) btn.disabled = false; }
  };
  const back = () => {
    if (dirty && !confirm("Leave without saving your changes?")) return;
    estimateOpen = null; render();
  };

  const status = saved && h("select", { "aria-label": "Estimate status", style: "width:auto", onchange: run(async () => {
    await api("/api/estimates/" + saved.id + "/status", "PUT", { status: +status.value }); await refresh();
  }) }, EST_STATUS.map((s, i) => h("option", { value: i, selected: saved.status === i ? "" : false }, s.label)));

  drawLines();
  return h("div", {},
    h("button", { class: "link", type: "button", onclick: back, style: "margin-bottom:12px" }, "← Back to " + p.name),
    h("div", { class: "head" }, h("h1", {}, saved ? "Estimate #" + saved.number : "New estimate"),
      h("div", { class: "headtools" }, status,
        saved && h("button", { class: "btn ghost", type: "button", onclick: run(async () => {
          if (dirty && !locked) { await save(); await refresh(); }
          printEstimate(estimates.find(e => e.id === estimateOpen), p);
        }) }, "Print"),
        saved && h("button", { class: "btn ghost", type: "button", onclick: run(async () => {
          if (dirty && !confirm("Copy the last saved version? Your unsaved changes won’t be in the copy.")) return;
          const out = await api("/api/estimates/" + saved.id + "/copy", "POST"); estimateOpen = out.id; await refresh();
        }) }, "Copy"),
        saved && h("button", { class: "btn ghost", type: "button", onclick: () => confirmDelete("Delete estimate #" + saved.number + "? This can’t be undone.", "/api/estimates/" + saved.id, () => { estimateOpen = null; }) }, "Delete"))),
    locked ? h("p", { class: "sub" }, "This estimate is approved, so it’s locked. Make a copy to change it.") : null,
    h("div", { class: "estgrid" },
      h("div", { class: "panel" },
        h("label", {}, "Title", lock(h("input", { value: d.title, maxlength: 200, oninput: (e) => { d.title = e.target.value; dirty = true; } }))),
        h("div", { class: "inline" },
          h("label", { style: "flex:1" }, "Markup %", lock(h("input", { type: "number", min: 0, max: 500, step: "any", value: d.markupPercent, oninput: (e) => { d.markupPercent = e.target.value; changed(); } }))),
          h("label", { style: "flex:1" }, "Sales tax % (materials)", lock(h("input", { type: "number", min: 0, max: 20, step: "any", value: d.taxPercent, oninput: (e) => { d.taxPercent = e.target.value; changed(); } })))),
        linesEl,
        locked ? null : h("div", {}, h("button", { class: "btn ghost", type: "button", onclick: () => {
          d.items.push({ description: "", category: 0, quantity: 1, unit: "", unitCost: "" }); dirty = true; drawLines();
          const descs = linesEl.querySelectorAll(".cell-desc"); descs[descs.length - 1].focus();
        } }, "+ Add line")),
        h("label", {}, "Notes for the customer", lock(Object.assign(h("textarea", { rows: 4, maxlength: 10000, placeholder: "Scope, exclusions, payment terms, how long the price is good for", oninput: (e) => { d.notes = e.target.value; dirty = true; } }), { value: d.notes }))),
        err,
        locked ? null : h("div", { class: "actions" },
          h("button", { class: "btn ghost", type: "button", onclick: back }, "Cancel"),
          h("button", { class: "btn", type: "button", onclick: run(async () => { await save(); await refresh(); }) }, saved ? "Save changes" : "Create estimate"))),
      totalsEl));
}

/* ---------- Printable customer copy ---------- */
// Shows prices only (never cost or markup), then opens the print dialog,
// where it can also be saved as a PDF.
function printEstimate(e, p) {
  if (!e) return;
  const client = clients.find(c => c.id === p.clientId);
  const page = h("div", { class: "doc" },
    h("div", { class: "doc-head" },
      h("div", {}, h("div", { class: "doc-company" }, (me && me.company) || ""), h("div", { class: "sub" }, me ? me.name + " · " + me.email : "")),
      h("div", { style: "text-align:right" }, h("div", { class: "doc-title" }, "Estimate #" + e.number),
        h("div", { class: "sub" }, new Date(e.updatedAt).toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" })))),
    h("div", { class: "doc-parties" },
      h("div", {}, h("div", { class: "doc-label" }, "Prepared for"),
        client ? [h("div", {}, client.name), client.email && h("div", {}, client.email), client.phone && h("div", {}, client.phone)] : h("div", { class: "sub" }, "—")),
      h("div", {}, h("div", { class: "doc-label" }, "Job"), h("div", {}, p.name), p.address && h("div", {}, p.address))),
    h("h2", { class: "doc-sub" }, e.title),
    h("table", {},
      h("thead", {}, h("tr", {}, h("th", {}, "Description"), h("th", { class: "num" }, "Qty"), h("th", {}, "Unit"), h("th", { class: "num" }, "Unit price"), h("th", { class: "num" }, "Amount"))),
      h("tbody", {}, e.items.map(i => h("tr", {},
        h("td", {}, i.description), h("td", { class: "num" }, String(+i.quantity)), h("td", {}, i.unit || ""),
        h("td", { class: "num" }, money(i.unitPrice)), h("td", { class: "num" }, money(i.amount))))),
      h("tfoot", {},
        h("tr", {}, h("td", { colspan: 4, class: "num" }, "Subtotal"), h("td", { class: "num" }, money(e.subtotal))),
        e.tax > 0 ? h("tr", {}, h("td", { colspan: 4, class: "num" }, "Sales tax on materials (" + (+e.taxPercent) + "%)"), h("td", { class: "num" }, money(e.tax))) : null,
        h("tr", { class: "doc-total" }, h("td", { colspan: 4, class: "num" }, "Total"), h("td", { class: "num" }, money(e.total))))),
    e.notes ? h("div", { class: "doc-notes" }, h("div", { class: "doc-label" }, "Notes"), h("p", {}, e.notes)) : null,
    h("div", { class: "doc-sign" },
      h("div", {}, h("span"), "Accepted by"), h("div", {}, h("span"), "Date")));
  const root = document.getElementById("print");
  root.replaceChildren(page);
  window.print();
}
