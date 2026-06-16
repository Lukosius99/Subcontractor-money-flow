const formatter = new Intl.NumberFormat("en-US", {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2
});

const compactMoney = new Intl.NumberFormat("en-US", {
  minimumFractionDigits: 0,
  maximumFractionDigits: 0
});

const percentFormatter = new Intl.NumberFormat("en-US", {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2
});

const monthFormatter = new Intl.DateTimeFormat("lt-LT", {
  month: "short",
  year: "numeric"
});

const importedAtFormatter = new Intl.DateTimeFormat("lt-LT", {
  month: "short",
  day: "numeric",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  hour12: false
});

const MONTH_SHORT = ["SAU", "VAS", "KOV", "BAL", "GEG", "BIR", "LIE", "RGP", "RGS", "SPA", "LAP", "GRD"];

const loading = document.querySelector("#loading");
const error = document.querySelector("#error");
const empty = document.querySelector("#empty");
const content = document.querySelector("#content");
const projectCodeHeading = document.querySelector("#projectCode");
const projectNameEl = document.querySelector("#projectName");
const breadcrumbProject = document.querySelector("#breadcrumbProject");
const projectMeta = document.querySelector("#projectMeta");
const latestImportMeta = document.querySelector("#latestImportMeta");
const latestImportText = document.querySelector("#latestImportText");
const totalsScopeNote = document.querySelector("#totalsScopeNote");
const kpiCards = document.querySelector("#kpiCards");
const objectScope = document.querySelector("#objectScope");
const objectScopeCards = document.querySelector("#objectScopeCards");
const tableCaption = document.querySelector("#tableCaption");
const tableShowing = document.querySelector("#tableShowing");
const subSearch = document.querySelector("#subSearch");
const subSearchClear = document.querySelector("#subSearchClear");
const toggleMonthsButton = document.querySelector("#toggleMonths");
const toggleEditButton = document.querySelector("#toggleEdit");
const exportCsvButton = document.querySelector("#exportCsv");
const contractTableHead = document.querySelector("#contractTableHead");
const contractTableBody = document.querySelector("#contractTableBody");
const contractTableFoot = document.querySelector("#contractTableFoot");
const objectValueSection = document.querySelector("#objectValueSection");
const objectValueSummary = document.querySelector("#objectValueSummary");
const objectValueContent = document.querySelector("#objectValueContent");
const importedRowsSummary = document.querySelector("#importedRowsSummary");
const importedRowsContent = document.querySelector("#importedRowsContent");
const exceptionsBadge = document.querySelector("#exceptionsBadge");
const exceptionsSummary = document.querySelector("#exceptionsSummary");
const exceptionsContent = document.querySelector("#exceptionsContent");
const drawer = document.querySelector("#drawer");
const drawerBackdrop = document.querySelector("#drawerBackdrop");

let monthGroups = [];
let allRows = [];
let currentProjectCode = "";
let currentMonthKeys = [];
let baseContractRows = [];
let projectDetail = {};
let selectedObjectNumber = "";
let isAllObjectsView = false;
let contractSort = { key: "status", direction: "desc" };
let linkSource = null;
let renderedContractRows = [];
let showMonths = false;
let editMode = false;
let objectAssignments = [];
let searchTerm = "";
let selectedSummaryKey = "";
let drawerTab = "monthly";
let firstContractRender = true;
let firstKpiRender = true;

const linkBanner = document.createElement("div");
linkBanner.className = "link-banner";
linkBanner.setAttribute("role", "status");
linkBanner.setAttribute("aria-live", "polite");
linkBanner.hidden = true;

const editBar = document.createElement("div");
editBar.className = "edit-bar";
editBar.setAttribute("role", "status");
editBar.hidden = true;

document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") return;
  if (linkSource) { cancelLinking(); return; }
  closeDrawer();
});

drawerBackdrop?.addEventListener("click", () => closeDrawer());

subSearch?.addEventListener("input", () => {
  searchTerm = subSearch.value.trim().toLowerCase();
  subSearchClear.hidden = searchTerm.length === 0;
  renderCurrentContractTable();
});
subSearchClear?.addEventListener("click", () => {
  subSearch.value = "";
  searchTerm = "";
  subSearchClear.hidden = true;
  renderCurrentContractTable();
  subSearch.focus();
});

toggleMonthsButton?.addEventListener("click", () => setShowMonths(!showMonths));

toggleEditButton?.addEventListener("click", () => setEditMode(!editMode));

exportCsvButton?.addEventListener("click", exportCsv);

function setShowMonths(value) {
  showMonths = Boolean(value);
  toggleMonthsButton?.classList.toggle("is-active", showMonths);
  toggleMonthsButton?.setAttribute("aria-pressed", String(showMonths));
  renderCurrentContractTable();
}

function setEditMode(value) {
  editMode = Boolean(value);
  toggleEditButton?.classList.toggle("is-active", editMode);
  toggleEditButton?.setAttribute("aria-pressed", String(editMode));
  if (editMode && linkSource) cancelLinking();
  renderCurrentContractTable();
}

/* Rows whose object number can be corrected: imported-only invoices (no matched
   contract) with a real subcontractor name. Editing happens in the All-objects
   view, where the Object column is shown. */
function isEditableObjectRow(summary) {
  return Boolean(summary.isImportedOnly) && summary.name !== "(Be subrangovo)";
}

async function assignObject(summary, rawTarget) {
  const target = String(rawTarget ?? "").trim();
  if (!target || target === summary.projectObjectNumber) {
    renderCurrentContractTable();
    return;
  }
  const confirmed = window.confirm(
    `Perkelti „${summary.name}" sąskaitas iš objekto ${summary.projectObjectNumber || "—"} į ${target}?\n\n`
    + `Tai pataiso klaidingą objekto numerį iš pirminio failo. Sąskaitos bus rodomos `
    + `objekte ${target}, kad galėtumėte jas susieti su sutartimi. Galėsite atšaukti vėliau.`
  );
  if (!confirmed) {
    renderCurrentContractTable();
    return;
  }
  try {
    const response = await fetch(`/api/projects/${encodeURIComponent(parentProjectCodeForApi())}/object-assignments`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        subcontractorName: summary.name,
        sourceObjectNumber: summary.projectObjectNumber,
        targetObjectNumber: target
      })
    });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok || !payload.assigned) {
      throw new Error(payload.error || `Užklausa nepavyko (${response.status}).`);
    }
    await loadProject();
  } catch (exception) {
    window.alert(`Nepavyko perkelti: ${exception.message}`);
    renderCurrentContractTable();
  }
}

async function removeObjectAssignment(assignment) {
  const confirmed = window.confirm(
    `Atšaukti objekto pakeitimą „${assignment.subcontractorName}" (${assignment.sourceObjectNumber} → ${assignment.targetObjectNumber})?`
  );
  if (!confirmed) return;
  try {
    const response = await fetch(
      `/api/projects/${encodeURIComponent(parentProjectCodeForApi())}/object-assignments/${encodeURIComponent(assignment.id)}`,
      { method: "DELETE" }
    );
    if (!response.ok) throw new Error(`Užklausa nepavyko (${response.status}).`);
    await loadProject();
  } catch (exception) {
    window.alert(`Nepavyko atšaukti: ${exception.message}`);
  }
}

function show(el) { if (el) el.hidden = false; }
function hide(el) { if (el) el.hidden = true; }
function setText(el, text) { if (el) el.textContent = text ?? ""; }
function money(value) { return formatter.format(Number(value ?? 0)); }
function moneyCompact(value) { return compactMoney.format(Number(value ?? 0)); }
function numberValue(value) { return Number(value ?? 0); }

function svgIcon(paths, viewBox = "0 0 24 24") {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", viewBox);
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "2");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");
  svg.innerHTML = paths;
  return svg;
}

const ICONS = {
  contracted: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M9 13h6M9 17h6"/>',
  invoiced: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="m9 15 2 2 4-4"/>',
  remaining: '<rect x="2" y="6" width="20" height="14" rx="2"/><path d="M16 13h4"/><path d="M2 10h20"/>',
  usage: '<path d="M12 2a10 10 0 1 0 10 10"/><path d="M12 6a6 6 0 1 0 6 6"/><path d="M12 12 19 5"/>',
  people: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
  warning: '<path d="m10.3 3.86-8.05 13.9A2 2 0 0 0 4 21h16a2 2 0 0 0 1.75-3.24l-8.05-13.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4"/><path d="M12 17h.01"/>',
  layers: '<path d="m12 2 9 5-9 5-9-5 9-5z"/><path d="m3 12 9 5 9-5"/><path d="m3 17 9 5 9-5"/>',
  cube: '<path d="M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="m3.3 7 8.7 5 8.7-5"/><path d="M12 22V12"/>',
  close: '<path d="M18 6 6 18"/><path d="m6 6 12 12"/>',
  external: '<path d="M15 3h6v6"/><path d="M10 14 21 3"/><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>',
  link: '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>',
  eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z"/><circle cx="12" cy="12" r="3"/>',
  info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4"/><path d="M12 8h.01"/>'
};

function isValidClientObjectNumber(value) {
  return /^P\d{4}-\d{2}$/i.test(String(value ?? "").trim());
}

function normalizeClientName(value) {
  let text = String(value ?? "").trim();
  if (!text) return "";
  const pipeIndex = text.indexOf("|");
  if (pipeIndex >= 0) text = pipeIndex === 0 ? "" : text.slice(0, pipeIndex);
  text = text
    .replace(/["'„“”«»]/g, " ")
    .replace(/[()[\]{}]/g, " ")
    .replace(/\s*[,.]\s*/g, " ")
    .replace(/\s+/g, " ")
    .trim()
    .toUpperCase();
  const legalForms = new Set(["AB", "UAB", "MB", "VŠĮ", "VšĮ".toUpperCase(), "IĮ", "VĮ"]);
  const tokens = text.split(" ").filter(Boolean);
  while (tokens.length && legalForms.has(tokens[0])) tokens.shift();
  while (tokens.length && legalForms.has(tokens[tokens.length - 1])) tokens.pop();
  return tokens.join(" ");
}

function cleanClientDisplayName(value) {
  let text = String(value ?? "").trim();
  if (!text) return "";
  const pipeIndex = text.indexOf("|");
  if (pipeIndex >= 0) text = pipeIndex === 0 ? "" : text.slice(0, pipeIndex);
  text = text
    .replace(/["'„“”«»]/g, " ")
    .replace(/\s*[,.]\s*/g, " ")
    .replace(/\s+/g, " ")
    .trim();
  const legalForms = new Set(["AB", "UAB", "MB", "VŠĮ", "VšĮ".toUpperCase(), "IĮ", "VĮ"]);
  const tokens = text.split(" ").filter(Boolean);
  while (tokens.length && legalForms.has(tokens[0].toUpperCase())) tokens.shift();
  while (tokens.length && legalForms.has(tokens[tokens.length - 1].toUpperCase())) tokens.pop();
  return clientDisplayCase(tokens.join(" ") || text);
}

function clientDisplayCase(value) {
  if (!/[A-Za-zÀ-ž]/.test(value) || /[a-zà-ž]/.test(value)) return value;
  return value.toLocaleLowerCase().replace(/\b\p{L}/gu, (letter) => letter.toLocaleUpperCase());
}

function cleanSubcontractorDisplayName(value) {
  let text = String(value ?? "").trim().replace(/\s+/g, " ");
  if (!text) return "(Be subrangovo)";
  const pipeIndex = text.indexOf("|");
  if (pipeIndex >= 0) {
    text = pipeIndex === 0 ? text.slice(pipeIndex + 1) : text.slice(0, pipeIndex);
  }
  text = text.trim();
  return text || "(Be subrangovo)";
}

function parseProjectObjectCode(value) {
  const cleaned = String(value ?? "").trim();
  const dashIndex = cleaned.lastIndexOf("-");
  if (dashIndex <= 0 || dashIndex === cleaned.length - 1) {
    return { parentProjectCode: cleaned, objectNumber: cleaned, objectCode: "" };
  }
  return {
    parentProjectCode: cleaned.slice(0, dashIndex),
    objectNumber: cleaned,
    objectCode: cleaned.slice(dashIndex + 1)
  };
}

function uniqueValues(values) {
  return [...new Set(values
    .map((v) => typeof v === "string" ? v.trim() : "")
    .filter(Boolean))];
}

function summarizeValues(values, fallback) {
  if (values.length === 0) return fallback;
  if (values.length <= 2) return values.join(", ");
  return `${values[0]}, ${values[1]} ir dar ${values.length - 2}`;
}

function renderProjectMeta() {
  const responsibles = uniqueValues([projectDetail.responsible, ...allRows.map((r) => r.responsible)]);
  const engineers = uniqueValues([projectDetail.engineer, ...allRows.map((r) => r.engineer)]);
  setText(projectNameEl, projectDetail.projectName || "");
  projectMeta.replaceChildren();
  const metaField = (label, value) => {
    const labelEl = document.createElement("span");
    labelEl.textContent = `${label}:`;
    const chip = document.createElement("span");
    chip.className = "meta-chip";
    chip.textContent = value;
    return [labelEl, chip];
  };
  const fields = [];
  if (responsibles.length > 0) fields.push(...metaField("Atsakingas", summarizeValues(responsibles, "-")));
  if (engineers.length > 0) fields.push(...metaField("Inžinierius", summarizeValues(engineers, "-")));
  projectMeta.append(...fields);
  projectMeta.hidden = fields.length === 0;
}

function monthKey(group) {
  return `${group.year}-${String(group.month).padStart(2, "0")}`;
}

function monthLabel(key) {
  const [year, month] = key.split("-").map(Number);
  return monthFormatter.format(new Date(year, month - 1, 1));
}

function periodRange(keys) {
  if (keys.length === 0) return "pasirinktas laikotarpis";
  if (keys.length === 1) return monthLabel(keys[0]);
  return `${monthLabel(keys[0])} – ${monthLabel(keys[keys.length - 1])}`;
}

/* A row needs review only when it is actually over its contracted amount
   (remaining < 0). Being at or near 100% with money still left is on track.
   Missing-contract rows (invoices but no contract) are over by definition. */
function deriveStatus(contracted, invoiced) {
  if (numberValue(contracted) <= 0) return numberValue(invoiced) > 0 ? "Trūksta sutarties" : "Pagal planą";
  return numberValue(invoiced) > numberValue(contracted) ? "Viršyta riba" : "Pagal planą";
}

function statusClass(status) {
  if (status === "Trūksta sutarties") return { label: "Trūksta sutarties", className: "warn", pill: "pill-warn", usage: "warn", rank: 3 };
  if (status === "Viršyta riba") return { label: "Viršyta riba", className: "over", pill: "pill-danger", usage: "over", rank: 4 };
  if (status === "Pasiekta riba") return { label: "Pasiekta riba", className: "warn", pill: "pill-danger", usage: "over", rank: 2 };
  if (status === "Artėja prie ribos") return { label: "Artėja prie ribos", className: "warn", pill: "pill-warn", usage: "warn", rank: 1 };
  return { label: "Pagal planą", className: "ok", pill: "pill-ok", usage: "ok", rank: 0 };
}

function appendTextCell(row, text, className) {
  const cell = document.createElement("td");
  cell.textContent = text ?? "";
  if (className) cell.className = className;
  row.append(cell);
  return cell;
}

function appendMoneyCell(row, value, options = {}) {
  const cell = appendTextCell(row, money(value), "col-money");
  if (options.emphasize) cell.classList.add("is-strong");
  if (Number(value) < 0 || options.danger) cell.classList.add("is-negative");
  if (Number(value ?? 0) === 0 && !options.emphasize) cell.classList.add("cell-quiet");
  return cell;
}

function lastInvoiceKey(monthly) {
  let last = "";
  for (const [key, amount] of monthly) {
    if (numberValue(amount) !== 0 && key > last) last = key;
  }
  return last;
}

function buildContractRows(monthKeys) {
  return (projectDetail.contractRows ?? []).map((row) => {
    const monthly = new Map(monthKeys.map((m) => [m, 0]));
    for (const amount of row.monthly ?? []) {
      monthly.set(`${amount.year}-${String(amount.month).padStart(2, "0")}`, numberValue(amount.amountWithoutVat));
    }
    return {
      name: cleanSubcontractorDisplayName(row.subcontractorName),
      projectObjectNumber: row.projectObjectNumber || "",
      objectNumber: row.objectNumber || "",
      objectPrintCode: row.objectPrintCode || "",
      departmentCode: row.departmentCode || "",
      objectName: row.objectName || "",
      contracted: numberValue(row.contracted),
      invoiced: numberValue(row.invoiced),
      remaining: numberValue(row.remaining),
      usagePercent: numberValue(row.usagePercent),
      status: deriveStatus(row.contracted, row.invoiced),
      warning: row.warning || "",
      isImportedOnly: Boolean(row.isImportedOnly),
      rowKey: row.rowKey || "",
      links: row.links ?? [],
      monthly,
      lastInvoice: lastInvoiceKey(monthly)
    };
  });
}

function summaryKey(summary) {
  return summary.rowKey || `${summary.projectObjectNumber}|${summary.name}`.toLowerCase();
}

function findSummaryByKey(key) {
  return baseContractRows.find((row) => summaryKey(row) === key) ?? null;
}

function parentProjectCodeForApi() {
  return projectDetail.parentProjectCode || parseProjectObjectCode(currentProjectCode).parentProjectCode;
}

/* ─── Latest import meta ──────────────────────────────────────────────── */

async function loadImportStatus() {
  try {
    const response = await fetch("/api/imports/monthly-flow/status", { cache: "no-store" });
    if (!response.ok) return;
    const payload = await response.json();
    const importedAt = payload?.latestImport?.importedAt;
    if (!importedAt) return;
    setText(latestImportText, `Paskutinis importas: ${importedAtFormatter.format(new Date(importedAt))}`);
    show(latestImportMeta);
    setText(document.querySelector("#footerImportText"), `Paskutinis importas: ${importedAtFormatter.format(new Date(importedAt))}`);
  } catch {
    /* meta line is optional */
  }
}

/* ─── Manual contract linking ─────────────────────────────────────────── */

function canStartLink(summary) {
  return summary.isImportedOnly
    && Boolean(summary.objectNumber)
    && summary.name !== "(Be subrangovo)";
}

function isEligibleLinkTarget(summary) {
  return Boolean(linkSource)
    && !summary.isImportedOnly
    && Boolean(summary.rowKey)
    && summary.projectObjectNumber.toLowerCase() === linkSource.projectObjectNumber.toLowerCase();
}

function startLinking(summary) {
  linkSource = summary;
  updateLinkingVisuals();
}

function cancelLinking() {
  if (!linkSource) return;
  linkSource = null;
  updateLinkingVisuals();
}

function updateLinkingVisuals() {
  let targetCount = 0;
  for (const { tr, summary } of renderedContractRows) {
    const isTarget = isEligibleLinkTarget(summary);
    if (isTarget) targetCount += 1;
    tr.classList.toggle("link-target", isTarget);
    tr.classList.toggle("link-source-active", summary === linkSource);
  }
  if (linkSource) {
    const icon = svgIcon(ICONS.link);
    const text = document.createElement("span");
    text.className = "link-banner-text";
    if (targetCount > 0) {
      const step = document.createElement("b");
      step.className = "link-banner-step";
      step.textContent = "2 žingsnis iš 2";
      const name = document.createElement("b");
      name.textContent = `„${linkSource.name}“`;
      text.append(step, " — dabar spustelėkite paryškintą subrangovą žemiau, kad sujungtumėte ", name, " sąskaitas su juo.");
    } else {
      const name = document.createElement("b");
      name.textContent = `„${linkSource.name}“`;
      text.append("Objekte ", linkSource.projectObjectNumber, " nėra subrangovo, kuris galėtų priimti ", name, " — pakeiskite objekto sritį arba patikrinkite duomenis.");
    }
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.className = "btn link-banner-cancel";
    cancel.textContent = "Atšaukti";
    cancel.addEventListener("click", () => cancelLinking());
    linkBanner.replaceChildren(icon, text, cancel);
    linkBanner.hidden = false;
  } else {
    linkBanner.replaceChildren();
    linkBanner.hidden = true;
  }
}

async function completeLink(targetSummary) {
  const source = linkSource;
  if (!source || !isEligibleLinkTarget(targetSummary)) return;
  const confirmed = window.confirm(
    `Susieti „${source.name}“ su „${targetSummary.name}“ objekte ${targetSummary.projectObjectNumber}?\n\n`
    + `Naudokite tai, kai abu pavadinimai reiškia tą patį subrangovą, bet vienas įrašytas kitaip. `
    + `Sąskaitos, importuotos kaip „${source.name}“, nuo šiol bus skaičiuojamos prie „${targetSummary.name}“, `
    + `o būsimi importai bus susiejami automatiškai. Tai galėsite atšaukti vėliau Susiejimo stulpelyje.`
  );
  cancelLinking();
  if (!confirmed) return;
  try {
    const response = await fetch(`/api/projects/${encodeURIComponent(parentProjectCodeForApi())}/contract-links`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        sourceSubcontractorName: source.name,
        sourceObjectNumber: source.projectObjectNumber,
        targetContractRowKey: targetSummary.rowKey
      })
    });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok || !payload.linked) {
      throw new Error(payload.error || `Link request failed with status ${response.status}.`);
    }
    await loadProject();
  } catch (exception) {
    window.alert(`Nepavyko susieti eilučių: ${exception.message}`);
  }
}

async function removeLink(link) {
  const confirmed = window.confirm(`Atsieti „${link.sourceName}“ sąskaitas nuo sutarties „${link.targetName}“?`);
  if (!confirmed) return;
  try {
    const response = await fetch(
      `/api/projects/${encodeURIComponent(parentProjectCodeForApi())}/contract-links/${encodeURIComponent(link.id)}`,
      { method: "DELETE" }
    );
    if (!response.ok) throw new Error(`Unlink request failed with status ${response.status}.`);
    await loadProject();
  } catch (exception) {
    window.alert(`Nepavyko atsieti eilučių: ${exception.message}`);
  }
}

/* ─── KPI cards ───────────────────────────────────────────────────────── */

function scopeTotals(rows) {
  const totals = rows.reduce((acc, row) => {
    acc.contracted += row.contracted;
    acc.invoiced += row.invoiced;
    if (row.warning || row.status !== "Pagal planą") acc.warnings += 1;
    return acc;
  }, { contracted: 0, invoiced: 0, warnings: 0 });
  totals.remaining = totals.contracted - totals.invoiced;
  totals.usage = totals.contracted > 0 ? totals.invoiced / totals.contracted * 100 : 0;
  totals.count = rows.length;
  totals.status = deriveStatus(totals.contracted, totals.invoiced);
  return totals;
}

/* widths set before insertion animate from zero on the next frame */
function animateFill(fill) {
  const target = fill.style.width;
  fill.style.width = "0%";
  requestAnimationFrame(() => requestAnimationFrame(() => { fill.style.width = target; }));
  return fill;
}

function totalsCell({ label, value, unit, tone, meta }) {
  const cell = document.createElement("div");
  cell.className = "totals-cell";

  const valueEl = document.createElement("div");
  valueEl.className = `totals-value${tone ? ` is-${tone}` : ""}${String(value).length > 12 ? " is-long" : ""}`;
  valueEl.textContent = value;
  valueEl.title = unit ? `${value} ${unit}` : String(value);
  if (unit) {
    const em = document.createElement("em");
    em.textContent = unit;
    valueEl.append(em);
  }

  const labelEl = document.createElement("div");
  labelEl.className = "totals-label";
  labelEl.textContent = label;

  cell.append(valueEl, labelEl);
  if (meta) {
    const metaEl = document.createElement("div");
    metaEl.className = "totals-meta";
    metaEl.append(meta);
    cell.append(metaEl);
  }
  return cell;
}

function totalsMetaText(text, tone) {
  const span = document.createElement("span");
  span.className = `totals-meta-text${tone ? ` is-${tone}` : ""}`;
  span.textContent = text;
  return span;
}

/* Contracted project value (client contract / "P vertė") for the current
   scope. projectObjectValues is already scoped server-side to the project or
   the selected object, so summing it = project total (all objects) or the
   single object's value — matching the Object/Client value tracking panel. */
function scopedProjectValue() {
  return (projectDetail?.projectObjectValues ?? [])
    .filter((value) => isValidClientObjectNumber(value.objectNumber))
    .reduce((sum, value) => sum + numberValue(value.projectValueAmount), 0);
}

/* Invoiced to the client (SMD) for the current scope, mirroring
   scopedProjectValue: smdCustomerRows is already scoped server-side. */
function scopedClientInvoiced() {
  return (projectDetail?.smdCustomerRows ?? [])
    .filter((row) => isValidClientObjectNumber(row.objectNumber))
    .reduce((sum, row) => sum + numberValue(row.clientMonthlyAmount), 0);
}

/* Usage bar + word, reused by both KPI clusters. Returns the meta node and the
   fill element so the caller can animate it after insertion. */
function kpiUsageMeta(usagePercent, usageKind, word) {
  const meta = document.createElement("span");
  meta.className = "usage";
  const bar = document.createElement("span");
  bar.className = "usage-bar";
  const fill = document.createElement("span");
  fill.className = `usage-fill ${usageKind}`.trim();
  fill.style.width = `${Math.min(Math.max(usagePercent, 0), 100)}%`;
  bar.append(fill);
  const num = document.createElement("span");
  num.className = "usage-num";
  num.style.minWidth = "0";
  num.textContent = word;
  if (usageKind === "warn") num.classList.add("warn");
  if (usageKind === "over") num.classList.add("over");
  meta.append(bar, num);
  return { meta, fill };
}

/* One labelled cluster in the totals band (e.g. Užsakovas vs Subrangovai), so
   each percentage sits next to its own contracted base instead of borrowing
   the neighbour's. */
function totalsGroup(label, modifier, cells) {
  const group = document.createElement("div");
  group.className = `totals-group ${modifier}`;
  const labelEl = document.createElement("div");
  labelEl.className = "totals-group-label";
  labelEl.textContent = label;
  const cellsWrap = document.createElement("div");
  cellsWrap.className = "totals-group-cells";
  cellsWrap.append(...cells);
  group.append(labelEl, cellsWrap);
  return group;
}

function renderKpis() {
  const totals = scopeTotals(baseContractRows);
  const scopeName = selectedObjectNumber || "Visi objektai";
  setText(totalsScopeNote, `${scopeName} · be PVM`);

  const status = statusClass(totals.status);
  const hasContract = totals.contracted > 0;

  /* Užsakovas (client) side: project value contracted vs invoiced to client. */
  const projectValue = scopedProjectValue();
  const clientInvoiced = scopedClientInvoiced();
  const hasClientData = projectValue > 0 || clientInvoiced > 0;
  const clientRemaining = projectValue - clientInvoiced;
  const clientUsage = projectValue > 0 ? clientInvoiced / projectValue * 100 : 0;
  const clientUsageKind = projectValue <= 0
    ? "" : clientInvoiced > projectValue ? "over" : clientUsage >= 90 ? "warn" : "ok";

  const animatedFills = [];

  /* Subrangovai (subcontractor) side: contracted to subs vs invoiced by subs. */
  const subUsage = kpiUsageMeta(
    totals.usage,
    hasContract ? status.usage : "",
    hasContract ? status.label : "Kol kas nėra sutarties"
  );
  animatedFills.push(subUsage.fill);

  const subCells = [
    totalsCell({
      label: "Sutarta", value: hasContract ? money(totals.contracted) : "—",
      unit: hasContract ? "€" : undefined,
      tone: hasContract ? undefined : "quiet",
      meta: hasContract ? null : totalsMetaText("Nėra subrangų sutarčių")
    }),
    totalsCell({
      label: "Sąskaitose", value: money(totals.invoiced), unit: "€",
      tone: totals.invoiced === 0 ? "quiet" : undefined,
      meta: hasContract && totals.invoiced !== 0
        ? totalsMetaText(`${percentFormatter.format(totals.usage)}% nuo sutarto`)
        : null
    }),
    totalsCell({
      label: "Likutis", value: money(totals.remaining), unit: "€",
      tone: totals.remaining < 0 ? "danger" : undefined,
      meta: totals.remaining < 0
        ? totalsMetaText(`Viršyta ${money(Math.abs(totals.remaining))} €`, "danger")
        : (hasContract ? totalsMetaText(`liko ${money(totals.remaining)} €`) : null)
    }),
    totalsCell({
      label: "Panaudota",
      value: hasContract ? `${percentFormatter.format(totals.usage)}%` : "—",
      tone: hasContract ? undefined : "quiet",
      meta: subUsage.meta
    }),
    totalsCell({ label: "Subrangovai", value: String(totals.count) }),
    totalsCell({
      label: "Įspėjimai", value: String(totals.warnings),
      tone: totals.warnings > 0 ? "danger" : "quiet",
      meta: totals.warnings > 0 ? totalsMetaText("Reikia peržiūrėti", "danger") : null
    })
  ];

  const groups = [];
  if (hasClientData) {
    let clientInvoicedMeta;
    if (projectValue > 0) {
      const cu = kpiUsageMeta(clientUsage, clientUsageKind, `${percentFormatter.format(clientUsage)}%`);
      animatedFills.push(cu.fill);
      clientInvoicedMeta = cu.meta;
    } else {
      clientInvoicedMeta = totalsMetaText("Nėra projekto vertės palyginimui");
    }
    const clientCells = [
      totalsCell({
        label: "Projekto vertė",
        value: projectValue > 0 ? money(projectValue) : "—",
        unit: projectValue > 0 ? "€" : undefined,
        tone: projectValue > 0 ? undefined : "quiet",
        meta: projectValue > 0
          ? totalsMetaText(
              clientRemaining < 0
                ? `Užsakovui viršyta ${money(Math.abs(clientRemaining))} €`
                : `liko ${money(clientRemaining)} €`,
              clientRemaining < 0 ? "danger" : undefined)
          : totalsMetaText("Projekto vertė neimportuota")
      }),
      totalsCell({
        label: "Klientui sąskaitose",
        value: money(clientInvoiced), unit: "€",
        tone: clientInvoiced === 0 ? "quiet" : undefined,
        meta: clientInvoicedMeta
      })
    ];
    groups.push(totalsGroup("Užsakovas", "is-client", clientCells));
  }
  groups.push(totalsGroup("Subrangovai", "is-sub", subCells));

  kpiCards.classList.toggle("is-split", hasClientData);
  kpiCards.replaceChildren(...groups);

  if (firstKpiRender) {
    kpiCards.querySelectorAll(".totals-cell").forEach((cell, i) => {
      cell.classList.add("cell-enter");
      cell.style.animationDelay = `${i * 28}ms`;
    });
    firstKpiRender = false;
  }
  animatedFills.forEach(animateFill);

  const hint = document.querySelector("#totalsHint");
  const hintText = document.querySelector("#totalsHintText");
  if (hint && hintText) {
    const noInvoicesYet = totals.invoiced === 0 && currentMonthKeys.length === 0;
    hintText.textContent = "Kol kas neimportuota mėnesinių sąskaitų. Sąskaitų sumos, panaudojimas ir įspėjimai atsiras po pirmojo mėnesinio importo.";
    hint.hidden = !noInvoicesYet;
  }
}

/* ─── Needs-attention strip ───────────────────────────────────────────── */
/* One calm line summarizing what needs review; hidden when everything is
   on track. Derived from the same rows the table and exceptions use. */

function renderAttentionStrip() {
  const strip = document.querySelector("#attentionStrip");
  if (!strip) return;

  const issues = baseContractRows.filter((row) => row.warning || row.status !== "Pagal planą");
  if (issues.length === 0) { hide(strip); return; }

  const overAmount = baseContractRows
    .filter((row) => row.status === "Viršyta riba")
    .reduce((sum, row) => sum + Math.abs(Math.min(row.remaining, 0)), 0);
  const missingCount = baseContractRows.filter((row) => row.status === "Trūksta sutarties").length;

  strip.replaceChildren();
  strip.append(svgIcon(ICONS.warning));

  const parts = [];
  const reviewPart = document.createElement("span");
  const reviewCount = document.createElement("b");
  reviewCount.textContent = String(issues.length);
  reviewPart.append("reikia peržiūrėti ", reviewCount, ` subrangov${issues.length === 1 ? "ą" : "us"}`);
  parts.push(reviewPart);

  if (overAmount > 0) {
    const overPart = document.createElement("span");
    overPart.className = "is-danger-text";
    overPart.textContent = `Riba viršyta ${money(overAmount)} €`;
    parts.push(overPart);
  }
  if (missingCount > 0) {
    const missingPart = document.createElement("span");
    const missingB = document.createElement("b");
    missingB.textContent = String(missingCount);
    missingPart.append(missingB, ` be sutarties (geltonos eilutės lentelėje)`);
    parts.push(missingPart);
  }

  parts.forEach((part, index) => {
    if (index > 0) {
      const sep = document.createElement("span");
      sep.className = "attention-sep";
      sep.setAttribute("aria-hidden", "true");
      sep.textContent = "·";
      strip.append(sep);
    }
    strip.append(part);
  });

  const review = document.createElement("button");
  review.type = "button";
  review.className = "btn-link";
  review.textContent = "Peržiūrėti išimtis";
  review.addEventListener("click", () => {
    const section = document.querySelector("#exceptionsSection");
    if (section) {
      section.open = true;
      section.scrollIntoView({ behavior: "smooth", block: "start" });
    }
  });
  strip.append(review);
  show(strip);
}

/* ─── Object scope cards ──────────────────────────────────────────────── */

function scopeStatusModifier(summary, contracted) {
  if (contracted <= 0) return "status-neutral";
  if (summary.status === "Viršyta riba") return "status-danger";
  if (summary.status === "Pasiekta riba" || summary.status === "Artėja prie ribos") return "status-warn";
  if (summary.status === "Trūksta sutarties") return "status-neutral";
  return "";
}

function scopeUsageClass(status) {
  if (status === "Viršyta riba") return "over";
  if (status === "Pasiekta riba" || status === "Artėja prie ribos" || status === "Trūksta sutarties") return "warn";
  return "ok";
}

function scopeCard({ title, sub, href, icon, summary, isSelected }) {
  const card = document.createElement("a");
  const scopeContracted = numberValue(summary.contractedAmount);
  card.className = ["scope-card", scopeStatusModifier(summary, scopeContracted), isSelected ? "is-active" : ""]
    .filter(Boolean).join(" ");
  card.href = href;
  if (isSelected) card.setAttribute("aria-current", "true");

  const head = document.createElement("div");
  head.className = "scope-card-head";
  const chip = document.createElement("span");
  chip.className = "scope-card-icon";
  chip.append(svgIcon(ICONS[icon]));
  const titleWrap = document.createElement("div");
  titleWrap.style.minWidth = "0";
  const titleEl = document.createElement("span");
  titleEl.className = "scope-card-title";
  titleEl.textContent = title;
  titleWrap.append(titleEl);
  if (sub) {
    const subEl = document.createElement("span");
    subEl.className = "scope-card-sub";
    subEl.textContent = sub;
    titleWrap.append(subEl);
  }
  head.append(chip, titleWrap);

  const statusModifier = scopeStatusModifier(summary, scopeContracted);
  if (statusModifier === "status-danger" || statusModifier === "status-warn") {
    const dot = document.createElement("span");
    dot.className = `dot ${statusModifier === "status-danger" ? "dot-danger" : "dot-warn"}`;
    dot.title = summary.status;
    dot.setAttribute("role", "img");
    dot.setAttribute("aria-label", summary.status);
    dot.style.marginLeft = "auto";
    head.append(dot);
  } else if (isSelected) {
    const check = svgIcon('<path d="M20 6 9 17l-5-5"/>');
    check.classList.add("scope-card-check");
    head.append(check);
  }

  const contracted = numberValue(summary.contractedAmount);
  const invoiced = numberValue(summary.amountWithoutVat);
  const usage = contracted > 0 ? invoiced / contracted * 100 : 0;

  /* invoices without any contracted amount: flag it instead of showing a
     confusing "0 contracted" next to real invoiced money */
  if (contracted <= 0 && invoiced > 0 && title !== "Visi objektai") {
    const flag = document.createElement("span");
    flag.className = "scope-card-flag";
    flag.textContent = "Tik mėnesiniai duomenys";
    flag.title = "Sąskaitos importuotos iš mėnesinių duomenų, bet šiam objektui dar nėra sutartos sumos.";
    card.append(head, flag);
  } else {
    card.append(head);
  }

  const stats = document.createElement("div");
  stats.className = "scope-card-stats";
  const statDefs = [
    { label: "Sutarta", value: `${moneyCompact(contracted)} €` },
    { label: "Sąskaitose", value: `${moneyCompact(invoiced)} €` }
  ];
  for (const def of statDefs) {
    const stat = document.createElement("div");
    stat.className = "scope-card-stat";
    const label = document.createElement("span");
    label.className = "scope-card-stat-label";
    label.textContent = def.label;
    const value = document.createElement("span");
    value.className = "scope-card-stat-value";
    value.textContent = def.value;
    stat.append(label, value);
    stats.append(stat);
  }

  const usageStat = document.createElement("div");
  usageStat.className = "scope-card-stat is-usage";
  const usageLabel = document.createElement("span");
  usageLabel.className = "scope-card-stat-label";
  usageLabel.textContent = "Panaudota";
  const usageWrap = document.createElement("div");
  usageWrap.className = "usage";
  const usageBar = document.createElement("div");
  usageBar.className = "usage-bar";
  const usageClass = scopeUsageClass(summary.status);
  const usageFill = document.createElement("span");
  usageFill.className = `usage-fill ${contracted > 0 ? usageClass : ""}`.trim();
  usageFill.style.width = `${contracted > 0 ? Math.min(Math.max(usage, 0), 100) : 0}%`;
  usageBar.append(usageFill);
  const usageNum = document.createElement("span");
  usageNum.className = "usage-num";
  if (contracted > 0 && usageClass === "warn") usageNum.classList.add("warn");
  if (contracted > 0 && usageClass === "over") usageNum.classList.add("over");
  usageNum.textContent = contracted > 0 ? `${percentFormatter.format(usage)}%` : "—";
  usageWrap.append(usageBar, usageNum);
  usageStat.append(usageLabel, usageWrap);
  stats.append(usageStat);
  if (contracted > 0) animateFill(usageFill);

  card.append(stats);
  return card;
}

function renderObjectScope() {
  const objects = projectDetail.objects ?? [];
  if (objects.length <= 1) { hide(objectScope); return; }

  const parentProjectCode = projectDetail.parentProjectCode || currentProjectCode;
  const allSummary = objects.reduce((acc, item) => {
    acc.contractedAmount += numberValue(item.contractedAmount);
    acc.amountWithoutVat += numberValue(item.amountWithoutVat);
    acc.remaining += numberValue(item.remaining);
    if (item.status === "Viršyta riba") acc.status = "Viršyta riba";
    else if (acc.status !== "Viršyta riba" && item.status === "Trūksta sutarties") acc.status = "Trūksta sutarties";
    else if (!["Viršyta riba", "Trūksta sutarties"].includes(acc.status) && (item.status === "Artėja prie ribos" || item.status === "Pasiekta riba")) {
      acc.status = item.status;
    }
    return acc;
  }, { contractedAmount: 0, amountWithoutVat: 0, remaining: 0, status: "Pagal planą" });

  const cards = [
    scopeCard({
      title: "Visi objektai",
      sub: `${objects.length} objekt${objects.length === 1 ? "as" : "ai"} · visi padaliniai`,
      href: `/project.html?projectCode=${encodeURIComponent(parentProjectCode)}`,
      icon: "layers",
      summary: allSummary,
      isSelected: !selectedObjectNumber
    }),
    ...objects.map((item) => scopeCard({
      title: item.objectNumber,
      sub: [item.departmentCode, item.objectPrintCode].filter(Boolean).join(" · ") || " ",
      href: `/project.html?projectCode=${encodeURIComponent(parentProjectCode)}&objectNumber=${encodeURIComponent(item.objectNumber)}`,
      icon: "cube",
      summary: item,
      isSelected: selectedObjectNumber.toLowerCase() === String(item.objectNumber).toLowerCase()
    }))
  ];

  objectScopeCards.replaceChildren(...cards);
  show(objectScope);
}

/* ─── Subcontractors summary table ────────────────────────────────────── */

function headerButton(text, key, className, help) {
  const th = document.createElement("th");
  if (className) th.className = className;
  if (!key) {
    if (help) {
      const label = document.createElement("span");
      label.className = "th-help";
      label.append(text);
      const icon = svgIcon(ICONS.info);
      icon.classList.add("th-help-icon");
      label.append(icon);
      label.title = help;
      th.append(label);
    } else {
      th.textContent = text;
    }
    return th;
  }
  const button = document.createElement("button");
  button.type = "button";
  button.className = "sort-button";
  button.dataset.sort = key;
  button.textContent = text;
  if (key === contractSort.key) button.dataset.direction = contractSort.direction;
  th.setAttribute("aria-sort", key === contractSort.key
    ? (contractSort.direction === "asc" ? "ascending" : "descending")
    : "none");
  button.addEventListener("click", () => {
    if (contractSort.key === key) {
      contractSort.direction = contractSort.direction === "asc" ? "desc" : "asc";
    } else {
      contractSort = { key, direction: "asc" };
    }
    renderCurrentContractTable();
  });
  th.append(button);
  return th;
}

function tableColumns(monthKeys) {
  return [
    ...(isAllObjectsView ? [{ text: "Objektas", key: "object", cls: "" }] : []),
    { text: "Padalinys", key: "department", cls: "" },
    { text: "Subrangovas", key: "name", cls: "" },
    { text: "Sutarta €", key: "contracted", cls: "col-money col-sep" },
    { text: "Sąskaitose €", key: "invoiced", cls: "col-money" },
    { text: "Likutis €", key: "remaining", cls: "col-money" },
    { text: "Panaudota", key: "usage", cls: "col-money" },
    { text: "Būsena", key: "status", cls: "col-sep" },
    { text: "Paskutinė sąskaita", key: "lastInvoice", cls: "" },
    { text: "Susiejimas", key: "", cls: "", help: "Susiekite subrangovą, kurio importuotas pavadinimas automatiškai neatitiko sutarties — dažniausiai dėl pavadinimo rašybos klaidos." },
    ...(showMonths ? [
      ...monthKeys.map((k, i) => ({ text: monthLabel(k), key: `month:${k}`, cls: i === 0 ? "col-money col-sep" : "col-money" })),
      { text: "Iš viso sąsk. €", key: "total", cls: "col-money" }
    ] : [])
  ];
}

function renderTableHead(monthKeys) {
  const row = document.createElement("tr");
  row.replaceChildren(...tableColumns(monthKeys).map(({ text, key, cls, help }) => headerButton(text, key, cls, help)));
  contractTableHead.replaceChildren(row);
}

function createUsageCell(usage, status) {
  const cell = document.createElement("td");
  cell.className = "col-money";

  const wrap = document.createElement("span");
  wrap.className = "usage";

  const num = document.createElement("span");
  num.className = `usage-num ${status.usage === "over" ? "over" : status.usage === "warn" ? "warn" : ""}`;
  num.textContent = status.label === "Trūksta sutarties" ? "-" : `${percentFormatter.format(usage)}%`;

  const bar = document.createElement("span");
  bar.className = "usage-bar";
  bar.title = status.label === "Trūksta sutarties" ? "Kol kas nėra sutartos sumos palyginimui." : `panaudota ${percentFormatter.format(usage)}%`;
  const fill = document.createElement("span");
  fill.className = `usage-fill ${status.usage}`;
  fill.style.width = `${Math.min(Math.max(usage, 0), 100)}%`;
  bar.append(fill);

  wrap.append(bar, num);
  cell.append(wrap);
  return cell;
}

function createStatusCell(status) {
  const cell = document.createElement("td");
  if (status.rank === 0) {
    const quiet = document.createElement("span");
    quiet.className = "status-quiet";
    quiet.textContent = status.label;
    cell.append(quiet);
    return cell;
  }
  const pill = document.createElement("span");
  pill.className = `pill ${status.pill}`;
  pill.textContent = status.label;
  cell.append(pill);
  return cell;
}

function createMatchCell(summary) {
  const cell = document.createElement("td");

  if (canStartLink(summary)) {
    /* Invoices imported under a name no contract matched — usually a typo in
       the subcontractor name. The filled amber chip reads as an action, not a
       label, and the wording says plainly what clicking it does. */
    const badge = document.createElement("button");
    badge.type = "button";
    badge.className = "match-badge is-connect";
    badge.append(svgIcon(ICONS.link), "Susieti");
    badge.title = `„${summary.name}“ neatitiko jokios sutarties — tikriausiai rašybos klaida pavadinime. `
      + `Spustelėkite, kad susietumėte jo sąskaitas su tinkamu subrangovu objekte ${summary.projectObjectNumber}.`;
    badge.addEventListener("click", (event) => {
      event.stopPropagation();
      startLinking(summary);
    });
    cell.append(badge);
    return cell;
  }

  if (!summary.isImportedOnly && summary.links.length > 0) {
    const badge = document.createElement("button");
    badge.type = "button";
    badge.className = "match-badge is-alias";
    badge.append(svgIcon(ICONS.link), `susieta: ${summary.links.length}`);
    badge.title = `Taip pat įskaičiuoja sąskaitas, įrašytas kaip: ${summary.links.map((link) => link.sourceName).join(", ")}. Spustelėkite, kad peržiūrėtumėte ar atsietumėte.`;
    badge.addEventListener("click", (event) => {
      event.stopPropagation();
      openDrawer(summary, "matching");
    });
    cell.append(badge);
    return cell;
  }

  const none = document.createElement("span");
  none.className = "match-none";
  none.textContent = "—";
  none.title = "Susieta automatiškai pagal pavadinimą. Nieko daryti nereikia.";
  cell.append(none);
  return cell;
}

function contractSortValue(row, key) {
  if (key === "object") return row.projectObjectNumber;
  if (key === "department") return row.departmentCode;
  if (key === "name") return row.name.toLocaleLowerCase();
  if (key === "contracted") return row.contracted;
  if (key === "invoiced" || key === "total") return row.invoiced;
  if (key === "remaining") return row.remaining;
  if (key === "usage") return row.usagePercent;
  if (key === "status") return statusClass(row.status).rank;
  if (key === "lastInvoice") return row.lastInvoice;
  if (key.startsWith("month:")) return numberValue(row.monthly.get(key.slice(6)));
  return "";
}

function sortedContractRows(rows) {
  const { key, direction } = contractSort;
  const multiplier = direction === "asc" ? 1 : -1;
  return [...rows].sort((a, b) => {
    const av = contractSortValue(a, key);
    const bv = contractSortValue(b, key);
    if (typeof av === "number" && typeof bv === "number") return (av - bv) * multiplier;
    return String(av).localeCompare(String(bv), undefined, { numeric: true, sensitivity: "base" }) * multiplier;
  });
}

function filteredContractRows() {
  if (!searchTerm) return baseContractRows;
  return baseContractRows.filter((row) =>
    row.name.toLowerCase().includes(searchTerm)
    || row.projectObjectNumber.toLowerCase().includes(searchTerm)
    || row.departmentCode.toLowerCase().includes(searchTerm)
    || row.objectName.toLowerCase().includes(searchTerm));
}

/* Object cell for the All-objects view. In edit mode, imported-only invoice rows
   get an inline text input so a wrong/missing object number can be corrected. */
function appendObjectCell(tr, summary) {
  const cell = document.createElement("td");
  cell.className = "col-code";

  if (editMode && isEditableObjectRow(summary)) {
    const input = document.createElement("input");
    input.type = "text";
    input.className = "object-edit-input";
    input.value = summary.projectObjectNumber || "";
    input.setAttribute("aria-label", `Objekto numeris: ${summary.name}`);
    input.title = "Įveskite teisingą objekto numerį ir paspauskite Enter";
    input.addEventListener("click", (event) => event.stopPropagation());
    input.addEventListener("keydown", (event) => {
      event.stopPropagation();
      if (event.key === "Enter") { event.preventDefault(); input.blur(); }
      else if (event.key === "Escape") { input.value = summary.projectObjectNumber || ""; input.blur(); }
    });
    input.addEventListener("change", () => assignObject(summary, input.value));
    cell.append(input);
    tr.append(cell);
    return;
  }

  cell.textContent = summary.projectObjectNumber || "-";
  if (summary.objectPrintCode) {
    const sub = document.createElement("span");
    sub.className = "object-printcode";
    sub.textContent = summary.objectPrintCode;
    cell.append(document.createElement("br"), sub);
  }
  tr.append(cell);
}

/* Edit-mode banner: instructions + removable chips to undo each object move. */
function updateEditBar() {
  if (!editMode) {
    editBar.replaceChildren();
    editBar.hidden = true;
    return;
  }
  editBar.replaceChildren();
  editBar.append(svgIcon(ICONS.info));

  const text = document.createElement("span");
  text.className = "edit-bar-text";
  text.textContent = isAllObjectsView
    ? "Redagavimo režimas: pataisykite klaidingą objekto numerį importuotose (geltonose) eilutėse, tada susiekite subrangovus."
    : "Objektų numerius galima keisti tik „Visi objektai“ rodinyje — pasirinkite jį objektų srityje viršuje.";
  editBar.append(text);

  if (objectAssignments.length > 0) {
    const chips = document.createElement("div");
    chips.className = "edit-bar-chips";
    for (const assignment of objectAssignments) {
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "edit-undo-chip";
      chip.append(svgIcon(ICONS.close), `${assignment.subcontractorName}: ${assignment.sourceObjectNumber} → ${assignment.targetObjectNumber}`);
      chip.title = "Atšaukti šį objekto pakeitimą";
      chip.addEventListener("click", () => removeObjectAssignment(assignment));
      chips.append(chip);
    }
    editBar.append(chips);
  }

  const done = document.createElement("button");
  done.type = "button";
  done.className = "btn edit-bar-done";
  done.textContent = "Baigti";
  done.addEventListener("click", () => setEditMode(false));
  editBar.append(done);
  editBar.hidden = false;
}

function renderContractTable(contractRows, monthKeys) {
  renderTableHead(monthKeys);
  const departmentRows = buildDepartmentSummaryRows(monthKeys);
  const bodyRows = [];
  renderedContractRows = [];

  const tableWrap = contractTableBody.closest("table")?.parentElement;
  const panel = tableWrap?.closest(".panel");
  if (panel && editBar.parentElement !== panel.parentElement) {
    panel.before(editBar);
  }
  if (panel && linkBanner.parentElement !== panel.parentElement) {
    panel.before(linkBanner);
  }
  updateEditBar();

  for (const summary of sortedContractRows(contractRows)) {
    const status = statusClass(summary.status);
    const tr = document.createElement("tr");
    if (summary.isImportedOnly) tr.classList.add("is-imported-only");
    if (summary.status === "Viršyta riba") tr.classList.add("is-over");
    if (selectedSummaryKey && summaryKey(summary) === selectedSummaryKey) tr.classList.add("is-selected");
    renderedContractRows.push({ tr, summary });

    if (isAllObjectsView) {
      appendObjectCell(tr, summary);
    }

    appendTextCell(tr, summary.departmentCode || "-", summary.departmentCode ? "" : "cell-quiet");
    const nameCell = appendTextCell(tr, summary.name, "subcontractor-name");
    nameCell.title = summary.name;
    appendMoneyCell(tr, summary.contracted).classList.add("col-sep");
    appendMoneyCell(tr, summary.invoiced);
    appendMoneyCell(tr, summary.remaining);
    tr.append(createUsageCell(summary.usagePercent, status));
    const statusCell = createStatusCell(status);
    statusCell.classList.add("col-sep");
    tr.append(statusCell);
    appendTextCell(tr, summary.lastInvoice ? monthLabel(summary.lastInvoice) : "—", summary.lastInvoice ? "col-last" : "col-last cell-quiet");
    tr.append(createMatchCell(summary));

    if (showMonths) {
      monthKeys.forEach((key, i) => {
        const cell = appendMoneyCell(tr, summary.monthly.get(key) ?? 0);
        if (i === 0) cell.classList.add("col-sep");
      });
      appendMoneyCell(tr, summary.invoiced, { emphasize: true, danger: status.className === "over" });
    }

    tr.tabIndex = 0;
    tr.setAttribute("role", "button");
    tr.setAttribute("aria-label", `${summary.name} — atidaryti subrangovo informaciją`);
    tr.addEventListener("click", () => {
      if (linkSource) {
        if (isEligibleLinkTarget(summary)) completeLink(summary);
        return;
      }
      openDrawer(summary);
    });
    tr.addEventListener("keydown", (event) => {
      if (event.key !== "Enter" && event.key !== " ") return;
      if (event.target !== tr) return;
      event.preventDefault();
      if (linkSource) {
        if (isEligibleLinkTarget(summary)) completeLink(summary);
        return;
      }
      openDrawer(summary);
    });

    if (canStartLink(summary)) {
      tr.draggable = true;
      tr.addEventListener("dragstart", (event) => {
        event.dataTransfer.effectAllowed = "link";
        event.dataTransfer.setData("text/plain", summary.name);
        startLinking(summary);
      });
      tr.addEventListener("dragend", () => cancelLinking());
    }

    if (!summary.isImportedOnly && summary.rowKey) {
      tr.addEventListener("dragover", (event) => {
        if (isEligibleLinkTarget(summary)) {
          event.preventDefault();
          event.dataTransfer.dropEffect = "link";
        }
      });
      tr.addEventListener("drop", (event) => {
        if (isEligibleLinkTarget(summary)) {
          event.preventDefault();
          completeLink(summary);
        }
      });
    }

    bodyRows.push(tr);
  }

  if (firstContractRender) {
    bodyRows.slice(0, 12).forEach((tr, i) => {
      tr.classList.add("row-enter");
      tr.style.animationDelay = `${i * 15}ms`;
    });
    firstContractRender = false;
  }
  contractTableBody.replaceChildren(...departmentRows, ...bodyRows);
  updateLinkingVisuals();

  /* search produced nothing: keep the table frame, explain inside it */
  if (bodyRows.length === 0) {
    const emptyRow = document.createElement("tr");
    const cell = document.createElement("td");
    cell.className = "table-empty-cell";
    cell.colSpan = tableColumns(monthKeys).length;
    cell.textContent = searchTerm
      ? `Pagal „${subSearch?.value ?? ""}“ subrangovų nerasta. Išvalykite paiešką, kad pamatytumėte visus (${baseContractRows.length}).`
      : "Šioje srityje subrangovų nėra.";
    emptyRow.append(cell);
    contractTableBody.replaceChildren(...departmentRows, emptyRow);
    contractTableFoot.replaceChildren();
    setText(tableCaption, `${periodRange(monthKeys)}`);
    setText(tableShowing, `Rodoma 0 iš ${baseContractRows.length} subrangovų`);
    return;
  }

  const totals = contractRows.reduce((acc, row) => {
    acc.contracted += row.contracted;
    acc.invoiced += row.invoiced;
    for (const key of monthKeys) {
      acc.monthly.set(key, numberValue(acc.monthly.get(key)) + numberValue(row.monthly.get(key)));
    }
    return acc;
  }, {
    contracted: 0,
    invoiced: 0,
    monthly: new Map(monthKeys.map((k) => [k, 0]))
  });

  const totalRemaining = totals.contracted - totals.invoiced;
  const totalUsage = totals.contracted > 0 ? totals.invoiced / totals.contracted * 100 : 0;
  const totalStatus = statusClass(deriveStatus(totals.contracted, totals.invoiced));

  const footRow = document.createElement("tr");
  if (isAllObjectsView) appendTextCell(footRow, "");
  appendTextCell(footRow, "");
  appendTextCell(footRow, "IŠ VISO · subrangovai", "subcontractor-name is-strong");
  appendMoneyCell(footRow, totals.contracted, { emphasize: true }).classList.add("col-sep");
  appendMoneyCell(footRow, totals.invoiced, { emphasize: true });
  appendMoneyCell(footRow, totalRemaining, { emphasize: true });
  footRow.append(createUsageCell(totalUsage, totalStatus));
  appendTextCell(footRow, "");
  appendTextCell(footRow, "");
  appendTextCell(footRow, "");
  if (showMonths) {
    for (const key of monthKeys) {
      appendMoneyCell(footRow, totals.monthly.get(key) ?? 0, { emphasize: true });
    }
    appendMoneyCell(footRow, totals.invoiced, { emphasize: true, danger: totalStatus.className === "over" });
  }
  contractTableFoot.replaceChildren(footRow);

  setText(tableCaption, `${periodRange(monthKeys)}`);
  setText(tableShowing, contractRows.length === baseContractRows.length
    ? `Rodomi visi ${contractRows.length} subrangov${contractRows.length === 1 ? "as" : "ai"}`
    : `Rodoma ${contractRows.length} iš ${baseContractRows.length} subrangovų`);
}

function renderCurrentContractTable() {
  renderContractTable(filteredContractRows(), currentMonthKeys);
}

/* ─── CSV export ──────────────────────────────────────────────────────── */

function csvField(value) {
  const text = String(value ?? "");
  return /[",;\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

function exportCsv() {
  const rows = sortedContractRows(filteredContractRows());
  const header = [
    "Objektas", "Padalinys", "Subrangovas", "Sutarta (EUR)", "Sąskaitose (EUR)",
    "Likutis (EUR)", "Panaudota %", "Būsena", "Paskutinė sąskaita",
    ...currentMonthKeys.map(monthLabel)
  ];
  const lines = [header.map(csvField).join(",")];
  for (const row of rows) {
    lines.push([
      row.projectObjectNumber, row.departmentCode, row.name,
      row.contracted.toFixed(2), row.invoiced.toFixed(2), row.remaining.toFixed(2),
      row.usagePercent.toFixed(0), row.status, row.lastInvoice ? monthLabel(row.lastInvoice) : "",
      ...currentMonthKeys.map((key) => numberValue(row.monthly.get(key)).toFixed(2))
    ].map(csvField).join(","));
  }
  const blob = new Blob([`﻿${lines.join("\n")}`], { type: "text/csv;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = `${selectedObjectNumber || parentProjectCodeForApi() || "project"}-subrangovai.csv`;
  a.click();
  URL.revokeObjectURL(url);
}

/* ─── Exceptions ──────────────────────────────────────────────────────── */

function renderExceptions() {
  const issues = baseContractRows.filter((row) => row.warning || row.status !== "Pagal planą");
  exceptionsBadge.textContent = String(issues.length);
  exceptionsBadge.classList.toggle("is-zero", issues.length === 0);
  setText(exceptionsSummary, issues.length === 0
    ? "Nieko nereikia peržiūrėti"
    : `${issues.length} įspėjim${issues.length === 1 ? "as" : "ai"} reikalauja dėmesio`);

  if (issues.length === 0) {
    const ok = document.createElement("div");
    ok.className = "drawer-empty";
    ok.textContent = "Visi šios srities subrangovai yra pagal planą.";
    exceptionsContent.replaceChildren(ok);
    return;
  }

  const items = issues.map((summary) => {
    const status = statusClass(summary.status);
    const item = document.createElement("button");
    item.type = "button";
    item.className = "exception-item";

    const pill = document.createElement("span");
    pill.className = `pill ${status.pill}`;
    pill.textContent = status.label;

    const name = document.createElement("span");
    name.className = "exception-item-name";
    name.textContent = summary.name;

    const object = document.createElement("span");
    object.className = "exception-item-object";
    object.textContent = summary.projectObjectNumber || "-";

    const detail = document.createElement("span");
    detail.className = "exception-item-detail";
    detail.textContent = summary.warning
      || (summary.status === "Trūksta sutarties"
        ? `${money(summary.invoiced)} € sąskaitose be susietos sutarties`
        : `panaudota ${percentFormatter.format(summary.usagePercent)}% · likutis ${money(summary.remaining)} €`);

    item.append(pill, name, object, detail);
    item.addEventListener("click", () => openDrawer(summary, "warnings"));
    return item;
  });

  exceptionsContent.replaceChildren(...items);
}

/* ─── Detail drawer ───────────────────────────────────────────────────── */

/* Element that opened the drawer, so keyboard focus can return to it on close
   (matters most in the overlay layout, where the drawer is a true modal sheet). */
let drawerTrigger = null;
const drawerIsOverlay = () => window.matchMedia("(max-width: 1399px)").matches;

function closeDrawer() {
  if (!document.body.classList.contains("drawer-open")) return;
  document.body.classList.remove("drawer-open");
  drawer.setAttribute("aria-hidden", "true");
  selectedSummaryKey = "";
  for (const { tr } of renderedContractRows) tr.classList.remove("is-selected");
  const trigger = drawerTrigger;
  drawerTrigger = null;
  if (trigger && typeof trigger.focus === "function" && document.contains(trigger)) {
    trigger.focus();
  }
}

function openDrawer(summary, tab) {
  if (!document.body.classList.contains("drawer-open")) drawerTrigger = document.activeElement;
  selectedSummaryKey = summaryKey(summary);
  drawerTab = tab || drawerTab || "monthly";
  document.body.classList.add("drawer-open");
  drawer.setAttribute("aria-hidden", "false");
  for (const { tr, summary: rowSummary } of renderedContractRows) {
    tr.classList.toggle("is-selected", summaryKey(rowSummary) === selectedSummaryKey);
  }
  renderDrawer(summary);
  /* In overlay mode the panel covers the page, so pull focus in for keyboard
     and screen-reader users; in the split layout it stays a side column and
     stealing focus would jump the viewport, so leave focus on the row. */
  if (drawerIsOverlay()) drawer.querySelector(".drawer-close")?.focus();
}

function drawerStat(label, valueNode, tone) {
  const stat = document.createElement("div");
  stat.className = "drawer-stat";
  const labelEl = document.createElement("span");
  labelEl.className = "drawer-stat-label";
  labelEl.textContent = label;
  const valueEl = document.createElement("span");
  valueEl.className = `drawer-stat-value${tone ? ` is-${tone}` : ""}`;
  if (valueNode instanceof Node) valueEl.append(valueNode);
  else valueEl.textContent = valueNode;
  stat.append(labelEl, valueEl);
  return stat;
}

function renderDrawer(summary) {
  const status = statusClass(summary.status);

  /* head */
  const head = document.createElement("div");
  const drawerStatusTone = status.usage === "over" ? "danger" : status.usage === "warn" ? "warn" : "ok";
  head.className = `drawer-head drawer-status-${drawerStatusTone}`;
  const title = document.createElement("div");
  title.className = "drawer-title";
  const h2 = document.createElement("h2");
  h2.textContent = summary.name;
  title.append(h2);
  if (summary.links.length > 0) {
    const chip = document.createElement("span");
    chip.className = "alias-chip";
    chip.textContent = `${summary.links.length} alternatyv${summary.links.length === 1 ? "us pavadinimas" : "ūs pavadinimai"}`;
    title.append(chip);
  }
  /* identity chips: object, department, status; the colored top border and
     this pill speak the same status vocabulary as the table */
  const chips = document.createElement("div");
  chips.className = "drawer-chips";
  if (summary.projectObjectNumber) {
    const objectChip = document.createElement("span");
    objectChip.className = "drawer-chip";
    const objectLabel = document.createElement("span");
    objectLabel.textContent = "Objektas";
    objectChip.append(objectLabel, ` ${summary.projectObjectNumber}`);
    chips.append(objectChip);
  }
  if (summary.departmentCode) {
    const deptChip = document.createElement("span");
    deptChip.className = "drawer-chip";
    const deptLabel = document.createElement("span");
    deptLabel.textContent = "Pad.";
    deptChip.append(deptLabel, ` ${summary.departmentCode}`);
    chips.append(deptChip);
  }
  const statusPill = document.createElement("span");
  statusPill.className = `pill ${status.pill}`;
  statusPill.textContent = status.label;
  chips.append(statusPill);
  title.append(chips);

  const close = document.createElement("button");
  close.type = "button";
  close.className = "drawer-close";
  close.setAttribute("aria-label", "Uždaryti informacijos skydelį");
  close.append(svgIcon(ICONS.close));
  close.addEventListener("click", () => closeDrawer());
  head.append(title, close);

  /* stats */
  const stats = document.createElement("div");
  stats.className = "drawer-stats";
  const usageWrap = document.createElement("span");
  usageWrap.className = "usage";
  const usageBar = document.createElement("span");
  usageBar.className = "usage-bar";
  const usageFill = document.createElement("span");
  usageFill.className = `usage-fill ${status.usage}`;
  usageFill.style.width = `${Math.min(Math.max(summary.usagePercent, 0), 100)}%`;
  usageBar.append(usageFill);
  usageWrap.append(
    Object.assign(document.createElement("span"), {
      textContent: summary.isImportedOnly ? "-" : `${percentFormatter.format(summary.usagePercent)}%`
    }),
    usageBar
  );

  stats.append(
    drawerStat("Sutarta", `${money(summary.contracted)} €`),
    drawerStat("Sąskaitose", `${money(summary.invoiced)} €`),
    drawerStat("Likutis", `${money(summary.remaining)} €`, summary.remaining < 0 ? "danger" : undefined),
    drawerStat("Panaudota", usageWrap),
    drawerStat("Būsena", status.label, status.usage === "ok" ? "ok" : status.usage === "warn" ? "warn" : "danger"),
    drawerStat("Paskutinė sąskaita", summary.lastInvoice ? monthLabel(summary.lastInvoice) : "—")
  );

  /* tabs */
  const tabs = document.createElement("div");
  tabs.className = "drawer-tabs";
  tabs.setAttribute("role", "tablist");
  const tabDefs = [
    { id: "monthly", label: "Mėnesiai" },
    { id: "sources", label: "Pirminės eilutės" },
    { id: "contracts", label: "Sutartys" },
    { id: "matching", label: "Susiejimas" },
    { id: "warnings", label: "Įspėjimai" }
  ];
  for (const def of tabDefs) {
    const tab = document.createElement("button");
    tab.type = "button";
    tab.className = `drawer-tab${drawerTab === def.id ? " is-active" : ""}`;
    tab.setAttribute("role", "tab");
    tab.setAttribute("aria-selected", String(drawerTab === def.id));
    tab.textContent = def.label;
    tab.addEventListener("click", () => {
      drawerTab = def.id;
      renderDrawer(summary);
    });
    tabs.append(tab);
  }

  /* body */
  const body = document.createElement("div");
  body.className = "drawer-body";
  if (drawerTab === "monthly") body.append(...drawerMonthly(summary));
  else if (drawerTab === "sources") body.append(drawerSources(summary));
  else if (drawerTab === "contracts") body.append(...drawerContracts(summary));
  else if (drawerTab === "matching") body.append(...drawerMatching(summary));
  else body.append(...drawerWarnings(summary));

  const scroll = document.createElement("div");
  scroll.className = "drawer-scroll";
  scroll.append(stats, tabs, body);

  drawer.replaceChildren(head, scroll);

  if (drawerTab === "monthly") {
    const foot = document.createElement("div");
    foot.className = "drawer-foot";
    const link = document.createElement("button");
    link.type = "button";
    link.className = "btn-link";
    link.append("Rodyti mėnesių stulpelius lentelėje", svgIcon(ICONS.external));
    link.addEventListener("click", () => {
      setShowMonths(true);
      contractTableHead.closest(".table-wrap")?.scrollIntoView({ behavior: "smooth", block: "start" });
    });
    foot.append(link);
    drawer.append(foot);
  }
}

function drawerMonthly(summary) {
  /* only years that actually carry invoices; all-zero years are noise */
  const years = [...new Set(currentMonthKeys.map((key) => key.slice(0, 4)))].sort()
    .filter((year) => {
      for (let month = 1; month <= 12; month += 1) {
        if (numberValue(summary.monthly.get(`${year}-${String(month).padStart(2, "0")}`)) !== 0) return true;
      }
      return false;
    });
  if (years.length === 0) {
    const emptyEl = document.createElement("div");
    emptyEl.className = "drawer-empty";
    emptyEl.textContent = "Kol kas neužregistruota mėnesinių sąskaitų.";
    return [emptyEl];
  }

  const nodes = [];
  const note = document.createElement("p");
  note.className = "drawer-note";
  note.textContent = `Mėnesinės sąskaitų sumos (EUR, be PVM) · ${periodRange(currentMonthKeys)}`;
  nodes.push(note);

  let grandTotal = 0;
  for (const year of years) {
    let yearTotal = 0;
    const grid = document.createElement("div");
    grid.className = "month-grid";
    let yearMax = 0;
    for (let month = 1; month <= 12; month += 1) {
      const key = `${year}-${String(month).padStart(2, "0")}`;
      yearMax = Math.max(yearMax, Math.abs(numberValue(summary.monthly.get(key))));
    }
    for (let month = 1; month <= 12; month += 1) {
      const key = `${year}-${String(month).padStart(2, "0")}`;
      const amount = numberValue(summary.monthly.get(key));
      yearTotal += amount;
      const cell = document.createElement("div");
      cell.className = `month-cell${amount === 0 ? " is-zero" : ""}`;
      const label = document.createElement("span");
      label.className = "month-cell-label";
      label.textContent = MONTH_SHORT[month - 1];
      const value = document.createElement("span");
      value.className = "month-cell-value";
      value.textContent = amount === 0 ? "0" : moneyCompact(amount);
      value.title = `${money(amount)} €`;
      const bar = document.createElement("span");
      bar.className = "month-cell-bar";
      const pct = amount !== 0 && yearMax > 0 ? Math.abs(amount) / yearMax * 100 : 0;
      bar.style.width = `${pct}%`;
      cell.append(label, value, bar);
      grid.append(cell);
    }
    grandTotal += yearTotal;

    const block = document.createElement("div");
    block.className = "month-year";
    const labelRow = document.createElement("div");
    labelRow.className = "month-year-label";
    labelRow.append(year);
    const yearSum = document.createElement("span");
    yearSum.textContent = `${money(yearTotal)} €`;
    const activeMonths = Array.from({ length: 12 }, (_, i) =>
      numberValue(summary.monthly.get(`${year}-${String(i + 1).padStart(2, "0")}`))).filter((v) => v !== 0).length;
    const monthsNote = document.createElement("em");
    monthsNote.textContent = `${activeMonths} mėn.`;
    yearSum.append(monthsNote);
    labelRow.append(yearSum);
    block.append(labelRow, grid);
    nodes.push(block);
  }

  const totalRow = document.createElement("div");
  totalRow.className = "month-total-row";
  const totalLabel = document.createElement("span");
  totalLabel.textContent = "Iš viso sąskaitose";
  const totalValue = document.createElement("span");
  totalValue.textContent = `${money(grandTotal)} €`;
  totalRow.append(totalLabel, totalValue);
  nodes.push(totalRow);

  return nodes;
}

function drawerSources(summary) {
  const aliasNames = new Set(summary.links.map((link) => String(link.sourceName).toLowerCase()));
  const rows = allRows.filter((row) => {
    const name = cleanSubcontractorDisplayName(row.subcontractorName).toLowerCase();
    const matchesName = name === summary.name.toLowerCase() || aliasNames.has(name);
    const matchesObject = !summary.projectObjectNumber
      || String(row.objectNumber ?? "").toLowerCase() === summary.projectObjectNumber.toLowerCase();
    return matchesName && matchesObject;
  });

  if (rows.length === 0) {
    const emptyEl = document.createElement("div");
    emptyEl.className = "drawer-empty";
    emptyEl.textContent = "Šiam subrangovui nėra importuotų pirminių eilučių.";
    return emptyEl;
  }

  const table = document.createElement("table");
  table.className = "drawer-table";
  const thead = document.createElement("thead");
  const headRow = document.createElement("tr");
  [["Šaltinis", ""], ["Mėnuo", ""], ["Įvesta kaip", ""], ["Suma (EUR)", "col-money"]].forEach(([t, c]) => {
    const th = document.createElement("th");
    th.textContent = t;
    if (c) th.className = c;
    headRow.append(th);
  });
  thead.append(headRow);

  const tbody = document.createElement("tbody");
  for (const row of rows) {
    const tr = document.createElement("tr");
    appendTextCell(tr, `${row.sourceSheet || "Lapas"} ${row.sourceRow ? `#${row.sourceRow}` : ""}`.trim(), "source-ref");
    appendTextCell(tr, monthLabel(`${row.year}-${String(row.month).padStart(2, "0")}`));
    appendTextCell(tr, cleanSubcontractorDisplayName(row.subcontractorName));
    appendMoneyCell(tr, row.amountWithoutVat);
    tbody.append(tr);
  }
  table.append(thead, tbody);
  return table;
}

function drawerContracts(summary) {
  if (summary.isImportedOnly) {
    const note = document.createElement("p");
    note.className = "drawer-note";
    note.textContent = "Šiam subrangovui dar nėra susietos sutarties. Jo sąskaitos importuotos tik iš mėnesinių duomenų.";
    const nodes = [note];
    if (canStartLink(summary)) {
      const connect = document.createElement("button");
      connect.type = "button";
      connect.className = "btn is-active";
      connect.append(svgIcon(ICONS.link), "Susieti su sutarties eilute");
      connect.addEventListener("click", () => {
        closeDrawer();
        startLinking(summary);
      });
      nodes.push(connect);
    }
    return nodes;
  }

  const dl = document.createElement("dl");
  dl.className = "drawer-dl";
  const entries = [
    ["Objektas", summary.projectObjectNumber || "-"],
    ["Padalinys", summary.departmentCode || "-"],
    ["Objekto pavadinimas", summary.objectName || "-"],
    ["Sutarta", `${money(summary.contracted)} €`],
    ["Sąskaitose iki šiol", `${money(summary.invoiced)} €`],
    ["Likutis", `${money(summary.remaining)} €`],
    ["Panaudota", `${percentFormatter.format(summary.usagePercent)}%`],
    ["Būsena", summary.status]
  ];
  for (const [label, value] of entries) {
    const dt = document.createElement("dt");
    dt.textContent = label;
    const dd = document.createElement("dd");
    dd.textContent = value;
    if (label === "Likutis" && summary.remaining < 0) dd.classList.add("is-negative");
    dl.append(dt, dd);
  }
  return [dl];
}

function drawerMatching(summary) {
  const nodes = [];

  if (summary.links.length > 0) {
    const note = document.createElement("p");
    note.className = "drawer-note";
    note.textContent = "Šiais pavadinimais įrašytos sąskaitos įskaičiuojamos į šį subrangovą:";
    nodes.push(note);
    for (const link of summary.links) {
      const item = document.createElement("div");
      item.className = "matching-item";
      const chip = document.createElement("span");
      chip.className = "alias-chip";
      chip.textContent = "susieta";
      const name = document.createElement("b");
      name.textContent = link.sourceName;
      const disconnect = document.createElement("button");
      disconnect.type = "button";
      disconnect.className = "btn";
      disconnect.textContent = "Atsieti";
      disconnect.addEventListener("click", () => removeLink(link));
      item.append(chip, name, disconnect);
      nodes.push(item);
    }
    return nodes;
  }

  if (canStartLink(summary)) {
    const note = document.createElement("p");
    note.className = "drawer-note";
    note.textContent = `„${summary.name}“ neatitiko jokios sutarties — dažniausiai dėl rašybos klaidos pavadinime. Susiekite jį su tinkamu subrangovu objekte ${summary.projectObjectNumber}, kad jo sąskaitos būtų įskaičiuotos, o būsimi importai susietųsi automatiškai.`;
    const connect = document.createElement("button");
    connect.type = "button";
    connect.className = "btn is-active";
    connect.append(svgIcon(ICONS.link), "Susieti su tinkamu subrangovu");
    connect.addEventListener("click", () => {
      closeDrawer();
      startLinking(summary);
    });
    return [note, connect];
  }

  const emptyEl = document.createElement("div");
  emptyEl.className = "drawer-empty";
  emptyEl.textContent = "Susieta tiesiogiai pagal pavadinimą. Rankinių alternatyvų nėra.";
  return [emptyEl];
}

function drawerWarnings(summary) {
  const status = statusClass(summary.status);
  const issues = [];
  if (summary.warning) issues.push(summary.warning);
  if (summary.status === "Viršyta riba") {
    issues.push(`Sąskaitose ${money(summary.invoiced)} € viršija sutartą ${money(summary.contracted)} € suma ${money(Math.abs(summary.remaining))} €.`);
  } else if (summary.status === "Pasiekta riba") {
    issues.push("Sutarta suma visiškai panaudota.");
  } else if (summary.status === "Artėja prie ribos") {
    issues.push(`Panaudota ${percentFormatter.format(summary.usagePercent)}% sutartos sumos. Liko ${money(summary.remaining)} €.`);
  } else if (summary.status === "Trūksta sutarties") {
    issues.push("Sąskaitos importuotos, bet šiam subrangovui nesusieta jokia sutartis.");
  }

  if (issues.length === 0) {
    const emptyEl = document.createElement("div");
    emptyEl.className = "drawer-empty";
    emptyEl.textContent = "Įspėjimų nėra. Šis subrangovas yra pagal planą.";
    return [emptyEl];
  }

  const nodes = [];
  const pillWrap = document.createElement("p");
  pillWrap.className = "drawer-note";
  const pill = document.createElement("span");
  pill.className = `pill ${status.pill}`;
  pill.textContent = status.label;
  pillWrap.append(pill);
  nodes.push(pillWrap);
  for (const issue of issues) {
    const p = document.createElement("p");
    p.className = "drawer-note";
    p.textContent = issue;
    nodes.push(p);
  }
  return nodes;
}

/* ─── Department client rows (pinned atop the subcontractor table) ─────── */
/* The client side of each department, shown as rows in the same table and the
   same columns as subcontractors: Contracted = the department's contracted
   project value (P vertė), Invoiced + the monthly cells = how much the client
   was invoiced (SMD), Remaining/Usage/Status derived the same way. They sit
   above the subcontractor rows and are visually marked as client-value rows. */
function buildDepartmentSummaryRows(monthKeys) {
  const objectValues = (projectDetail?.projectObjectValues ?? [])
    .filter((value) => isValidClientObjectNumber(value.objectNumber));
  const clientRows = (projectDetail?.smdCustomerRows ?? [])
    .filter((row) => isValidClientObjectNumber(row.objectNumber));
  if (objectValues.length === 0 && clientRows.length === 0) return [];

  /* SMD rows carry no department; resolve it via the project-value rows. */
  const deptByObject = new Map();
  for (const value of objectValues) {
    const key = String(value.objectNumber ?? "").toLowerCase();
    if (value.departmentCode && !deptByObject.has(key)) deptByObject.set(key, value.departmentCode);
  }

  const deptMap = new Map();
  const ensure = (dept) => {
    if (!deptMap.has(dept)) {
      deptMap.set(dept, {
        dept,
        contracted: 0,
        invoiced: 0,
        monthly: new Map(monthKeys.map((m) => [m, 0])),
        objects: new Set(),
        objectPrintCode: "",
        clients: new Set(),
        lastInvoice: null
      });
    }
    return deptMap.get(dept);
  };

  for (const value of objectValues) {
    const entry = ensure(value.departmentCode || "—");
    entry.contracted += numberValue(value.projectValueAmount);
    if (value.objectNumber) entry.objects.add(value.objectNumber);
    entry.objectPrintCode ||= value.objectPrintCode || "";
  }
  for (const row of clientRows) {
    const dept = deptByObject.get(String(row.objectNumber ?? "").toLowerCase()) || "—";
    const entry = ensure(dept);
    const monthKey = `${row.year}-${String(row.month).padStart(2, "0")}`;
    const amount = numberValue(row.clientMonthlyAmount);
    entry.invoiced += amount;
    if (entry.monthly.has(monthKey)) entry.monthly.set(monthKey, numberValue(entry.monthly.get(monthKey)) + amount);
    const clientName = cleanClientDisplayName(row.customerName);
    if (clientName) entry.clients.add(clientName);
    if (row.objectNumber) entry.objects.add(row.objectNumber);
    if (amount !== 0 && (!entry.lastInvoice || monthKey > entry.lastInvoice)) entry.lastInvoice = monthKey;
  }

  const depts = [...deptMap.values()].sort((a, b) =>
    String(a.dept).localeCompare(String(b.dept), undefined, { numeric: true, sensitivity: "base" }));

  return depts.map((entry) => {
    const usage = entry.contracted > 0 ? entry.invoiced / entry.contracted * 100 : 0;
    const status = statusClass(deriveStatus(entry.contracted, entry.invoiced));
    const remaining = entry.contracted - entry.invoiced;

    const tr = document.createElement("tr");
    tr.className = "is-department-row";

    if (isAllObjectsView) {
      const objects = [...entry.objects];
      appendTextCell(tr, objects.length === 1 ? objects[0] : `${objects.length} objekt${objects.length === 1 ? "as" : "ai"}`, "col-code");
    }
    appendTextCell(tr, entry.dept || "—", entry.dept ? "col-code" : "cell-quiet");
    const nameCell = appendTextCell(tr, [...entry.clients][0] || "Užsakovo vertė", "subcontractor-name");
    const badge = document.createElement("span");
    badge.className = "client-value-badge";
    badge.textContent = "Užsakovas";
    nameCell.prepend(badge, " ");
    nameCell.title = `Padalinys ${entry.dept} — sutartinė projekto vertė palyginti su užsakovui išrašytomis sąskaitomis`;
    appendMoneyCell(tr, entry.contracted).classList.add("col-sep");
    appendMoneyCell(tr, entry.invoiced);
    appendMoneyCell(tr, remaining);
    tr.append(createUsageCell(usage, status));
    const statusCell = createStatusCell(status);
    statusCell.classList.add("col-sep");
    tr.append(statusCell);
    appendTextCell(tr, entry.lastInvoice ? monthLabel(entry.lastInvoice) : "—", entry.lastInvoice ? "col-last" : "col-last cell-quiet");
    appendTextCell(tr, "—", "cell-quiet");

    if (showMonths) {
      monthKeys.forEach((key, i) => {
        const cell = appendMoneyCell(tr, entry.monthly.get(key) ?? 0);
        if (i === 0) cell.classList.add("col-sep");
      });
      appendMoneyCell(tr, entry.invoiced, { emphasize: true });
    }
    return tr;
  });
}

/* ─── Object / client value tracking (P vertė vs SMD) ─────────────────── */

function renderObjectClientValues(monthKeys) {
  const objectValues = (projectDetail.projectObjectValues ?? [])
    .filter((value) => isValidClientObjectNumber(value.objectNumber));
  const clientRows = (projectDetail.smdCustomerRows ?? [])
    .filter((row) => isValidClientObjectNumber(row.objectNumber));
  if (objectValues.length === 0 && clientRows.length === 0) {
    hide(objectValueSection);
    return;
  }

  const clientMonthKeys = [...new Set(clientRows.map((row) => `${row.year}-${String(row.month).padStart(2, "0")}`))].sort();
  const displayMonthKeys = clientMonthKeys.length > 0 ? clientMonthKeys : monthKeys;
  const objectValueByObject = new Map();
  for (const value of objectValues) {
    const key = String(value.objectNumber ?? "").toLowerCase();
    const existing = objectValueByObject.get(key);
    if (existing) {
      existing.projectValueAmount += numberValue(value.projectValueAmount);
      existing.objectPrintCode ||= value.objectPrintCode || "";
      existing.departmentCode ||= value.departmentCode || "";
      existing.objectIndex ||= value.objectIndex || "";
    } else {
      objectValueByObject.set(key, {
        objectNumber: value.objectNumber || "",
        objectPrintCode: value.objectPrintCode || "",
        departmentCode: value.departmentCode || "",
        objectIndex: value.objectIndex || "",
        projectValueAmount: numberValue(value.projectValueAmount)
      });
    }
  }

  const rowsByKey = new Map();
  for (const row of clientRows) {
    const objectNumber = row.objectNumber || "";
    const normalizedClientName = normalizeClientName(row.customerName) || "(NO CUSTOMER)";
    const clientName = cleanClientDisplayName(row.customerName) || "(Be užsakovo)";
    const key = `${objectNumber.toLowerCase()}|${normalizedClientName}`;
    if (!rowsByKey.has(key)) {
      const objectValue = objectValueByObject.get(objectNumber.toLowerCase()) ?? {
        objectNumber,
        objectPrintCode: "",
        departmentCode: "",
        objectIndex: "",
        projectValueAmount: 0
      };
      rowsByKey.set(key, {
        ...objectValue,
        clientName,
        normalizedClientName,
        monthly: new Map(displayMonthKeys.map((month) => [month, 0])),
        totalClientValue: 0,
        missingProjectValue: !objectValueByObject.has(objectNumber.toLowerCase())
      });
    }
    const summary = rowsByKey.get(key);
    const candidateDisplayName = cleanClientDisplayName(row.customerName);
    if (candidateDisplayName && candidateDisplayName.length < summary.clientName.length) {
      summary.clientName = candidateDisplayName;
    }
    const month = `${row.year}-${String(row.month).padStart(2, "0")}`;
    summary.monthly.set(month, numberValue(summary.monthly.get(month)) + numberValue(row.clientMonthlyAmount));
    summary.totalClientValue += numberValue(row.clientMonthlyAmount);
  }

  for (const value of objectValues) {
    const hasClientRow = clientRows.some((row) => String(row.objectNumber ?? "").toLowerCase() === String(value.objectNumber ?? "").toLowerCase());
    if (!hasClientRow) {
      const key = `${String(value.objectNumber ?? "").toLowerCase()}|`;
      rowsByKey.set(key, {
        objectNumber: value.objectNumber || "",
        objectPrintCode: value.objectPrintCode || "",
        departmentCode: value.departmentCode || "",
        objectIndex: value.objectIndex || "",
        projectValueAmount: numberValue(value.projectValueAmount),
        clientName: "-",
        monthly: new Map(displayMonthKeys.map((month) => [month, 0])),
        totalClientValue: 0,
        missingProjectValue: false
      });
    }
  }

  const rows = [...rowsByKey.values()].sort((a, b) =>
    String(a.objectNumber).localeCompare(String(b.objectNumber), undefined, { numeric: true, sensitivity: "base" })
    || String(a.clientName).localeCompare(String(b.clientName), undefined, { numeric: true, sensitivity: "base" })
  );
  const totalProjectValue = [...objectValueByObject.values()]
    .reduce((sum, row) => sum + numberValue(row.projectValueAmount), 0);
  const totalClientValue = rows.reduce((sum, row) => sum + numberValue(row.totalClientValue), 0);
  setText(objectValueSummary, `${rows.length} eilut${rows.length === 1 ? "ė" : "ės"} · ${money(totalClientValue)} užsakovo / ${money(totalProjectValue)} projekto vertė`);

  const table = document.createElement("table");
  table.className = "imported-table source-table";
  const thead = document.createElement("thead");
  const headRow = document.createElement("tr");
  [
    { t: "Objektas", c: "" },
    { t: "Padalinys", c: "" },
    { t: "Užsakovas", c: "" },
    { t: "Sutartinė projekto vertė (EUR)", c: "col-money" },
    ...displayMonthKeys.map((key) => ({ t: monthLabel(key), c: "col-money" })),
    { t: "Iš viso/metų užsakovo vertė (EUR)", c: "col-money" },
    { t: "Likutis (EUR)", c: "col-money" }
  ].forEach(({ t, c }) => {
    const th = document.createElement("th");
    th.textContent = t;
    if (c) th.className = c;
    headRow.append(th);
  });
  thead.append(headRow);

  const tbody = document.createElement("tbody");
  for (const row of rows) {
    const tr = document.createElement("tr");
    tr.className = "client-value-row";
    appendTextCell(tr, row.objectNumber || "-", "col-code");
    appendTextCell(tr, row.departmentCode || "-");
    const clientCell = appendTextCell(tr, row.clientName || "-", "subcontractor-name");
    const badge = document.createElement("span");
    badge.className = "client-value-badge";
    badge.textContent = row.missingProjectValue ? "Trūksta projekto vertės" : "Užsakovo vertė";
    clientCell.prepend(badge, " ");
    appendMoneyCell(tr, row.projectValueAmount, { danger: row.missingProjectValue });
    for (const key of displayMonthKeys) {
      appendMoneyCell(tr, row.monthly.get(key) ?? 0);
    }
    appendMoneyCell(tr, row.totalClientValue, { emphasize: true });
    appendMoneyCell(tr, row.projectValueAmount - row.totalClientValue, { emphasize: true });
    tbody.append(tr);
  }

  table.append(thead, tbody);
  const wrap = document.createElement("div");
  wrap.className = "table-wrap source-table-wrap";
  wrap.append(table);
  objectValueContent.replaceChildren(wrap);
  show(objectValueSection);
}

/* ─── Monthly source rows (audit) ─────────────────────────────────────── */

function rowSourceLabel(row) {
  const sheet = row.sourceSheet || "Lapas";
  const sourceRow = row.sourceRow ? `#${row.sourceRow}` : "";
  return `${sheet} ${sourceRow}`.trim();
}

function isSmdRow(row) {
  return String(row.sourceSheet ?? "").trim().toLowerCase() === "smd";
}

function renderImportedRows(monthKeys) {
  setText(importedRowsSummary, `${allRows.length} eilut${allRows.length === 1 ? "ė" : "ės"} · ${periodRange(monthKeys)}`);

  const sections = monthGroups.map((group) => {
    const rows = group.rows ?? [];
    const section = document.createElement("section");
    section.className = "month-group";

    const heading = document.createElement("div");
    heading.className = "month-group-head";
    const title = document.createElement("h3");
    title.textContent = monthLabel(monthKey(group));
    const total = rows.reduce((sum, row) => sum + numberValue(row.amountWithoutVat), 0);
    const sheets = [...new Set(rows.map(row => row.sourceSheet).filter(Boolean))];
    const meta = document.createElement("span");
    meta.textContent = `${rows.length} eilut${rows.length === 1 ? "ė" : "ės"} · ${sheets.length || 1} šaltin${sheets.length === 1 ? "is" : "iai"} · ${money(total)} EUR`;
    heading.append(title, meta);

    const tableWrap = document.createElement("div");
    tableWrap.className = "table-wrap source-table-wrap";

    const table = document.createElement("table");
    table.className = "imported-table source-table";
    const thead = document.createElement("thead");
    const headRow = document.createElement("tr");
    [
      { t: "Šaltinis", c: "" },
      { t: "Objektas", c: "" },
      { t: "Subrangovas / Užsakovas", c: "" },
      { t: "Objekto pavadinimas", c: "" },
      { t: "Suma be PVM (EUR)", c: "col-money" },
      { t: "Asmenys", c: "" }
    ].forEach(({ t, c }) => {
      const th = document.createElement("th");
      th.textContent = t;
      if (c) th.className = c;
      headRow.append(th);
    });
    thead.append(headRow);

    const tbody = document.createElement("tbody");
    for (const row of rows) {
      const tr = document.createElement("tr");
      appendTextCell(tr, rowSourceLabel(row), "source-ref");
      appendTextCell(tr, row.objectNumber || "-", "col-code");
      appendTextCell(tr, isSmdRow(row)
        ? (row.customerName || row.subcontractorName || "(Be užsakovo)")
        : cleanSubcontractorDisplayName(row.subcontractorName), "subcontractor-name");
      appendTextCell(tr, row.objectName || "-");
      appendMoneyCell(tr, row.amountWithoutVat);
      appendTextCell(tr, [row.responsible, row.engineer].filter(Boolean).join(" · ") || "-");
      tbody.append(tr);
    }

    table.append(thead, tbody);
    tableWrap.append(table);
    section.append(heading, tableWrap);
    return section;
  });

  importedRowsContent.replaceChildren(...sections);
}

/* ─── Load ────────────────────────────────────────────────────────────── */

async function loadProject() {
  const params = new URLSearchParams(window.location.search);
  const projectCode = params.get("projectCode");

  if (!projectCode) {
    hide(loading);
    setText(error, "Reikalingas projekto kodas.");
    show(error);
    return;
  }

  currentProjectCode = projectCode;
  linkSource = null;
  selectedObjectNumber = params.get("objectNumber") ?? "";
  const parsedProject = parseProjectObjectCode(projectCode);
  const parentProjectCode = parsedProject.parentProjectCode;
  setText(projectCodeHeading, selectedObjectNumber || parentProjectCode);
  setText(breadcrumbProject, selectedObjectNumber || parentProjectCode);
  document.title = `${selectedObjectNumber || parentProjectCode} | Mėnesinis pinigų srautas`;

  try {
    const detailUrl = new URL(`/api/projects/${encodeURIComponent(parentProjectCode)}/monthly-flow`, window.location.origin);
    if (selectedObjectNumber) detailUrl.searchParams.set("objectNumber", selectedObjectNumber);
    const response = await fetch(detailUrl, { cache: "no-store" });
    if (!response.ok) throw new Error(`Project request failed with status ${response.status}.`);

    const data = await response.json();
    projectDetail = data;
    isAllObjectsView = Boolean(data.isAllObjects);
    objectAssignments = data.objectAssignments ?? [];
    monthGroups = data.groups ?? [];
    allRows = monthGroups.flatMap((group) => group.rows ?? []);

    hide(loading);

    if (allRows.length === 0
      && (data.contractRows ?? []).length === 0
      && (data.projectObjectValues ?? []).length === 0
      && (data.smdCustomerRows ?? []).length === 0) {
      show(empty);
      return;
    }

    renderProjectMeta();
    const contractMonthKeys = (data.contractRows ?? [])
      .flatMap((row) => row.monthly ?? [])
      .map((m) => `${m.year}-${String(m.month).padStart(2, "0")}`);
    const monthKeys = [...new Set([...monthGroups.map(monthKey), ...contractMonthKeys])].sort();
    currentMonthKeys = monthKeys;
    baseContractRows = buildContractRows(monthKeys);

    renderKpis();
    renderAttentionStrip();
    renderObjectScope();
    renderObjectClientValues(monthKeys);
    renderCurrentContractTable();
    renderImportedRows(monthKeys);
    renderExceptions();
    show(content);

    /* refresh the open drawer with re-fetched data */
    if (selectedSummaryKey) {
      const summary = findSummaryByKey(selectedSummaryKey);
      if (summary) openDrawer(summary);
      else closeDrawer();
    }
  } catch (exception) {
    hide(loading);
    error.replaceChildren();
    const message = document.createElement("span");
    message.textContent = `Nepavyko įkelti šio projekto. ${exception.message}`;
    const retry = document.createElement("button");
    retry.type = "button";
    retry.className = "btn";
    retry.textContent = "Bandyti dar kartą";
    retry.addEventListener("click", () => {
      hide(error);
      show(loading);
      loadProject();
    });
    error.append(message, retry);
    show(error);
  }
}

loadImportStatus();
loadProject();
